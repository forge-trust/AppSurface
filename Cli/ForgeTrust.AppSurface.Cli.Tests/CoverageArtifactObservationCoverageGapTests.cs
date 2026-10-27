using System.Text.Encodings.Web;
using System.Text.Json;
using ForgeTrust.AppSurface.Cli;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactObservationCoverageGapTests
{
    private static readonly DateTimeOffset FixedUtc = DateTimeOffset.Parse("2026-10-22T12:00:00Z");

    [Fact]
    public void InspectorEntryLimitReturnsObservedBytesAsALowerBoundAndKeepsThePendingDirectory()
    {
        using var temp = TestDirectory.Create();
        var results = Path.Join(temp.Path, "TestResults");
        Directory.CreateDirectory(results);
        for (var index = 0; index < 3; index++)
            File.WriteAllText(Path.Join(results, $"coverage-{index}.xml"), "data");

        var observation = CoverageArtifactInspector.Inspect(
            temp.Path,
            new MutableClock(FixedUtc),
            TimeSpan.FromSeconds(1),
            maximumEntries: 2,
            CancellationToken.None,
            continuationDirectories: ["TestResults"]);

        Assert.False(observation.Complete);
        Assert.False(observation.Cancelled);
        Assert.Equal(2, observation.Entries);
        Assert.Equal(2, observation.FileCount);
        Assert.Equal(8, observation.Bytes);
        Assert.Empty(observation.Errors);
        Assert.Equal(["TestResults"], observation.ContinuationDirectories);
    }

    [Fact]
    public async Task EmitWithoutStoreUsesLocalCooldownAndDropsSevenDayOldFacts()
    {
        using var temp = TestDirectory.Create();
        var clock = new MutableClock(FixedUtc);
        using var errors = new StringWriter();
        var warnings = new CoverageArtifactWarnings(temp.Path, null, clock, errors);

        await warnings.EmitAsync(Diagnostic("same condition"), "same-condition", 1024);
        await warnings.EmitAsync(Diagnostic("same condition"), "same-condition", 1024);
        Assert.Equal(1, CountCode(errors.ToString(), "ASCOV210"));

        clock.Advance(TimeSpan.FromDays(8));
        await warnings.EmitAsync(Diagnostic("same condition after expiry"), "same-condition", 1024);
        await warnings.EmitAsync(Diagnostic("fresh condition"), "fresh-condition", 1024);
        await warnings.EmitAsync(Diagnostic("fresh condition"), "fresh-condition", 1024);

        Assert.Equal(3, CountCode(errors.ToString(), "ASCOV210"));
        Assert.Contains("same condition after expiry", errors.ToString(), StringComparison.Ordinal);
        Assert.Contains("fresh condition", errors.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("ASCOV201", errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsObservationRejectsNullArgumentElementsAndMissingSplitPropertyValues()
    {
        using var temp = TestDirectory.Create();
        var project = new CoverageRunProject(
            "Sample.csproj",
            Path.Join(temp.Path, "Sample.csproj"),
            "fixture",
            IsExclusive: false);
        const string capabilities = "{\"Properties\":{\"Configuration\":\"Debug\",\"TargetFramework\":\"net10.0\"}}";

        var nullArgument = CoverageArtifactSettings.Observe(project, "net10.0", capabilities, "Debug", new string[] { null! });
        var missingPropertyValue = CoverageArtifactSettings.Observe(project, "net10.0", capabilities, "Debug", ["--property"]);
        var whitespacePropertyValue = CoverageArtifactSettings.Observe(project, "net10.0", capabilities, "Debug", ["--property", " \t"]);

        Assert.Equal("unknown", nullArgument.State);
        Assert.Equal("settings-arguments-over-limit", nullArgument.Cause);
        Assert.Null(nullArgument.EffectivePath);
        Assert.Equal("unknown", missingPropertyValue.State);
        Assert.Equal("settings-override-unusable", missingPropertyValue.Cause);
        Assert.Equal("unknown", whitespacePropertyValue.State);
        Assert.Equal("settings-override-unusable", whitespacePropertyValue.Cause);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("null-scopes")]
    [InlineData("scope-count")]
    public void CursorReadRejectsIncompatibleScopeStateAndPreservesItsBytes(string fault)
    {
        using var temp = TestDirectory.Create();
        using var store = CoverageArtifactStore.Open(Path.Join(temp.Path, "store"));
        var scopes = Enumerable.Range(0, fault == "scope-count" ? 33 : 1)
            .Select(index => new
            {
                ScopeId = index.ToString("x64"),
                UpdatedAtUtc = FixedUtc,
                Paths = new[] { "TestResults" }
            })
            .ToArray();
        var original = JsonSerializer.SerializeToUtf8Bytes(new
        {
            SchemaVersion = fault == "version" ? 2 : 1,
            Scopes = fault == "null-scopes" ? null : scopes
        });
        var statePath = WriteCursorState(store, original);

        var exception = Assert.Throws<InvalidDataException>(() =>
            CoverageArtifactInspectionCursorStore.Read(store, new string('a', 64), new MutableClock(FixedUtc)));

        Assert.Contains("scope count", exception.Message, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllBytes(statePath));
    }

    [Fact]
    public void CursorReadRejectsCanonicalEncodingOverLimitAndPreservesState()
    {
        using var temp = TestDirectory.Create();
        using var store = CoverageArtifactStore.Open(Path.Join(temp.Path, "store"));
        const int scopeCount = 32;
        const int pathsPerScope = 24;
        const int charactersPerPath = 32;
        var repeatedPath = new string('\u00e9', charactersPerPath);
        var scopes = Enumerable.Range(0, scopeCount)
            .Select(index => new
            {
                ScopeId = index.ToString("x64"),
                UpdatedAtUtc = FixedUtc,
                Paths = Enumerable.Repeat(repeatedPath, pathsPerScope).ToArray()
            })
            .ToArray();
        var relaxedOptions = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var original = JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = 1, Scopes = scopes }, relaxedOptions);
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = 1, Scopes = scopes });
        Assert.InRange(original.Length, 1, 64 * 1024);
        Assert.True(canonical.Length > 64 * 1024);
        var statePath = WriteCursorState(store, original);

        var exception = Assert.Throws<InvalidDataException>(() =>
            CoverageArtifactInspectionCursorStore.Read(store, new string('a', 64), new MutableClock(FixedUtc)));

        Assert.Contains("encoded-byte limit", exception.Message, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllBytes(statePath));
    }

    private static string WriteCursorState(CoverageArtifactStore store, byte[] contents)
    {
        var inspectionPath = Path.Join(store.RootPath, "staging", "inspection");
        using var inspection = CoverageRunOutputLease.AcquireArtifactDirectory(inspectionPath, true, true)!;
        using (var state = inspection.OpenArtifactFile("state.json", createNew: true))
            state.Write(contents);
        return Path.Join(inspectionPath, "state.json");
    }

    private static CoverageArtifactDiagnostic Diagnostic(string cause) =>
        new("ASCOV210", "storage-growth", cause, "Inspect coverage storage status.");

    private static int CountCode(string text, string code) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Count(line => line.StartsWith(code + " ", StringComparison.Ordinal));

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
        public override long GetTimestamp() => 0;

        internal void Advance(TimeSpan duration) => _now += duration;
    }
}
