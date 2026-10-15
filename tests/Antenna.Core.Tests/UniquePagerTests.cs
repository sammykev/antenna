using Antenna.Core.Library;

namespace Antenna.Core.Tests;

public sealed class UniquePagerTests
{
    [Fact]
    public async Task Skips_duplicates_and_returns_full_pages_until_the_end()
    {
        // 25 items, every third a duplicate of the one before ("a2" repeats "a1"…), keys by number/3.

        var source = Enumerable.Range(0, 25).Select(i => (Id: i, Key: i % 3 == 2 ? $"k{i - 1}" : $"k{i}")).ToList();
        var fetches = 0;
        var pager = new UniquePager<(int Id, string Key)>((offset, count) =>
        {
            fetches++;
            return Task.FromResult<IReadOnlyList<(int, string)>>(source.Skip(offset).Take(count).ToList());
        }, x => x.Key);

        var pages = new List<IReadOnlyList<(int Id, string Key)>>();
        while (true)
        {
            var page = await pager.NextAsync(5);
            pages.Add(page);
            if (page.Count < 5)
                break;
        }

        var shown = pages.SelectMany(p => p).ToList();
        Assert.All(pages.SkipLast(1), p => Assert.Equal(5, p.Count)); // Full pages until the last.
        Assert.Equal(shown.Count, shown.Select(x => x.Key).Distinct().Count()); // No duplicates.
        Assert.Equal(17, shown.Count); // 25 items minus the 8 duplicates.
        Assert.Equal(shown.OrderBy(x => x.Id), shown); // Order kept.
        Assert.True(fetches >= 6);

    }

    [Fact]
    public async Task Empty_keys_are_never_merged()
    {
        var pager = new UniquePager<string>((o, c) => Task.FromResult<IReadOnlyList<string>>(o == 0 ? ["", "", "x", "x"] : []), s => s);
        Assert.Equal(["", "", "x"], await pager.NextAsync(10));
    }
}
