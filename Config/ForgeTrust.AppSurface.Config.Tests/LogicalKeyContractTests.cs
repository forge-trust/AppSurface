namespace ForgeTrust.AppSurface.Config.Tests;

public sealed class LogicalKeyContractTests
{
    [Fact]
    public void Parse_PreservesColonSegmentsAndLiteralPunctuation()
    {
        var key = AppSurfaceConfigKey.Parse("Logging:LogLevel:Microsoft.Hosting.Lifetime");

        Assert.Equal("Logging:LogLevel:Microsoft.Hosting.Lifetime", key.Value);
        Assert.Equal(
            new[] { "Logging", "LogLevel", "Microsoft.Hosting.Lifetime" },
            key.Segments);
        Assert.Equal(key.Value, key.ToString());
    }

    [Theory]
    [InlineData("Payments:ApiKey")]
    [InlineData("Payments:Api-Key")]
    [InlineData("Payments:Api_Key")]
    [InlineData("Payments:Api/Key")]
    [InlineData(@"Payments:Api\Key")]
    [InlineData("Payments:Api Key")]
    [InlineData("Πληρωμές:ключ")]
    public void TryParse_AcceptsLiteralNonControlContent(string value)
    {
        Assert.True(AppSurfaceConfigKey.TryParse(value, out var key));
        Assert.NotNull(key);
        Assert.Equal(value, key!.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(":A")]
    [InlineData("A:")]
    [InlineData("A::B")]
    [InlineData(" A")]
    [InlineData("A ")]
    [InlineData("A: B")]
    [InlineData("A:B ")]
    [InlineData("A:\u0000B")]
    [InlineData("A:\nB")]
    public void TryParse_RejectsMalformedInputWithoutPartialOutput(string? value)
    {
        Assert.False(AppSurfaceConfigKey.TryParse(value, out var key));
        Assert.Null(key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(":A")]
    [InlineData("A:")]
    [InlineData("A::B")]
    [InlineData(" A")]
    [InlineData("A:\u0001B")]
    public void Parse_ThrowsFormatExceptionForMalformedInput(string? value)
    {
        Assert.Throws<FormatException>(() => AppSurfaceConfigKey.Parse(value!));
    }

    [Fact]
    public void FromSegments_CopiesInputAndJoinsWithoutNormalization()
    {
        var segments = new[] { "Payments", "Microsoft.Hosting.Lifetime", "Api-Key" };

        var key = AppSurfaceConfigKey.FromSegments(segments);
        segments[0] = "Changed";

        Assert.Equal("Payments:Microsoft.Hosting.Lifetime:Api-Key", key.Value);
        Assert.Equal(
            new[] { "Payments", "Microsoft.Hosting.Lifetime", "Api-Key" },
            key.Segments);
        Assert.False(key.Segments.IsDefault);
    }

    [Fact]
    public void FromSegments_RejectsNullArrayAndEmptyArray()
    {
        var nullError = Assert.Throws<ArgumentNullException>(() =>
            AppSurfaceConfigKey.FromSegments((string[]?)null!));
        Assert.Equal("segments", nullError.ParamName);

        var emptyError = Assert.Throws<ArgumentException>(() =>
            AppSurfaceConfigKey.FromSegments());
        Assert.Equal("segments", emptyError.ParamName);
    }

    [Theory]
    [InlineData(0, "A", "")]
    [InlineData(1, "A", "B:C")]
    [InlineData(2, "A", " B")]
    [InlineData(3, "A", "B\u0001")]
    public void FromSegments_ReportsFailingMemberIndex(int index, string first, string invalid)
    {
        var segments = new[] { first, "B", "C", "D" };
        segments[index] = invalid;

        var error = Assert.Throws<ArgumentException>(() => AppSurfaceConfigKey.FromSegments(segments));

        Assert.Equal("segments", error.ParamName);
        Assert.Contains($"index {index}", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EqualityHashingAndObjectEquality_AreOrdinalIgnoreCase()
    {
        var upper = AppSurfaceConfigKey.Parse("Payments:ApiKey");
        var lower = AppSurfaceConfigKey.Parse("payments:apikey");

        Assert.True(upper.Equals(lower));
        Assert.True(upper.Equals((object)lower));
        Assert.Equal(upper.GetHashCode(), lower.GetHashCode());
        Assert.NotEqual(upper.Value, lower.Value);
        Assert.False(upper.Equals(null));
        Assert.False(upper.Equals("Payments:ApiKey"));
    }

    [Fact]
    public void Equality_DoesNotNormalizeSeparatorsOrUnicode()
    {
        Assert.NotEqual(
            AppSurfaceConfigKey.Parse("Payments:Api-Key"),
            AppSurfaceConfigKey.Parse("Payments:Api:Key"));
        Assert.NotEqual(
            AppSurfaceConfigKey.Parse("A\u00E9"),
            AppSurfaceConfigKey.Parse("Ae\u0301"));
    }

    [Fact]
    public void Ancestry_IsSegmentAwareAndCaseInsensitive()
    {
        var prefix = AppSurfaceConfigKey.Parse("Payments");
        Assert.True(prefix.IsSameOrDescendantOf(prefix));
        Assert.True(AppSurfaceConfigKey.Parse("payments:ApiKey").IsSameOrDescendantOf(prefix));
        Assert.True(AppSurfaceConfigKey.Parse("Payments:Nested:Value").IsSameOrDescendantOf(prefix));
        Assert.False(AppSurfaceConfigKey.Parse("PaymentsArchive:ApiKey").IsSameOrDescendantOf(prefix));
        Assert.False(AppSurfaceConfigKey.Parse("Payments-Archive:ApiKey").IsSameOrDescendantOf(prefix));
        Assert.False(AppSurfaceConfigKey.Parse("Pay").IsSameOrDescendantOf(prefix));
    }

    [Fact]
    public void Ancestry_RejectsNullPrefix()
    {
        Assert.Throws<ArgumentNullException>(() =>
            AppSurfaceConfigKey.Parse("A").IsSameOrDescendantOf(null!));
    }

    [Fact]
    public void MissingFactory_ProducesOnlyMissingState()
    {
        var result = ConfigProviderValueResult<string>.Missing();

        Assert.Equal(ConfigProviderValueStatus.Missing, result.Status);
        Assert.Null(result.Value);
        Assert.Null(result.Diagnostic);
        Assert.Empty(result.Notices);
    }

    [Fact]
    public void FoundFactory_PreservesValueIncludingValueTypeDefaultAndCopiesNotices()
    {
        var notice = new ConfigProviderNotice("legacy", "Problem", "Cause", "Fix", "Docs", retryable: true);
        var notices = new[] { notice };

        var result = ConfigProviderValueResult<int>.Found(0, notices);
        notices[0] = new ConfigProviderNotice("other", "Problem", "Cause", "Fix", "Docs", retryable: false);

        Assert.Equal(ConfigProviderValueStatus.Found, result.Status);
        Assert.Equal(0, result.Value);
        Assert.Null(result.Diagnostic);
        Assert.Single(result.Notices);
        Assert.Same(notice, result.Notices[0]);
        Assert.True(notice.Retryable);
    }

    [Fact]
    public void FoundFactory_RejectsNullValueArrayAndMember()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ConfigProviderValueResult<string>.Found(null!));

        Assert.Throws<ArgumentNullException>(() =>
            ConfigProviderValueResult<string>.Found("value", (ConfigProviderNotice[]?)null!));

        Assert.Throws<ArgumentNullException>(() =>
            ConfigProviderValueResult<string>.Found(
                "value",
                new ConfigProviderNotice[] { null! }));
    }

    [Fact]
    public void TerminalFactory_ProducesOnlyTerminalState()
    {
        var diagnostic = CreateDiagnostic();
        var result = ConfigProviderValueResult<string>.Terminal(diagnostic);

        Assert.Equal(ConfigProviderValueStatus.Terminal, result.Status);
        Assert.Null(result.Value);
        Assert.Same(diagnostic, result.Diagnostic);
        Assert.Empty(result.Notices);
    }

    [Fact]
    public void TerminalFactory_RejectsNullDiagnostic()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ConfigProviderValueResult<string>.Terminal(null!));
    }

    [Fact]
    public void PatchFactories_EnforceNotAppliedAppliedAndTerminalInvariants()
    {
        var notApplied = ConfigPatchResult<string>.NotApplied();
        Assert.Equal(ConfigPatchStatus.NotApplied, notApplied.Status);
        Assert.Null(notApplied.Value);
        Assert.Null(notApplied.Diagnostic);

        var applied = ConfigPatchResult<int>.Applied(0);
        Assert.Equal(ConfigPatchStatus.Applied, applied.Status);
        Assert.Equal(0, applied.Value);
        Assert.Null(applied.Diagnostic);

        var diagnostic = CreateDiagnostic();
        var terminal = ConfigPatchResult<string>.Terminal(diagnostic);
        Assert.Equal(ConfigPatchStatus.Terminal, terminal.Status);
        Assert.Null(terminal.Value);
        Assert.Same(diagnostic, terminal.Diagnostic);
    }

    [Fact]
    public void PatchFactories_RejectNullAppliedValueAndDiagnostic()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ConfigPatchResult<string>.Applied(null!));
        Assert.Throws<ArgumentNullException>(() =>
            ConfigPatchResult<string>.Terminal(null!));
    }

    private static ConfigProviderTerminalDiagnostic CreateDiagnostic() =>
        new(
            "config-key-collision",
            "Configuration key collision.",
            "Two source spellings identify one logical key.",
            "Remove the duplicate spelling.",
            "config-logical-key-contract",
            retryable: false);
}

