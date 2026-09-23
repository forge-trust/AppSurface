using FakeItEasy;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Config.Tests;

public class DefaultConfigManagerTests
{
    [Fact]
    public void GetValue_ReturnsEnvironmentValueWhenPresent()
    {
        var environmentProvider = A.Fake<IEnvironmentConfigProvider>();
        var logger = A.Fake<ILogger<DefaultConfigManager>>();
        var otherProvider = A.Fake<IConfigProvider>();

        A.CallTo(() => environmentProvider.Resolve<string>(A<ConfigProviderRequest>.That.Matches(r => r.Environment == "Production" && r.Key.Equals(AppSurfaceConfigKey.Parse("App.Key".Replace('.', ':'))))))
            .Returns(ConfigProviderValueResult<string>.Found("from-environment"));

        var manager = new DefaultConfigManager(environmentProvider, [otherProvider], logger);

        var value = manager.GetValue<string>("Production", "App.Key");

        Assert.Equal("from-environment", value);
        A.CallTo(() => otherProvider.Resolve<string>(A<ConfigProviderRequest>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public void GetValue_QueriesProvidersByDescendingPriority()
    {
        var environmentProvider = A.Fake<IEnvironmentConfigProvider>();
        var logger = A.Fake<ILogger<DefaultConfigManager>>();
        var highPriorityProvider = A.Fake<IConfigProvider>();
        var lowPriorityProvider = A.Fake<IConfigProvider>();

        A.CallTo(() => environmentProvider.Resolve<string>(A<ConfigProviderRequest>._))
            .Returns(ConfigProviderValueResult<string>.Missing());
        A.CallTo(() => highPriorityProvider.Priority).Returns(10);
        A.CallTo(() => highPriorityProvider.Resolve<string>(A<ConfigProviderRequest>.That.Matches(r => r.Environment == "Production" && r.Key.Equals(AppSurfaceConfigKey.Parse("Feature.Flag".Replace('.', ':'))))))
            .Returns(ConfigProviderValueResult<string>.Missing());
        A.CallTo(() => lowPriorityProvider.Priority).Returns(1);
        A.CallTo(() => lowPriorityProvider.Resolve<string>(A<ConfigProviderRequest>.That.Matches(r => r.Environment == "Production" && r.Key.Equals(AppSurfaceConfigKey.Parse("Feature.Flag".Replace('.', ':'))))))
            .Returns(ConfigProviderValueResult<string>.Found("from-low"));

        var manager = new DefaultConfigManager(
            environmentProvider,
            [
                highPriorityProvider,
                lowPriorityProvider,
                environmentProvider // should be filtered out
            ],
            logger);

        var value = manager.GetValue<string>("Production", "Feature.Flag");

        Assert.Equal("from-low", value);

        A.CallTo(() => highPriorityProvider.Resolve<string>(A<ConfigProviderRequest>.That.Matches(r => r.Environment == "Production" && r.Key.Equals(AppSurfaceConfigKey.Parse("Feature.Flag".Replace('.', ':'))))))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => lowPriorityProvider.Resolve<string>(A<ConfigProviderRequest>.That.Matches(r => r.Environment == "Production" && r.Key.Equals(AppSurfaceConfigKey.Parse("Feature.Flag".Replace('.', ':'))))))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => environmentProvider.Resolve<string>(A<ConfigProviderRequest>.That.Matches(r => r.Environment == "Production" && r.Key.Equals(AppSurfaceConfigKey.Parse("Feature.Flag".Replace('.', ':'))))))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public void GetValue_ReturnsDefaultWhenNoProvidersHaveValue()
    {
        var environmentProvider = A.Fake<IEnvironmentConfigProvider>();
        var logger = A.Fake<ILogger<DefaultConfigManager>>();
        var otherProvider = A.Fake<IConfigProvider>();

        A.CallTo(() => environmentProvider.Resolve<string>(A<ConfigProviderRequest>._)).Returns(ConfigProviderValueResult<string>.Missing());
        A.CallTo(() => otherProvider.Resolve<string>(A<ConfigProviderRequest>._)).Returns(ConfigProviderValueResult<string>.Missing());

        var manager = new DefaultConfigManager(environmentProvider, [otherProvider], logger);

        var value = manager.GetValue<string>("Any", "Missing.Key");

        Assert.Null(value);
        A.CallTo(() => environmentProvider.Resolve<string>(A<ConfigProviderRequest>.That.Matches(r => r.Environment == "Any" && r.Key.Equals(AppSurfaceConfigKey.Parse("Missing.Key".Replace('.', ':'))))))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => otherProvider.Resolve<string>(A<ConfigProviderRequest>.That.Matches(r => r.Environment == "Any" && r.Key.Equals(AppSurfaceConfigKey.Parse("Missing.Key".Replace('.', ':'))))))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public void Constructor_HandlesNullOtherProviders()
    {
        var environmentProvider = A.Fake<IEnvironmentConfigProvider>();
        var logger = A.Fake<ILogger<DefaultConfigManager>>();
        A.CallTo(() => environmentProvider.Resolve<string>(A<ConfigProviderRequest>.That.Matches(r => r.Environment == "Production" && r.Key.Equals(AppSurfaceConfigKey.Parse("Key".Replace('.', ':'))))))
            .Returns(ConfigProviderValueResult<string>.Found("env-val"));

        var manager = new DefaultConfigManager(environmentProvider, null, logger);

        Assert.Equal("env-val", manager.GetValue<string>("Production", "Key"));
    }

    [Fact]
    public void GetValue_ReturnsDefaultWithNoProviders()
    {
        var environmentProvider = A.Fake<IEnvironmentConfigProvider>();
        var logger = A.Fake<ILogger<DefaultConfigManager>>();

        // Setup environment to return null
        A.CallTo(() => environmentProvider.Resolve<string>(A<ConfigProviderRequest>._))
            .Returns(ConfigProviderValueResult<string>.Missing());

        // No other providers
        var manager = new DefaultConfigManager(environmentProvider, [], logger);

        var value = manager.GetValue<string>("Production", "Any.Key");

        Assert.Null(value);
    }

    [Fact]
    public void GetValue_DeterministicallyBreaksPriorityTies()
    {
        var environmentProvider = A.Fake<IEnvironmentConfigProvider>();
        var logger = A.Fake<ILogger<DefaultConfigManager>>();
        var provider1 = A.Fake<IConfigProvider>();
        var provider2 = A.Fake<IConfigProvider>();

        A.CallTo(() => environmentProvider.Resolve<string>(A<ConfigProviderRequest>._)).Returns(ConfigProviderValueResult<string>.Missing());
        A.CallTo(() => provider1.Priority).Returns(5);
        A.CallTo(() => provider1.Resolve<string>(A<ConfigProviderRequest>.That.Matches(r => r.Environment == "Production" && r.Key.Equals(AppSurfaceConfigKey.Parse("Key".Replace('.', ':')))))).Returns(ConfigProviderValueResult<string>.Found("val1"));
        A.CallTo(() => provider2.Priority).Returns(5);
        A.CallTo(() => provider2.Resolve<string>(A<ConfigProviderRequest>.That.Matches(r => r.Environment == "Production" && r.Key.Equals(AppSurfaceConfigKey.Parse("Key".Replace('.', ':')))))).Returns(ConfigProviderValueResult<string>.Found("val2"));

        // When priorities are equal, the order in the list should be preserved by OrderByDescending (stable sort)
        var manager = new DefaultConfigManager(environmentProvider, [provider1, provider2], logger);
        Assert.Equal("val1", manager.GetValue<string>("Production", "Key"));

        var manager2 = new DefaultConfigManager(environmentProvider, [provider2, provider1], logger);
        Assert.Equal("val2", manager2.GetValue<string>("Production", "Key"));
    }

    [Fact]
    public void GetValue_LogsRetrievedFromEnvironmentWhenDebugEnabled()
    {
        var environmentProvider = A.Fake<IEnvironmentConfigProvider>();
        var logger = A.Fake<ILogger<DefaultConfigManager>>();

        A.CallTo(() => logger.IsEnabled(LogLevel.Debug)).Returns(true);
        A.CallTo(() => environmentProvider.Resolve<string>(A<ConfigProviderRequest>._)).Returns(ConfigProviderValueResult<string>.Found("val"));

        var manager = new DefaultConfigManager(environmentProvider, [], logger);
        manager.GetValue<string>("Env", "Key");

        A.CallTo(logger).Where(c => c.Method.Name == "Log").MustHaveHappenedOnceExactly();
    }

    [Fact]
    public void GetValue_LogsKeyNotFoundWhenDebugEnabled()
    {
        var environmentProvider = A.Fake<IEnvironmentConfigProvider>();
        var logger = A.Fake<ILogger<DefaultConfigManager>>();

        A.CallTo(() => logger.IsEnabled(LogLevel.Debug)).Returns(true);
        A.CallTo(() => environmentProvider.Resolve<string>(A<ConfigProviderRequest>._)).Returns(ConfigProviderValueResult<string>.Missing());

        var manager = new DefaultConfigManager(environmentProvider, [], logger);
        manager.GetValue<string>("Env", "Missing");

        A.CallTo(logger).Where(c => c.Method.Name == "Log").MustHaveHappenedOnceExactly();
    }

    [Fact]
    public void GetValue_PatchesProviderObjectWithNestedEnvironmentVariable()
    {
        var innerEnvironment = A.Fake<ForgeTrust.AppSurface.Core.IEnvironmentProvider>();
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        A.CallTo(() => innerEnvironment.CaptureEnvironmentVariables()).ReturnsLazily(() =>
            new Dictionary<string, string>(variables, StringComparer.Ordinal));
        var fileProvider = A.Fake<IConfigProvider>();
        var logger = A.Fake<ILogger<DefaultConfigManager>>();
        var fileValue = new AppSettings
        {
            Mode = "file",
            Database = new DatabaseOptions
            {
                Host = "db.from.file",
                Port = 5432
            }
        };

        variables["MYAPP__SETTINGS__DATABASE__PORT"] = "6543";
        A.CallTo(() => fileProvider.Priority).Returns(1);
        A.CallTo(() => fileProvider.Name).Returns("File");
        A.CallTo(() => fileProvider.Resolve<AppSettings>(A<ConfigProviderRequest>.That.Matches(r => r.Environment == "Production" && r.Key.Equals(AppSurfaceConfigKey.Parse("MyApp.Settings".Replace('.', ':')))))).Returns(ConfigProviderValueResult<AppSettings>.Found(fileValue));

        var environmentProvider = new EnvironmentConfigProvider(innerEnvironment);
        var manager = new DefaultConfigManager(environmentProvider, [fileProvider], logger);

        var value = manager.GetValue<AppSettings>("Production", "MyApp.Settings");

        Assert.NotSame(fileValue, value);
        Assert.NotNull(value);
        Assert.Equal("file", value.Mode);
        Assert.Equal("db.from.file", value.Database.Host);
        Assert.Equal(6543, value.Database.Port);
    }

    [Fact]
    public void GetValue_ReturnsDirectEnvironmentObjectBeforePatchingProviderValue()
    {
        var innerEnvironment = A.Fake<ForgeTrust.AppSurface.Core.IEnvironmentProvider>();
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        A.CallTo(() => innerEnvironment.CaptureEnvironmentVariables()).ReturnsLazily(() =>
            new Dictionary<string, string>(variables, StringComparer.Ordinal));
        var fileProvider = A.Fake<IConfigProvider>();
        var logger = A.Fake<ILogger<DefaultConfigManager>>();
        var directJson = """
            {
              "Mode": "environment",
              "Database": {
                "Host": "db.from.env",
                "Port": 7000
              }
            }
            """;

        variables["MYAPP__SETTINGS"] = directJson;

        var environmentProvider = new EnvironmentConfigProvider(innerEnvironment);
        var manager = new DefaultConfigManager(environmentProvider, [fileProvider], logger);

        var value = manager.GetValue<AppSettings>("Production", "MyApp.Settings");

        Assert.NotNull(value);
        Assert.Equal("environment", value.Mode);
        Assert.Equal("db.from.env", value.Database.Host);
        Assert.Equal(7000, value.Database.Port);
        A.CallTo(() => fileProvider.Resolve<AppSettings>(A<ConfigProviderRequest>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public void GetValue_CreatesObjectFromNestedEnvironmentVariableWhenProvidersAreMissing()
    {
        var innerEnvironment = A.Fake<ForgeTrust.AppSurface.Core.IEnvironmentProvider>();
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        A.CallTo(() => innerEnvironment.CaptureEnvironmentVariables()).ReturnsLazily(() =>
            new Dictionary<string, string>(variables, StringComparer.Ordinal));
        var logger = A.Fake<ILogger<DefaultConfigManager>>();

        A.CallTo(() => innerEnvironment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        variables["MYAPP__SETTINGS__MODE"] = "environment";

        var environmentProvider = new EnvironmentConfigProvider(innerEnvironment);
        var manager = new DefaultConfigManager(environmentProvider, [], logger);

        var value = manager.GetValue<AppSettings>("Production", "MyApp.Settings");

        Assert.NotNull(value);
        Assert.Equal("environment", value.Mode);
    }

    [Fact]
    public void GetValue_AppliesNestedEnvironmentPatchBeforeThrowingTerminalProviderDiagnostic()
    {
        var innerEnvironment = A.Fake<ForgeTrust.AppSurface.Core.IEnvironmentProvider>();
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        A.CallTo(() => innerEnvironment.CaptureEnvironmentVariables()).ReturnsLazily(() =>
            new Dictionary<string, string>(variables, StringComparer.Ordinal));
        var lowerProvider = A.Fake<IConfigProvider>();
        var logger = A.Fake<ILogger<DefaultConfigManager>>();
        var terminalProvider = new TerminalProvider();

        variables["MYAPP__SETTINGS__MODE"] = "environment";
        A.CallTo(() => lowerProvider.Priority).Returns(1);
        A.CallTo(() => lowerProvider.Resolve<AppSettings>(A<ConfigProviderRequest>._))
            .Returns(ConfigProviderValueResult<AppSettings>.Found(new AppSettings { Mode = "lower" }));

        var environmentProvider = new EnvironmentConfigProvider(innerEnvironment);
        var manager = new DefaultConfigManager(environmentProvider, [lowerProvider, terminalProvider], logger);

        var value = manager.GetValue<AppSettings>("Production", "MyApp.Settings");

        Assert.NotNull(value);
        Assert.Equal("environment", value.Mode);
        Assert.True(terminalProvider.WasCalled);
        A.CallTo(() => lowerProvider.Resolve<AppSettings>(A<ConfigProviderRequest>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public void GetValue_DoesNotPatchOverTerminalEnvironmentRoot()
    {
        var innerEnvironment = A.Fake<ForgeTrust.AppSurface.Core.IEnvironmentProvider>();
        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MYAPP__SETTINGS"] = "{invalid-json",
            ["MYAPP__SETTINGS__MODE"] = "environment"
        };
        A.CallTo(() => innerEnvironment.CaptureEnvironmentVariables()).ReturnsLazily(() =>
            new Dictionary<string, string>(variables, StringComparer.Ordinal));
        var lowerProvider = A.Fake<IConfigProvider>();
        var manager = new DefaultConfigManager(new EnvironmentConfigProvider(innerEnvironment),
            [lowerProvider], A.Fake<ILogger<DefaultConfigManager>>());

        var exception = Assert.Throws<ConfigurationResolutionException>(() =>
            manager.GetValue<AppSettings>("Production", "MyApp.Settings"));

        Assert.Equal("config-patch-failed", exception.Diagnostic.Code);
        A.CallTo(() => lowerProvider.Resolve<AppSettings>(A<ConfigProviderRequest>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public void GetValue_DoesNotInvokePatcherAfterTerminalEnvironmentResolution()
    {
        var environmentProvider = new TerminalEnvironmentPatcher();
        var manager = new DefaultConfigManager(environmentProvider, [], A.Fake<ILogger<DefaultConfigManager>>());

        var exception = Assert.Throws<ConfigurationResolutionException>(() =>
            manager.GetValue<AppSettings>("Production", "MyApp.Settings"));

        Assert.Equal("environment-root-terminal", exception.Diagnostic.Code);
        Assert.False(environmentProvider.WasPatched);
    }

    [Fact]
    public void GetValue_PatchesProviderObjectWithGetterOnlyNestedObject()
    {
        var innerEnvironment = A.Fake<ForgeTrust.AppSurface.Core.IEnvironmentProvider>();
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        A.CallTo(() => innerEnvironment.CaptureEnvironmentVariables()).ReturnsLazily(() =>
            new Dictionary<string, string>(variables, StringComparer.Ordinal));
        var fileProvider = A.Fake<IConfigProvider>();
        var logger = A.Fake<ILogger<DefaultConfigManager>>();
        var fileValue = new GetterOnlyAppSettings
        {
            Mode = "file"
        };
        fileValue.Database.Host = "db.from.file";
        fileValue.Database.Port = 5432;

        A.CallTo(() => innerEnvironment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        variables["MYAPP__SETTINGS__DATABASE__PORT"] = "6543";
        A.CallTo(() => fileProvider.Priority).Returns(1);
        A.CallTo(() => fileProvider.Resolve<GetterOnlyAppSettings>(A<ConfigProviderRequest>.That.Matches(r => r.Environment == "Production" && r.Key.Equals(AppSurfaceConfigKey.Parse("MyApp.Settings".Replace('.', ':'))))))
            .Returns(ConfigProviderValueResult<GetterOnlyAppSettings>.Found(fileValue));

        var environmentProvider = new EnvironmentConfigProvider(innerEnvironment);
        var manager = new DefaultConfigManager(environmentProvider, [fileProvider], logger);

        var value = manager.GetValue<GetterOnlyAppSettings>("Production", "MyApp.Settings");

        Assert.NotSame(fileValue, value);
        Assert.NotNull(value);
        Assert.Equal("file", value.Mode);
        Assert.Equal("db.from.file", value.Database.Host);
        Assert.Equal(6543, value.Database.Port);
    }

    [Fact]
    public void GetValue_PatchesProviderObjectWithGetterOnlyCollection()
    {
        var innerEnvironment = A.Fake<ForgeTrust.AppSurface.Core.IEnvironmentProvider>();
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        A.CallTo(() => innerEnvironment.CaptureEnvironmentVariables()).ReturnsLazily(() =>
            new Dictionary<string, string>(variables, StringComparer.Ordinal));
        var fileProvider = A.Fake<IConfigProvider>();
        var logger = A.Fake<ILogger<DefaultConfigManager>>();
        var fileValue = new GetterOnlyAppSettings();
        fileValue.Endpoints.Add("https://file.example");

        variables["MYAPP__SETTINGS__ENDPOINTS__0"] = "https://one.example";
        variables["MYAPP__SETTINGS__ENDPOINTS__1"] = "https://two.example";
        A.CallTo(() => fileProvider.Priority).Returns(1);
        A.CallTo(() => fileProvider.Resolve<GetterOnlyAppSettings>(A<ConfigProviderRequest>.That.Matches(r => r.Environment == "Production" && r.Key.Equals(AppSurfaceConfigKey.Parse("MyApp.Settings".Replace('.', ':'))))))
            .Returns(ConfigProviderValueResult<GetterOnlyAppSettings>.Found(fileValue));

        var environmentProvider = new EnvironmentConfigProvider(innerEnvironment);
        var manager = new DefaultConfigManager(environmentProvider, [fileProvider], logger);

        var value = manager.GetValue<GetterOnlyAppSettings>("Production", "MyApp.Settings");

        Assert.NotSame(fileValue, value);
        Assert.NotNull(value);
        Assert.Equal(["https://one.example", "https://two.example"], value.Endpoints);
    }

    private sealed class AppSettings
    {
        public string? Mode { get; set; }

        public DatabaseOptions Database { get; set; } = new();
    }

    private sealed class GetterOnlyAppSettings
    {
        public string? Mode { get; set; }

        public DatabaseOptions Database { get; } = new();

        public List<string> Endpoints { get; } = [];
    }

    private sealed class DatabaseOptions
    {
        public string? Host { get; set; }

        public int Port { get; set; }
    }

    private sealed class TerminalProvider : IConfigProvider
    {
        private readonly ConfigProviderTerminalDiagnostic _diagnostic = new(
            "provider-terminal",
            "Remote provider failed.",
            "The remote provider could not return a claimed value.",
            "Set an environment override or repair the provider.",
            docs: null,
            retryable: true);

        public int Priority => 10;

        public string Name => nameof(TerminalProvider);

        public bool WasCalled { get; private set; }

        public ConfigProviderValueResult<T> Resolve<T>(ConfigProviderRequest request)
        {
            WasCalled = true;
            return ConfigProviderValueResult<T>.Terminal(_diagnostic);
        }
    }

    private sealed class TerminalEnvironmentPatcher : IEnvironmentConfigProvider, IConfigValuePatcher
    {
        private static readonly ConfigProviderTerminalDiagnostic RootDiagnostic = new(
            "environment-root-terminal", "The environment root is invalid.",
            "The root cannot be used.", "Repair the root value.", docs: null, retryable: false);

        public bool WasPatched { get; private set; }
        public int Priority => 0;
        public string Name => nameof(TerminalEnvironmentPatcher);
        public string Environment => "Production";
        public bool IsDevelopment => false;

        public ConfigProviderValueResult<T> Resolve<T>(ConfigProviderRequest request) =>
            ConfigProviderValueResult<T>.Terminal(RootDiagnostic);

        public ConfigPatchResult<T> Patch<T>(ConfigProviderRequest request, T? currentValue)
        {
            WasPatched = true;
            return ConfigPatchResult<T>.Applied((T)(object)new AppSettings { Mode = "should-not-publish" });
        }

        public string? GetEnvironmentVariable(string name, string? defaultValue = null) => defaultValue;
        public IReadOnlyDictionary<string, string> CaptureEnvironmentVariables() => new Dictionary<string, string>();
    }
}
