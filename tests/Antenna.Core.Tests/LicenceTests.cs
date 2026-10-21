using System.Text.Json;
using Antenna.Core.Licensing;

namespace Antenna.Core.Tests;

/// <summary>licence-fixture.json is made by the site's own signing code (site/scripts/fixture.ts) with a test key.</summary>
public sealed class LicenceTests
{
    private static readonly JsonElement Fixture =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "licence-fixture.json"))).RootElement;

    private static string Key => Fixture.GetProperty("publicKey").GetString()!;

    [Fact]
    public void Reads_a_licence_the_site_signed()
    {
        var licence = LicenceCheck.Read(Fixture.GetProperty("token").GetString(), Key);
        Assert.NotNull(licence);
        Assert.Equal("user_test", licence.UserId);
        Assert.Equal("monthly", licence.Plan);
        Assert.Equal("device-fixture-1", licence.Device);
        Assert.Equal(2_000_000_000, licence.Until);
    }

    [Fact]
    public void Rejects_tampering_wrong_keys_and_junk()
    {
        Assert.Null(LicenceCheck.Read(Fixture.GetProperty("tampered").GetString(), Key));
        using var other = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        Assert.Null(LicenceCheck.Read(Fixture.GetProperty("token").GetString(), Convert.ToBase64String(other.ExportSubjectPublicKeyInfo())));
        Assert.Null(LicenceCheck.Read("not.a.licence", Key));
        Assert.Null(LicenceCheck.Read(null, Key));
    }

    [Fact]
    public void Evaluates_device_dates_and_staleness()
    {
        var licence = LicenceCheck.Read(Fixture.GetProperty("token").GetString(), Key)!;
        var issued = DateTimeOffset.FromUnixTimeSeconds(licence.IssuedAt);
        Assert.Equal(LicenceStatus.Active, LicenceCheck.Evaluate(licence, "device-fixture-1", issued.AddDays(1)));
        Assert.Equal(LicenceStatus.SignedOut, LicenceCheck.Evaluate(licence, "another-device", issued.AddDays(1)));
        Assert.Equal(LicenceStatus.Stale, LicenceCheck.Evaluate(licence, "device-fixture-1", licence.RefreshTime.AddSeconds(1)));
        Assert.Equal(LicenceStatus.Stale, LicenceCheck.Evaluate(licence, "device-fixture-1", issued.AddDays(-30)));
        Assert.Equal(LicenceStatus.Expired, LicenceCheck.Evaluate(licence, "device-fixture-1", licence.UntilTime));
        Assert.Equal(LicenceStatus.SignedOut, LicenceCheck.Evaluate(null, "device-fixture-1", issued));
    }
}
