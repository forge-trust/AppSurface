using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config.Tests;

public sealed class ConfigManagerContractTests
{
    private static readonly AppSurfaceConfigKey Key = AppSurfaceConfigKey.Parse("Payments:ApiKey");

    [Fact]
    public void ValueTypes_AreFoundEvenWhenDefault()
    {
        var environment = new Provider
        {
            ResolveValue = (_, type) => type == typeof(int)
            ? ConfigProviderValueResult<int>.Found(0) : ConfigProviderValueResult<bool>.Found(false)
        };
        var lower = new Provider { ResolveValue = (_, _) => throw new InvalidOperationException() };
        var manager = Manager(environment, lower);
        Assert.Equal(0, manager.GetValue<int>("Production", Key));
        Assert.False(manager.GetValue<bool>("Production", Key));
        Assert.Equal(0, lower.Reads);
    }

    [Theory]
    [InlineData("config-key-collision")]
    [InlineData("config-key-unrepresentable")]
    public void Terminal_StopsLowerProvidersAndRetainsDiagnostic(string code)
    {
        var environment = MissingEnvironment();
        var terminal = new Provider { ResolveValue = (_, _) => ConfigProviderValueResult<string>.Terminal(ConfigDiagnosticCatalog.Terminal(code)) };
        var lower = new Provider { ResolveValue = (_, _) => ConfigProviderValueResult<string>.Found("unreachable") };
        var failure = Assert.Throws<ConfigurationResolutionException>(() => Manager(environment, terminal, lower).GetValue<string>("Production", Key));
        Assert.Equal(code, failure.Diagnostic.Code);
        Assert.Equal(0, lower.Reads);
        Assert.Equal(1, environment.Patches);
        Assert.Equal(Key, failure.LogicalKey);
    }

    [Fact]
    public void TerminalEnvironment_NeverQueriesLowerProvider()
    {
        var environment = new Provider { ResolveValue = (_, _) => ConfigProviderValueResult<string>.Terminal(ConfigDiagnosticCatalog.Terminal("config-environment-snapshot-failed")) };
        var lower = new Provider();
        Assert.Throws<ConfigurationResolutionException>(() => Manager(environment, lower).GetValue<string>("Production", Key));
        Assert.Equal(0, lower.Reads);
    }

    [Fact]
    public void SuccessfulChildPatch_RescuesTerminalWithSameRequest()
    {
        ConfigProviderRequest? directRequest = null;
        var environment = MissingEnvironment();
        environment.ResolveValue = (request, _) => { directRequest = request; return ConfigProviderValueResult<string>.Missing(); };
        environment.PatchValue = (request, value) =>
        {
            Assert.Same(directRequest, request);
            Assert.Null(value);
            return ConfigPatchResult<string>.Applied("patched");
        };
        var high = new Provider
        {
            ResolveValue = (request, _) =>
        {
            Assert.Same(directRequest, request);
            return ConfigProviderValueResult<string>.Terminal(ConfigDiagnosticCatalog.Terminal("config-provider-failed"));
        }
        };
        Assert.Equal("patched", Manager(environment, high).GetValue<string>("Production", Key));
    }

    [Fact]
    public void PatchTerminal_WinsOverRetainedProviderDiagnostic()
    {
        var environment = MissingEnvironment();
        environment.PatchValue = (_, _) => ConfigPatchResult<string>.Terminal(ConfigDiagnosticCatalog.Terminal("config-patch-failed"));
        var high = new Provider { ResolveValue = (_, _) => ConfigProviderValueResult<string>.Terminal(ConfigDiagnosticCatalog.Terminal("config-key-collision")) };
        var failure = Assert.Throws<ConfigurationResolutionException>(() => Manager(environment, high).GetValue<string>("Production", Key));
        Assert.Equal("config-patch-failed", failure.Diagnostic.Code);
    }

    [Fact]
    public void AppliedPatch_LogsCommittedChildAliasOnceWithoutClaimingMissingBaseResolved()
    {
        var logger = new RecordingLogger();
        ConfigResolutionScope? scope = null;
        var childKey = AppSurfaceConfigKey.Parse("PatchNotice:Settings:Name");
        var environment = new Provider { Name = "PatchNoticeEnvironment" };
        environment.PatchValue = (request, _) =>
        {
            scope = request.Scope;
            scope.AddNotice(environment.Name, childKey,
                ConfigDiagnosticCatalog.LegacyAlias("PATCHNOTICE_SETTINGS_NAME", "PATCHNOTICE__SETTINGS__NAME"), 4096);
            return ConfigPatchResult<string>.Applied("SENTINEL_PATCH_VALUE");
        };
        var lower = new Provider { Name = "MissingPatchBase" };
        var manager = new DefaultConfigManager(environment, [new BaseProvider(lower)], logger);

        Assert.Equal("SENTINEL_PATCH_VALUE", manager.GetValue<string>("PatchNoticeProduction", "PatchNotice.Settings"));
        Assert.Equal(2, scope!.NoticeCount);
        Assert.Equal(2, scope.Notices.Count);
        Assert.Single(logger.Messages, message => message.Contains("config-key-legacy-provider-alias", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message => message.Contains(childKey.Value, StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message => message.Contains("config-key-found", StringComparison.Ordinal)
            && message.Contains(environment.Name, StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, message => message.Contains(lower.Name, StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, message => message.Contains("SENTINEL", StringComparison.Ordinal));

        manager.GetValue<string>("PatchNoticeProduction", "PatchNotice.Settings");
        Assert.Single(logger.Messages, message => message.Contains("config-key-legacy-provider-alias", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UncommittedPatch_DoesNotLogCandidateNotices(bool terminal)
    {
        var logger = new RecordingLogger();
        var environment = MissingEnvironment();
        environment.PatchValue = (request, _) =>
        {
            request.Scope.AddNotice(environment.Name, request.Key,
                ConfigDiagnosticCatalog.LegacyAlias("UNCOMMITTED_ALIAS", "CANONICAL_ALIAS"), 4096);
            return terminal
                ? ConfigPatchResult<string>.Terminal(ConfigDiagnosticCatalog.Terminal("config-patch-failed"))
                : ConfigPatchResult<string>.NotApplied();
        };
        var manager = new DefaultConfigManager(environment, [], logger);
        if (terminal)
        {
            Assert.Throws<ConfigurationResolutionException>(() => manager.GetValue<string>("Production", Key));
        }
        else
        {
            Assert.Null(manager.GetValue<string>("Production", Key));
        }

        Assert.DoesNotContain(logger.Messages, message => message.Contains("config-key-legacy-provider-alias", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnexpectedPatchFailure_IsValueSafeAndTerminal(bool returnNull)
    {
        var environment = MissingEnvironment();
        environment.PatchValue = (_, _) => returnNull ? null! : throw new InvalidOperationException("SENTINEL_SECRET");
        var failure = Assert.Throws<ConfigurationResolutionException>(() => Manager(environment).GetValue<string>("Production", Key));
        Assert.Equal("config-patch-failed", failure.Diagnostic.Code);
        Assert.DoesNotContain("SENTINEL_SECRET", failure.ToString());
    }

    [Theory]
    [InlineData(false, "config-provider-failed")]
    [InlineData(true, "config-provider-invalid-result")]
    public void UnexpectedProviderFailure_IsValueSafeAndTerminal(bool returnNull, string expectedCode)
    {
        var environment = MissingEnvironment();
        var high = new Provider { ResolveValue = (_, _) => returnNull ? null! : throw new InvalidOperationException("SENTINEL_SECRET") };
        var lower = new Provider();
        var failure = Assert.Throws<ConfigurationResolutionException>(() => Manager(environment, high, lower).GetValue<string>("Production", Key));
        Assert.Equal(expectedCode, failure.Diagnostic.Code);
        Assert.DoesNotContain("SENTINEL_SECRET", failure.ToString());
        Assert.Equal(0, lower.Reads);
    }

    [Fact]
    public void CallerCancellation_IsNotConvertedToProviderOrPatchFailure()
    {
        var environment = MissingEnvironment();
        environment.ResolveValue = (_, _) => throw new OperationCanceledException();
        Assert.Throws<OperationCanceledException>(() => Manager(environment).GetValue<string>("Production", Key));
        environment.ResolveValue = (_, _) => ConfigProviderValueResult<string>.Missing();
        environment.PatchValue = (_, _) => throw new OperationCanceledException();
        Assert.Throws<OperationCanceledException>(() => Manager(environment).GetValue<string>("Production", Key));
    }

    [Theory]
    [InlineData("")]
    [InlineData("A::B")]
    [InlineData(":A")]
    public void InvalidInput_StopsBeforeProviderTraversal(string input)
    {
        var environment = MissingEnvironment();
        var failure = Assert.Throws<ArgumentException>(() => Manager(environment).GetValue<string>("Production", input));
        Assert.Equal("key", failure.ParamName);
        Assert.Equal(0, environment.Reads);
    }

    [Fact]
    public void NullTypedKeyAndEnvironment_StopBeforeTraversal()
    {
        var environment = MissingEnvironment();
        var manager = Manager(environment);
        Assert.Throws<ArgumentNullException>(() => manager.GetValue<string>("Production", (AppSurfaceConfigKey)null!));
        Assert.Throws<ArgumentNullException>(() => manager.GetValue<string>(null!, Key));
        Assert.Equal(0, environment.Reads);
    }

    [Fact]
    public void ParserOrigin_IsPreservedAcrossTypedAndStringBoundaries()
    {
        var seen = new List<ConfigProviderRequest>();
        var environment = new Provider { ResolveValue = (request, _) => { seen.Add(request); return ConfigProviderValueResult<string>.Found("value"); } };
        var manager = Manager(environment);
        manager.GetValue<string>("Production", "Payments.ApiKey");
        manager.GetValue<string>("Production", "Payments:ApiKey");
        manager.GetValue<string>("Production", Key);
        Assert.Equal(new[] { ConfigKeyInputOrigin.TranslatedDot, ConfigKeyInputOrigin.StrictString, ConfigKeyInputOrigin.Typed }, seen.Select(r => r.InputOrigin));
        Assert.Equal("Payments.ApiKey", seen[0].OriginalInput);
        Assert.All(seen, request => Assert.Equal(Key, request.Key));
        Assert.NotSame(seen[0].Scope, seen[1].Scope);
        Assert.Single(seen[0].Scope.Notices);
    }

    [Fact]
    public void StrictParser_RetainsLiteralDot()
    {
        var environment = new Provider
        {
            ResolveValue = (request, _) =>
        {
            Assert.Single(request.Key.Segments);
            return ConfigProviderValueResult<string>.Found("value");
        }
        };
        var parser = new ConfigKeyInputParser(Options.Create(new AppSurfaceConfigKeyOptions { LegacyDotPathBehavior = LegacyDotPathBehavior.Strict }));
        var manager = new DefaultConfigManager(environment, [], NullLogger<DefaultConfigManager>.Instance, parser);
        manager.GetValue<string>("Production", "Payments.ApiKey");
    }

    [Fact]
    public void Logging_DropsExternalProseAndBoundsIdentifiers()
    {
        var logger = new RecordingLogger();
        var environment = new Provider
        {
            Name = "Provider\n" + new string('p', 400),
            ResolveValue = (_, _) => ConfigProviderValueResult<string>.Found("SENTINEL_VALUE",
                new ConfigProviderNotice("SENTINEL_CODE", "SENTINEL_PROBLEM", "SENTINEL_CAUSE", "SENTINEL_FIX", "SENTINEL_DOCS", false))
        };
        var manager = new DefaultConfigManager(environment, [], logger,
            resourceOptions: Options.Create(new ConfigResourceOptions { MaxRenderedIdentifierCharacters = 12 }));
        Assert.Equal("SENTINEL_VALUE", manager.GetValue<string>("Production\n" + new string('e', 400), Key));
        Assert.NotEmpty(logger.Messages);
        Assert.All(logger.Messages, message =>
        {
            Assert.DoesNotContain("SENTINEL", message);
            Assert.DoesNotContain('\n', message);
            Assert.True(message.Length < 240);
        });
        Assert.Contains(logger.Messages, message => message.Contains("config-provider-notice", StringComparison.Ordinal));
    }

    [Fact]
    public void ThrowingLogger_DoesNotChangeResolution()
    {
        var logger = new RecordingLogger { Throw = true };
        var environment = new Provider { ResolveValue = (_, _) => ConfigProviderValueResult<string>.Found("value") };
        Assert.Equal("value", new DefaultConfigManager(environment, [], logger).GetValue<string>("Production", "Payments.ApiKey"));
    }

    private static Provider MissingEnvironment() => new()
    {
        ResolveValue = (_, _) => ConfigProviderValueResult<string>.Missing()
    };

    private static DefaultConfigManager Manager(Provider environment, params IConfigProvider[] providers) =>
        new(environment, providers.Select(provider => new BaseProvider((Provider)provider)), NullLogger<DefaultConfigManager>.Instance);

    private sealed class BaseProvider(Provider provider) : IConfigProvider
    {
        public string Name => provider.Name;
        public int Priority => provider.Priority;
        public ConfigProviderValueResult<T> Resolve<T>(ConfigProviderRequest request) => provider.Resolve<T>(request);
    }

    private sealed class Provider : IEnvironmentConfigProvider, IConfigValuePatcher
    {
        public string Environment => "Production";
        public bool IsDevelopment => false;
        public string? GetEnvironmentVariable(string name, string? defaultValue = null) => defaultValue;
        public IReadOnlyDictionary<string, string> CaptureEnvironmentVariables() => new Dictionary<string, string>(StringComparer.Ordinal);
        public int Priority => 10;
        public string Name { get; init; } = "Fixture";
        internal Func<ConfigProviderRequest, Type, object> ResolveValue { get; set; } = (_, _) => ConfigProviderValueResult<string>.Missing();
        internal Func<ConfigProviderRequest, object?, object> PatchValue { get; set; } = (_, _) => ConfigPatchResult<string>.NotApplied();
        internal int Reads { get; private set; }
        internal int Patches { get; private set; }
        public ConfigProviderValueResult<T> Resolve<T>(ConfigProviderRequest request)
        {
            Reads++;
            return (ConfigProviderValueResult<T>)ResolveValue(request, typeof(T));
        }
        public ConfigPatchResult<T> Patch<T>(ConfigProviderRequest request, T? currentValue)
        {
            Patches++;
            return (ConfigPatchResult<T>)PatchValue(request, currentValue);
        }
    }

    private sealed class RecordingLogger : ILogger<DefaultConfigManager>
    {
        internal List<string> Messages { get; } = [];
        internal bool Throw { get; init; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (Throw) { throw new InvalidOperationException("logger unavailable"); }
            Messages.Add(formatter(state, exception));
        }
    }
}
