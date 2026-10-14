using System.Globalization;
using System.IO.Compression;
using System.Xml;

namespace Antenna.Core.Epg;

public sealed record XmltvChannel(string Id, IReadOnlyList<string> DisplayNames);

/// <summary>
/// Streaming XMLTV reader. Guides from IPTV providers run to hundreds of megabytes,
/// so nothing is held in memory beyond the channel list: programmes are pushed to a
/// callback as they are read.
/// </summary>
public static class XmltvParser
{
    /// <param name="channelsReady">
    /// Called once with every &lt;channel&gt; before the first programme (XMLTV lists channels first).
    /// Returns a filter: only programmes whose channel id it accepts are reported.
    /// </param>
    /// <param name="onProgramme">Receives each accepted programme overlapping [from, to).</param>
    public static async Task ParseAsync(
        Stream stream,
        Func<IReadOnlyList<XmltvChannel>, Func<string, bool>> channelsReady,
        Action<Programme> onProgramme,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default)
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Ignore, // Most guides reference xmltv.dtd.
            XmlResolver = null,
            CheckCharacters = false, // Provider guides often contain stray control characters.
            IgnoreComments = true,
            IgnoreWhitespace = true,
        };

        var channels = new List<XmltvChannel>();
        Func<string, bool>? accept = null;

        using var reader = XmlReader.Create(stream, settings);
        // Skip() already moves past the element, so only Read() when the last step didn't.
        var advance = true;
        while (true)
        {
            if (advance && !await reader.ReadAsync())
                break;
            advance = true;
            if (reader.EOF)
                break;
            ct.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element)
                continue;

            if (reader.LocalName == "channel")
            {
                var id = reader.GetAttribute("id");
                var names = await ReadChannelNamesAsync(reader);
                if (!string.IsNullOrWhiteSpace(id))
                    channels.Add(new XmltvChannel(id.Trim(), names));
            }
            else if (reader.LocalName == "programme")
            {
                accept ??= channelsReady(channels);

                var channel = reader.GetAttribute("channel")?.Trim();
                var start = ParseTime(reader.GetAttribute("start"));
                var stop = ParseTime(reader.GetAttribute("stop"));

                if (channel is null || start is null || !accept(channel) || start >= to || (stop ?? start) < from)
                {
                    await reader.SkipAsync();
                    advance = false;
                    continue;
                }

                var (title, desc) = await ReadProgrammeTextAsync(reader);
                onProgramme(new Programme
                {
                    ChannelId = channel,
                    Start = start.Value,
                    // A missing stop time is legal; assume the slot lasts until the next one would start.
                    Stop = stop ?? start.Value.AddMinutes(30),
                    Title = title ?? "",
                    Description = desc,
                });
            }
        }

        accept ??= channelsReady(channels);
    }

    /// <summary>Opens a guide from a URL or local path, transparently un-gzipping .xml.gz files.</summary>
    public static async Task<Stream> OpenAsync(HttpClient http, string location, CancellationToken ct = default)
    {
        Stream raw;
        if (Uri.TryCreate(location, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            // Spool to a temp file: the network stream isn't seekable and guides can be large.
            var temp = Path.Combine(Path.GetTempPath(), $"antenna-epg-{Guid.NewGuid():N}.tmp");
            var file = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            try
            {
                using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                await using (var body = await response.Content.ReadAsStreamAsync(ct))
                    await body.CopyToAsync(file, ct);
                file.Position = 0;
            }
            catch
            {
                await file.DisposeAsync();
                throw;
            }
            raw = file;
        }
        else
        {
            var path = uri is { IsFile: true } ? uri.LocalPath : location;
            raw = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        }

        var magic = new byte[2];
        var read = await raw.ReadAsync(magic, ct);
        raw.Position = 0;
        return read == 2 && magic[0] == 0x1F && magic[1] == 0x8B ? new GZipStream(raw, CompressionMode.Decompress) : raw;
    }

    /// <summary>Parses XMLTV times: "20261014200000 +0100", "20261014200000", or with fewer digits.</summary>
    internal static DateTimeOffset? ParseTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var parts = value.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var digits = parts[0];
        if (digits.Length < 8 || !digits.All(char.IsAsciiDigit))
            return null;
        digits = digits.PadRight(14, '0')[..14];

        if (!DateTime.TryParseExact(digits, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return null;

        var offset = TimeSpan.Zero;
        if (parts.Length == 2)
        {
            var z = parts[1].Trim();
            if (z.Length == 5 && (z[0] == '+' || z[0] == '-') && int.TryParse(z.AsSpan(1, 2), out var h) && int.TryParse(z.AsSpan(3, 2), out var m))
                offset = (z[0] == '-' ? -1 : 1) * new TimeSpan(h, m, 0);
        }
        return new DateTimeOffset(local, offset);
    }

    private static async Task<IReadOnlyList<string>> ReadChannelNamesAsync(XmlReader reader)
    {
        var names = new List<string>();
        await ForEachChildAsync(reader, async child =>
        {
            if (child.LocalName != "display-name")
                return false;
            var name = (await child.ReadElementContentAsStringAsync()).Trim();
            if (name.Length > 0)
                names.Add(name);
            return true;
        });
        return names;
    }

    private static async Task<(string? Title, string? Desc)> ReadProgrammeTextAsync(XmlReader reader)
    {
        string? title = null, desc = null;
        await ForEachChildAsync(reader, async child =>
        {
            if (child.LocalName is not ("title" or "desc"))
                return false;
            var isTitle = child.LocalName == "title";
            var text = (await child.ReadElementContentAsStringAsync()).Trim();
            // Keep the first of each; extra ones are other languages.
            if (isTitle) title ??= text;
            else desc ??= text.Length > 1000 ? text[..1000] + "…" : text;
            return true;
        });
        return (title, desc);
    }

    /// <summary>
    /// Visits each child element of the current element. The handler returns true if it
    /// consumed the element (e.g. ReadElementContent), otherwise the child is skipped.
    /// Leaves the reader on the parent's end tag.
    /// </summary>
    private static async Task ForEachChildAsync(XmlReader reader, Func<XmlReader, Task<bool>> handle)
    {
        if (reader.IsEmptyElement)
            return;

        var depth = reader.Depth;
        await reader.ReadAsync();
        while (!reader.EOF && reader.Depth > depth)
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (!await handle(reader))
                    await reader.SkipAsync();
            }
            else
            {
                await reader.ReadAsync();
            }
        }
    }
}
