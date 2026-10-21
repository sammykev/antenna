using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Antenna.Core.Licensing;

/// <summary>
/// A licence signed by the Antenna site (site/lib/licence.ts): "base64url(JSON).base64url(ECDSA P-256 SHA-256 DER
/// signature)" over the first part. The Android app checks the same format (licence/Licence.kt).
/// </summary>
public sealed record LicencePayload(
    [property: JsonPropertyName("v")] int Version,
    [property: JsonPropertyName("sub")] string UserId,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("plan")] string Plan,
    [property: JsonPropertyName("until")] long Until,
    [property: JsonPropertyName("refresh")] long RefreshBy,
    [property: JsonPropertyName("device")] string Device,
    [property: JsonPropertyName("iat")] long IssuedAt)
{
    public DateTimeOffset UntilTime => DateTimeOffset.FromUnixTimeSeconds(Until);
    public DateTimeOffset RefreshTime => DateTimeOffset.FromUnixTimeSeconds(RefreshBy);
}

public enum LicenceStatus
{
    /// <summary>No licence: the app must be signed in.</summary>
    SignedOut,

    /// <summary>Trial or plan running.</summary>
    Active,

    /// <summary>Trial or plan over: the user has to subscribe.</summary>
    Expired,

    /// <summary>Not refreshed for too long (offline): connect once to carry on.</summary>
    Stale,
}

public static class LicenceCheck
{
    /// <summary>The payload if the signature is good, whatever the dates say; null for anything else.</summary>
    public static LicencePayload? Read(string? token, string publicKeyBase64)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(publicKeyBase64))
            return null;
        var parts = token.Split('.');
        if (parts.Length != 2)
            return null;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            if (!key.VerifyData(Encoding.ASCII.GetBytes(parts[0]), FromBase64Url(parts[1]), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
                return null;
            var payload = JsonSerializer.Deserialize<LicencePayload>(FromBase64Url(parts[0]));
            return payload is { Version: 1 } && !string.IsNullOrEmpty(payload.UserId) && !string.IsNullOrEmpty(payload.Device) ? payload : null;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException)
        {
            return null;
        }
    }

    /// <summary>What the licence allows on this device now.</summary>
    public static LicenceStatus Evaluate(LicencePayload? licence, string deviceId, DateTimeOffset now)
    {
        if (licence is null || licence.Device != deviceId)
            return LicenceStatus.SignedOut;
        if (now >= licence.UntilTime)
            return LicenceStatus.Expired;
        // A clock set far back can't stretch a licence: it's no good before it was issued.
        if (now >= licence.RefreshTime || now < DateTimeOffset.FromUnixTimeSeconds(licence.IssuedAt).AddDays(-1))
            return LicenceStatus.Stale;
        return LicenceStatus.Active;
    }

    private static byte[] FromBase64Url(string s)
    {
        var b = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(b.PadRight(b.Length + (4 - b.Length % 4) % 4, '='));
    }
}

public sealed class LicenceServerException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Talks to the Antenna site: device sign-in with a code, and licence refreshes.</summary>
public sealed class LicenceClient(HttpClient http, string server)
{
    public sealed record SignInStart(string DeviceCode, string UserCode, int ExpiresIn, int Interval);

    private readonly string _server = server.TrimEnd('/');

    public string LinkUrl(string userCode) => $"{_server}/link?code={Uri.EscapeDataString(userCode)}";

    public string PricingUrl => $"{_server}/pricing";

    public string AccountUrl => $"{_server}/account";

    public async Task<SignInStart> StartAsync(string deviceId, string name, string platform, CancellationToken ct = default)
    {
        var doc = await PostAsync("/api/device/start", new { deviceId, name, platform }, ct);
        return new SignInStart(Str(doc, "deviceCode"), Str(doc, "userCode"), Int(doc, "expiresIn", 600), Int(doc, "interval", 5));
    }

    /// <summary>The licence once the user has entered the code, null while still waiting.</summary>
    public async Task<string?> PollAsync(string deviceCode, CancellationToken ct = default)
    {
        var doc = await PostAsync("/api/device/poll", new { deviceCode }, ct);
        return doc.RootElement.TryGetProperty("token", out var t) ? t.GetString() : null;
    }

    public async Task<string> RefreshAsync(string token, CancellationToken ct = default) =>
        Str(await PostAsync("/api/licence/refresh", new { token }, ct), "token");

    private async Task<JsonDocument> PostAsync(string path, object body, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(_server + path, body, ct);
        var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (doc.RootElement.TryGetProperty("error", out var error))
            throw new LicenceServerException(error.GetString() ?? "error", error.GetString() switch
            {
                "expired" => "The sign-in code expired. Get a new one.",
                "signed_out" => "This device was signed out of your account.",
                "too_many_devices" => "Your account is signed in on too many devices.",
                var other => $"The Antenna server said: {other}",
            });
        response.EnsureSuccessStatusCode();
        return doc;
    }

    private static string Str(JsonDocument d, string name) =>
        d.RootElement.TryGetProperty(name, out var v) && v.GetString() is { } s ? s : throw new LicenceServerException("bad_response", "Unexpected answer from the Antenna server.");

    private static int Int(JsonDocument d, string name, int fallback) =>
        d.RootElement.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : fallback;
}
