using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config.Tests;

public class EnvironmentConfigCodecTests
{
    [Theory]
    [InlineData("Payments:ApiKey", "PAYMENTS__APIKEY")]
    [InlineData("a.b:c-d:e_f:g/h:i\\j:09", "A.B__C-D__E_F__G/H__I\\J__09")]
    public void CanonicalGrammarPreservesEveryLiteralSeparator(string key, string expected)
    {
        Assert.Equal(expected, EnvironmentConfigCodec.Encode(AppSurfaceConfigKey.Parse(key)));
        Assert.True(EnvironmentConfigCodec.TryEncode(AppSurfaceConfigKey.Parse(key), out var encoded));
        Assert.Equal(expected, encoded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("_a")]
    [InlineData("a_")]
    [InlineData("a__b")]
    [InlineData("é")]
    [InlineData("a b")]
    [InlineData("a:b")]
    [InlineData("a=b")]
    public void SegmentGrammarRejectsLossyAndUnsupportedEncodings(string segment)
    {
        Assert.Throws<ArgumentException>(() => EnvironmentConfigCodec.EncodeSegment(segment));
        if (AppSurfaceConfigKey.TryParse(segment, out var key) && !segment.Contains(':'))
        {
            Assert.False(EnvironmentConfigCodec.TryEncode(key, out _));
            Assert.Throws<ArgumentException>(() => EnvironmentConfigCodec.Encode(key));
            Assert.Throws<InvalidOperationException>(() => EnvironmentConfigCodec.Candidates(new("Production", key), new Dictionary<AppSurfaceConfigKey, string>()).ToArray());
        }
    }

    [Fact]
    public void GeneratedAcceptedKeysNeverCollapseUnequalIdentities()
    {
        var segments = new[] { "a", "A", "z", "0", "a_b", "a.b", "a-b", "a/b", "a\\b", "_a", "a_", "a__b", "é" };
        var claims = new Dictionary<string, AppSurfaceConfigKey>(StringComparer.OrdinalIgnoreCase);
        foreach (var first in segments)
            foreach (var second in segments)
                foreach (var third in new[] { "x", "x_y", "_x", "x_" })
                {
                    var key = AppSurfaceConfigKey.FromSegments(first, second, third);
                    if (!EnvironmentConfigCodec.TryEncode(key, out var native)) continue;
                    if (claims.TryGetValue(native, out var owner)) Assert.Equal(owner, key);
                    else claims.Add(native, key);
                }
        Assert.NotEmpty(claims);
        Assert.Equal("A_B", EnvironmentConfigCodec.EncodeSegment("a_b"));
    }

    [Theory]
    [InlineData((int)ConfigKeyInputOrigin.Typed, "Payments:ApiKey", null, "scoped/PRODUCTION__PAYMENTS__APIKEY/False|unscoped/PAYMENTS__APIKEY/False")]
    [InlineData((int)ConfigKeyInputOrigin.StrictString, "Payments:ApiKey", "Payments:ApiKey", "scoped/PRODUCTION__PAYMENTS__APIKEY/False|unscoped/PAYMENTS__APIKEY/False|scoped/PRODUCTION_PAYMENTS:APIKEY/True|scoped/PRODUCTION__PAYMENTS:APIKEY/True|unscoped/PAYMENTS:APIKEY/True")]
    [InlineData((int)ConfigKeyInputOrigin.TranslatedDot, "Payments:ApiKey", "Payments.ApiKey", "scoped/PRODUCTION__PAYMENTS__APIKEY/False|unscoped/PAYMENTS__APIKEY/False|scoped/PRODUCTION_PAYMENTS_APIKEY/True|unscoped/PAYMENTS_APIKEY/True")]
    [InlineData((int)ConfigKeyInputOrigin.StrictString, "A", "A", "scoped/PRODUCTION__A/False|unscoped/A/False|scoped/PRODUCTION_A/True")]
    [InlineData((int)ConfigKeyInputOrigin.StrictString, "A-B", "A-B", "scoped/PRODUCTION__A-B/False|unscoped/A-B/False|scoped/PRODUCTION_A_B/True|scoped/PRODUCTION__A_B/True|unscoped/A_B/True")]
    [InlineData((int)ConfigKeyInputOrigin.StrictString, "A", null, "scoped/PRODUCTION__A/False|unscoped/A/False")]
    public void AliasTablesAreExactAndDeduplicateNativeNames(int origin, string value, string? input, string expected)
    {
        var key = AppSurfaceConfigKey.Parse(value).WithInput((ConfigKeyInputOrigin)origin, input);
        var candidates = EnvironmentConfigCodec.Candidates(new("Production", key), new Dictionary<AppSurfaceConfigKey, string>()).ToArray();
        Assert.Equal(expected, string.Join('|', candidates.Select(candidate => $"{candidate.Layer}/{candidate.Name}/{candidate.Legacy}")));
    }

    [Fact]
    public void MappingReplacesEveryCanonicalAndAliasCandidateWithExactSuffix()
    {
        var key = AppSurfaceConfigKey.Parse("Pay:Key").WithInput(ConfigKeyInputOrigin.TranslatedDot, "Pay.Key");
        var candidates = EnvironmentConfigCodec.Candidates(new("Dev-Us.East:Blue", key), new Dictionary<AppSurfaceConfigKey, string> { [key] = "Pay_Key" }).ToArray();
        Assert.Equal(new[] { ("scoped", "DEV_US_EAST_BLUE__Pay_Key", false), ("unscoped", "Pay_Key", false) }, candidates);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("_A")]
    [InlineData("A_")]
    [InlineData("A__B")]
    [InlineData("A-B")]
    [InlineData("A.B")]
    [InlineData("é")]
    [InlineData("A\nB")]
    public void ExplicitSuffixGrammarRejectsAmbiguity(string? suffix) =>
        Assert.Throws<ArgumentException>(() => new AppSurfaceEnvironmentConfigOptions().MapKey("A", suffix!));

    [Fact]
    public void OptionsFreezeAndValidateDuplicateAndCanonicalClaims()
    {
        var options = new AppSurfaceEnvironmentConfigOptions().MapKey("A:B", "a_B9");
        var snapshot = options.Snapshot();
        options.MapKey("C", "C");
        Assert.Single(snapshot);
        Assert.Equal("a_B9", snapshot[AppSurfaceConfigKey.Parse("a:b")]);
        Assert.Throws<ArgumentNullException>(() => options.MapKey((AppSurfaceConfigKey)null!, "A"));
        Assert.Throws<FormatException>(() => options.MapKey("A::B", "A"));
        Assert.Throws<OptionsValidationException>(() => new AppSurfaceEnvironmentConfigOptions().MapKey("A", "X").MapKey("a", "Y").Snapshot());
        Assert.Throws<OptionsValidationException>(() => new AppSurfaceEnvironmentConfigOptions().MapKey("A", "X").MapKey("B", "x").Snapshot());
        Assert.Throws<OptionsValidationException>(() => new AppSurfaceEnvironmentConfigOptions().MapKey("A", "B").MapKey("B", "C").Snapshot());
        Assert.Throws<OptionsValidationException>(() => new AppSurfaceEnvironmentConfigOptions { MaxAdHocClaims = 0 }.Snapshot());
        Assert.Single(new AppSurfaceEnvironmentConfigOptions().MapKey("é", "UNICODE").Snapshot());
    }
}
