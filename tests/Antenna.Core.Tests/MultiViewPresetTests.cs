using Antenna.Core.Library;

namespace Antenna.Core.Tests;

public sealed class MultiViewPresetTests
{
    private static MultiViewPreset Football(string name = "Saturday football") => new(name, "grid", 1,
    [
        new PresetTile("a", "10", "Sky Sports Main Event"),
        new PresetTile("b", "20", "TNT Sports 1"),
        null,
        new PresetTile("a", "11", "Sky Sports Football"),
    ]);

    [Fact]
    public void Round_trips_with_empty_tiles()
    {
        var json = MultiViewPresets.Serialize([Football()]);
        var back = Assert.Single(MultiViewPresets.Parse(json));

        Assert.Equal("Saturday football", back.Name);
        Assert.Equal("grid", back.Layout);
        Assert.Equal(1, back.MainTile);
        Assert.Equal(Football().Tiles, back.Tiles);
    }

    [Fact]
    public void Saving_with_an_existing_name_replaces_it_and_delete_removes_it()
    {
        IReadOnlyList<MultiViewPreset> presets = [Football(), Football("Tennis")];
        presets = MultiViewPresets.Save(presets, Football("saturday FOOTBALL") with { Layout = "spot" });

        Assert.Equal(2, presets.Count);
        Assert.Equal("spot", presets.Single(p => p.Name.Equals("saturday football", StringComparison.OrdinalIgnoreCase)).Layout);

        presets = MultiViewPresets.Delete(presets, "tennis");
        Assert.Equal(["saturday FOOTBALL"], presets.Select(p => p.Name));
    }

    [Fact]
    public void Sorted_by_name_and_tolerant_of_bad_json()
    {
        var json = MultiViewPresets.Serialize([Football("Zebra"), Football("apple")]);
        Assert.Equal(["apple", "Zebra"], MultiViewPresets.Parse(json).Select(p => p.Name));
        Assert.Empty(MultiViewPresets.Parse("{not json"));
        Assert.Empty(MultiViewPresets.Parse(null));
    }
}
