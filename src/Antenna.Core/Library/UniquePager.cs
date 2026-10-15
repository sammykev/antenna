namespace Antenna.Core.Library;

/// <summary>
/// Pages through a list while skipping items whose key has already been seen (e.g. the same channel
/// from a second playlist). Keeps its own position in the source, and returns exactly a full page
/// while more remain, so a short page reliably means the end.
/// </summary>
public sealed class UniquePager<T>(Func<int, int, Task<IReadOnlyList<T>>> fetch, Func<T, string> key)
{
    private readonly HashSet<string> _seen = [];
    private readonly List<T> _pending = [];
    private int _offset;
    private bool _exhausted;

    public async Task<IReadOnlyList<T>> NextAsync(int count)
    {
        while (_pending.Count < count && !_exhausted)
        {
            var page = await fetch(_offset, count);
            _offset += page.Count;
            _exhausted = page.Count < count;
            foreach (var item in page)
            {
                var k = key(item);
                if (k.Length == 0 || _seen.Add(k))
                    _pending.Add(item);
            }
        }
        var take = _pending.Take(count).ToList();
        _pending.RemoveRange(0, take.Count);
        return take;
    }
}
