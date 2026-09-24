using FakeItEasy;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config.Tests;

public sealed class LogicalKeyFoundationTests
{
    [Fact]
    public void AncestryNeverTreatsAParentAsItsChild()
    {
        var parent = AppSurfaceConfigKey.Parse("Payments");
        var child = AppSurfaceConfigKey.Parse("Payments:ApiKey");
        Assert.False(parent.IsSameOrDescendantOf(child));
        Assert.True(child.IsSameOrDescendantOf(parent));
    }

    [Fact]
    public void ConfigPackageGuard_ValidatesTheCurrentPublicContract()
    {
        ConfigPackageCompatibility.ValidateAssemblies([typeof(IConfigProvider).Assembly]);
        Assert.Throws<ArgumentNullException>(() => ConfigPackageCompatibility.ValidateAssemblies(null!));
    }

    [Theory]
    [InlineData(LegacyDotPathBehavior.Strict, "A.B", "A.B", (int)ConfigKeyInputOrigin.StrictString)]
    [InlineData(LegacyDotPathBehavior.TranslateDotOnlyWithDiagnostic, "A.B", "A:B", (int)ConfigKeyInputOrigin.TranslatedDot)]
    [InlineData(LegacyDotPathBehavior.TranslateDotOnlyWithDiagnostic, "A:B.C", "A:B.C", (int)ConfigKeyInputOrigin.StrictString)]
    [InlineData(LegacyDotPathBehavior.TranslateDotOnlyWithDiagnostic, "A", "A", (int)ConfigKeyInputOrigin.StrictString)]
    public void Parser_PreservesSingleIdentityAndInputOrigin(LegacyDotPathBehavior behavior, string input,
        string expected, int origin)
    {
        var parser = new ConfigKeyInputParser(Options.Create(new AppSurfaceConfigKeyOptions { LegacyDotPathBehavior = behavior }));
        var key = parser.Parse(input);
        Assert.Equal(expected, key.Value);
        Assert.Equal((ConfigKeyInputOrigin)origin, key.InputOrigin);
        Assert.Equal(input, key.OriginalInput);
        Assert.Equal(AppSurfaceConfigKey.Parse(expected), key);
        Assert.Equal(AppSurfaceConfigKey.Parse(expected).GetHashCode(), key.GetHashCode());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("A..B")]
    [InlineData("A::B")]
    [InlineData(" A")]
    public void Parser_InvalidInputIsAnApplicationArgumentFailure(string? input)
    {
        var parser = new ConfigKeyInputParser(Options.Create(new AppSurfaceConfigKeyOptions()));
        var error = Assert.Throws<ArgumentException>(() => parser.Parse(input!));
        Assert.Equal("key", error.ParamName);
        Assert.Contains("config-key-invalid", error.Message);
    }

    [Fact]
    public void Parser_SnapshotsOptionsAndRejectsUnknownMode()
    {
        var options = new AppSurfaceConfigKeyOptions();
        var parser = new ConfigKeyInputParser(Options.Create(options));
        options.LegacyDotPathBehavior = LegacyDotPathBehavior.Strict;
        Assert.Equal("A:B", parser.Parse("A.B").Value);
        Assert.Throws<ArgumentNullException>(() => new ConfigKeyInputParser(null!));
        options.LegacyDotPathBehavior = (LegacyDotPathBehavior)99;
        Assert.Throws<OptionsValidationException>(() => new ConfigKeyInputParser(Options.Create(options)));
    }

    [Fact]
    public void Manager_FoundDefaultValueDoesNotFallThrough()
    {
        var environment = new OutcomeEnvironment(ConfigProviderValueStatus.Found, 0);
        var lower = new OutcomeProvider(ConfigProviderValueStatus.Found, 7);
        Assert.Equal(0, Manager(environment, lower).GetValue<int>("Production", AppSurfaceConfigKey.Parse("Flag")));
        Assert.Equal(0, lower.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Manager_TerminalSuppressesLowerProvider(bool environmentTerminal)
    {
        var environment = new OutcomeEnvironment(environmentTerminal ? ConfigProviderValueStatus.Terminal : ConfigProviderValueStatus.Missing);
        var terminal = new OutcomeProvider(ConfigProviderValueStatus.Terminal);
        var lower = new OutcomeProvider(ConfigProviderValueStatus.Found, "unsafe-fallback") { Priority = 0 };
        var error = Assert.Throws<ConfigurationResolutionException>(() =>
            Manager(environment, terminal, lower).GetValue<string>("Production", "Payments:ApiKey"));
        Assert.Equal("config-key-collision", error.Diagnostic.Code);
        Assert.Equal(0, lower.Calls);
        Assert.Equal(environmentTerminal ? 0 : 1, terminal.Calls);
    }

    [Fact]
    public void Manager_OneValidTransactionalChildRescuesTerminal()
    {
        var environment = new PatchEnvironment(ConfigPatchStatus.Applied);
        var terminal = new OutcomeProvider(ConfigProviderValueStatus.Terminal);
        var lower = new OutcomeProvider(ConfigProviderValueStatus.Found, new Model()) { Priority = 0 };
        var result = Manager(environment, terminal, lower).GetValue<Model>("Production", "Settings");
        Assert.Equal("patched", result!.Name);
        Assert.Equal(1, environment.PatchCalls);
        Assert.Same(environment.Request, environment.PatchRequest);
        Assert.Null(environment.PatchInput);
        Assert.Equal(0, lower.Calls);
    }

    [Fact]
    public void Manager_PatchTerminalWinsAndNeverPublishesLowerValue()
    {
        var environment = new PatchEnvironment(ConfigPatchStatus.Terminal);
        var source = new Model { Name = "original" };
        var lower = new OutcomeProvider(ConfigProviderValueStatus.Found, source);
        var error = Assert.Throws<ConfigurationResolutionException>(() =>
            Manager(environment, lower).GetValue<Model>("Production", "Settings"));
        Assert.Equal("config-patch-failed", error.Diagnostic.Code);
        Assert.Same(source, environment.PatchInput);
        Assert.Equal("original", source.Name);
    }

    [Fact]
    public void Manager_NoPatchReturnsFoundOrMissingAndRetainsTerminal()
    {
        var environment = new PatchEnvironment(ConfigPatchStatus.NotApplied);
        var source = new Model();
        Assert.Same(source, Manager(environment, new OutcomeProvider(ConfigProviderValueStatus.Found, source))
            .GetValue<Model>("Production", "Settings"));
        Assert.Null(Manager(environment).GetValue<Model>("Production", "Settings"));
        Assert.Throws<ConfigurationResolutionException>(() =>
            Manager(environment, new OutcomeProvider(ConfigProviderValueStatus.Terminal))
                .GetValue<Model>("Production", "Settings"));
    }

    [Fact]
    public void Manager_SkipsMissingProvidersAndAcceptsAnAbsentProviderList()
    {
        var environment = new OutcomeEnvironment(ConfigProviderValueStatus.Missing);
        var missing = new OutcomeProvider(ConfigProviderValueStatus.Missing);
        var found = new OutcomeProvider(ConfigProviderValueStatus.Found, "found") { Priority = 1 };
        Assert.Equal("found", Manager(environment, missing, found).GetValue<string>("Production", "Key"));
        Assert.Equal(1, missing.Calls);
        Assert.Equal(1, found.Calls);
        Assert.Null(new DefaultConfigManager(environment, null, NullLogger<DefaultConfigManager>.Instance)
            .GetValue<string>("Production", "Key"));
        var direct = new ConfigProviderRequest("Production", AppSurfaceConfigKey.Parse("Key"));
        using (direct.Scope)
        {
            Assert.False(direct.Scope.IsAudit);
            Assert.Equal("Production", direct.Environment);
        }
    }

    [Fact]
    public void Manager_UnknownProviderFailureAndNullOutcomeAreTerminalWithoutRawException()
    {
        var environment = new OutcomeEnvironment(ConfigProviderValueStatus.Missing);
        var provider = A.Fake<IConfigProvider>();
        A.CallTo(() => provider.Name).Returns("External");
        A.CallTo(() => provider.Resolve<string>(A<ConfigProviderRequest>._)).Throws(new IOException("SENTINEL_SECRET"));
        var error = Assert.Throws<ConfigurationResolutionException>(() => Manager(environment, provider).GetValue<string>("P", "Key"));
        Assert.Equal("config-provider-failed", error.Diagnostic.Code);
        Assert.DoesNotContain("SENTINEL_SECRET", error.ToString());
        A.CallTo(() => provider.Resolve<string>(A<ConfigProviderRequest>._)).Returns(null!);
        error = Assert.Throws<ConfigurationResolutionException>(() => Manager(environment, provider).GetValue<string>("P", "Key"));
        Assert.Equal("config-provider-invalid-result", error.Diagnostic.Code);
        A.CallTo(() => provider.Resolve<string>(A<ConfigProviderRequest>._)).Throws(new OperationCanceledException());
        Assert.Throws<OperationCanceledException>(() => Manager(environment, provider).GetValue<string>("P", "Key"));
    }

    [Fact]
    public void Manager_ExternalNoticeProseNeverEntersAutomaticLogsAndLoggerFailuresDoNotChangeResults()
    {
        var environment = new OutcomeEnvironment(ConfigProviderValueStatus.Found, "SENTINEL_VALUE")
        {
            Notices = [new("SENTINEL_CODE", "SENTINEL_PROBLEM", "SENTINEL_CAUSE", "SENTINEL_FIX", "SENTINEL_DOCS", false)]
        };
        var logger = new RecordingLogger();
        var manager = new DefaultConfigManager(environment, [], logger);
        Assert.Equal("SENTINEL_VALUE", manager.GetValue<string>("P", AppSurfaceConfigKey.Parse("Key")));
        Assert.DoesNotContain("SENTINEL", string.Join("\n", logger.Messages));
        Assert.Contains(logger.Messages, message => message.Contains("config-provider-notice", StringComparison.Ordinal));
        logger.Throw = true;
        Assert.Equal("SENTINEL_VALUE", manager.GetValue<string>("P", "Key"));
        Assert.Null(Manager(new OutcomeEnvironment(ConfigProviderValueStatus.Missing)).GetValue<string>("P", "Key"));
    }

    [Fact]
    public void Manager_TranslationAndProviderRequestsRetainTheOriginalInput()
    {
        var environment = new OutcomeEnvironment(ConfigProviderValueStatus.Missing);
        var provider = new OutcomeProvider(ConfigProviderValueStatus.Found, "value");
        var logger = new RecordingLogger();
        var manager = new DefaultConfigManager(environment, [provider], logger);
        Assert.Equal("value", manager.GetValue<string>("P", "Payments.ApiKey"));
        Assert.Equal("Payments:ApiKey", provider.Request!.Key.Value);
        Assert.Equal("Payments.ApiKey", provider.Request.OriginalInput);
        Assert.Equal(ConfigKeyInputOrigin.TranslatedDot, provider.Request.InputOrigin);
        Assert.Same(provider.Request, environment.Request);
        Assert.Contains(provider.Request.Scope.Notices, item => item.Notice.Code == "config-key-legacy-dot-path");
        var strict = new ConfigKeyInputParser(Options.Create(new AppSurfaceConfigKeyOptions { LegacyDotPathBehavior = LegacyDotPathBehavior.Strict }));
        manager = new DefaultConfigManager(environment, [provider], logger, strict);
        manager.GetValue<string>("P", "Payments.ApiKey");
        Assert.Equal("Payments.ApiKey", provider.Request.Key.Value);
        Assert.Equal(ConfigKeyInputOrigin.StrictString, provider.Request.InputOrigin);
    }

    [Fact]
    public void Manager_RejectsInvalidApplicationInputBeforeProviderTraversal()
    {
        var environment = new OutcomeEnvironment(ConfigProviderValueStatus.Missing);
        var manager = Manager(environment);
        Assert.Throws<ArgumentException>(() => manager.GetValue<string>("P", "A::B"));
        Assert.Throws<ArgumentNullException>(() => manager.GetValue<string>("P", (AppSurfaceConfigKey)null!));
        Assert.Throws<ArgumentNullException>(() => manager.GetValue<string>(null!, AppSurfaceConfigKey.Parse("A")));
        Assert.Equal(0, environment.Calls);
        Assert.Throws<ArgumentNullException>(() => new DefaultConfigManager(null!, [], NullLogger<DefaultConfigManager>.Instance));
        Assert.Throws<ArgumentNullException>(() => new DefaultConfigManager(environment, [], null!));
    }

    [Fact]
    public async Task Scope_CapturesExactlyOnceConcurrentlyAndNewScopeSeesMutation()
    {
        var environment = new SnapshotEnvironment();
        environment.Values["NAME"] = "one";
        var scope = new ConfigResolutionScope();
        var captures = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => Task.Run(() =>
            scope.GetEnvironmentSnapshot(environment, new ConfigResourceOptions()))));
        Assert.Equal(1, environment.Captures);
        Assert.All(captures, capture => Assert.Same(captures[0], capture));
        environment.Values["NAME"] = "two";
        Assert.Equal("one", captures[0].Entries["NAME"]);
        Assert.Equal("two", new ConfigResolutionScope().GetEnvironmentSnapshot(environment, new ConfigResourceOptions()).Entries["NAME"]);
        Assert.Equal(2, environment.Captures);
    }

    [Fact]
    public void Snapshot_IndexesExactCasingGroupsAndOnlyPresentPrefixChildren()
    {
        var environment = new SnapshotEnvironment();
        foreach (var name in new[] { "A", "KEY__0", "key__0", "KEY__900000", "KEY_SIBLING", "Z" })
        {
            environment.Values.Add(name, "value");
        }

        var snapshot = ConfigEnvironmentSnapshot.Capture(environment, new ConfigResourceOptions());
        Assert.Equal(new[] { "KEY__0", "key__0" }, snapshot.GetMatchingNames("Key__0"));
        Assert.Empty(snapshot.GetMatchingNames("absent"));
        Assert.Equal(new[] { "KEY__0", "key__0", "KEY__900000" }, snapshot.GetDescendantNames("KEY__"));
        Assert.Equal(new[] { "A" }, snapshot.GetDescendantNames("A"));
        Assert.Empty(snapshot.GetDescendantNames("ZZ"));
    }

    [Fact]
    public void Snapshot_EnforcesExactBoundariesAndCachesCaptureFailure()
    {
        var environment = new SnapshotEnvironment();
        environment.Values.Add("K", "v");
        var limits = new ConfigResourceOptions { MaxEnvironmentEntries = 1, MaxEnvironmentBytes = 2 };
        Assert.Single(ConfigEnvironmentSnapshot.Capture(environment, limits).Entries);
        environment.Values.Add("Z", "x");
        Assert.Equal("config-environment-entry-limit", Assert.Throws<ConfigResourceLimitException>(() =>
            ConfigEnvironmentSnapshot.Capture(environment, limits)).Code);
        limits.MaxEnvironmentEntries = 2;
        var scope = new ConfigResolutionScope();
        var before = environment.Captures;
        Assert.Throws<ConfigResourceLimitException>(() => scope.GetEnvironmentSnapshot(environment, limits));
        environment.Values.Remove("Z");
        Assert.Throws<ConfigResourceLimitException>(() => scope.GetEnvironmentSnapshot(environment, limits));
        Assert.Equal(before + 1, environment.Captures);
    }

    [Fact]
    public void PatchFactoriesEnforceExclusiveStates()
    {
        var missing = ConfigPatchResult<string>.NotApplied();
        Assert.Equal(ConfigPatchStatus.NotApplied, missing.Status);
        Assert.Null(missing.Value);
        Assert.Null(missing.Diagnostic);
        var applied = ConfigPatchResult<int>.Applied(0);
        Assert.Equal(ConfigPatchStatus.Applied, applied.Status);
        Assert.Equal(0, applied.Value);
        Assert.Null(applied.Diagnostic);
        var terminal = ConfigPatchResult<string>.Terminal(ConfigDiagnosticCatalog.Terminal("config-key-collision"));
        Assert.Equal(ConfigPatchStatus.Terminal, terminal.Status);
        Assert.Null(terminal.Value);
        Assert.NotNull(terminal.Diagnostic);
        Assert.Throws<ArgumentNullException>(() => ConfigPatchResult<string>.Applied(null!));
        Assert.Throws<ArgumentNullException>(() => ConfigPatchResult<string>.Terminal(null!));
    }

    private static DefaultConfigManager Manager(IEnvironmentConfigProvider environment, params IConfigProvider[] providers) =>
        new(environment, providers, NullLogger<DefaultConfigManager>.Instance);

    private class OutcomeProvider(ConfigProviderValueStatus status, object? value = null) : IConfigProvider
    {
        public int Priority { get; init; } = 10;
        public string Name => GetType().Name;
        public int Calls;
        public ConfigProviderRequest? Request;
        public ConfigProviderNotice[] Notices = [];
        public ConfigProviderValueResult<T> Resolve<T>(ConfigProviderRequest request)
        {
            Interlocked.Increment(ref Calls);
            Request = request;
            return status switch
            {
                ConfigProviderValueStatus.Found => ConfigProviderValueResult<T>.Found((T)value!, Notices),
                ConfigProviderValueStatus.Terminal => ConfigProviderValueResult<T>.Terminal(ConfigDiagnosticCatalog.Terminal("config-key-collision")),
                _ => ConfigProviderValueResult<T>.Missing()
            };
        }
    }

    private class OutcomeEnvironment(ConfigProviderValueStatus status, object? value = null)
        : OutcomeProvider(status, value), IEnvironmentConfigProvider
    {
        public string Environment => "Production";
        public bool IsDevelopment => false;
        public string? GetEnvironmentVariable(string name, string? defaultValue = null) => defaultValue;
        public IReadOnlyDictionary<string, string> CaptureEnvironmentVariables() => new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private sealed class PatchEnvironment(ConfigPatchStatus status) : OutcomeEnvironment(ConfigProviderValueStatus.Missing), IConfigValuePatcher
    {
        public int PatchCalls;
        public ConfigProviderRequest? PatchRequest;
        public object? PatchInput;
        public ConfigPatchResult<T> Patch<T>(ConfigProviderRequest request, T? currentValue)
        {
            PatchCalls++;
            PatchRequest = request;
            PatchInput = currentValue;
            return status switch
            {
                ConfigPatchStatus.Applied => ConfigPatchResult<T>.Applied((T)(object)new Model { Name = "patched" }),
                ConfigPatchStatus.Terminal => ConfigPatchResult<T>.Terminal(ConfigDiagnosticCatalog.Terminal("config-patch-failed")),
                _ => ConfigPatchResult<T>.NotApplied()
            };
        }
    }

    private sealed class SnapshotEnvironment : IEnvironmentProvider
    {
        public int Captures;
        public Dictionary<string, string> Values = new(StringComparer.Ordinal);
        public string Environment => "Production";
        public bool IsDevelopment => false;
        public string? GetEnvironmentVariable(string name, string? defaultValue = null) => Values.TryGetValue(name, out var value) ? value : defaultValue;
        public IReadOnlyDictionary<string, string> CaptureEnvironmentVariables()
        {
            Interlocked.Increment(ref Captures);
            return new Dictionary<string, string>(Values, StringComparer.Ordinal);
        }
    }

    private sealed class Model { public string? Name { get; set; } }

    private sealed class RecordingLogger : ILogger<DefaultConfigManager>
    {
        public List<string> Messages = [];
        public bool Throw;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (Throw) { throw new IOException("SENTINEL_LOGGER"); }
            Messages.Add(formatter(state, exception));
        }
    }
}
