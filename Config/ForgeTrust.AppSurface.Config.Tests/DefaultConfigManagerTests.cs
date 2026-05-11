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

        A.CallTo(() => environmentProvider.GetValue<string>("Production", "App.Key"))
            .Returns("from-environment");

        var manager = new DefaultConfigManager(environmentProvider, [otherProvider], logger);

        var value = manager.GetValue<string>("Production", "App.Key");

        Assert.Equal("from-environment", value);
        A.CallTo(() => otherProvider.GetValue<string>(A<string>._, A<string>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public void GetValue_QueriesProvidersByDescendingPriority()
    {
        var environmentProvider = A.Fake<IEnvironmentConfigProvider>();
        var logger = A.Fake<ILogger<DefaultConfigManager>>();
        var highPriorityProvider = A.Fake<IConfigProvider>();
        var lowPriorityProvider = A.Fake<IConfigProvider>();
        var anotherConfigManager = A.Fake<IConfigManager>();

        A.CallTo(() => environmentProvider.GetValue<string>(A<string>._, A<string>._))
            .Returns(null);
        A.CallTo(() => highPriorityProvider.Priority).Returns(10);
        A.CallTo(() => highPriorityProvider.GetValue<string>("Production", "Feature.Flag"))
            .Returns(null);
        A.CallTo(() => lowPriorityProvider.Priority).Returns(1);
        A.CallTo(() => lowPriorityProvider.GetValue<string>("Production", "Feature.Flag"))
            .Returns("from-low");

        var manager = new DefaultConfigManager(
            environmentProvider,
            [
                highPriorityProvider,
                lowPriorityProvider,
                environmentProvider, // should be filtered out
                anotherConfigManager // should be filtered out
            ],
            logger);

        var value = manager.GetValue<string>("Production", "Feature.Flag");

        Assert.Equal("from-low", value);

        A.CallTo(() => highPriorityProvider.GetValue<string>("Production", "Feature.Flag"))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => lowPriorityProvider.GetValue<string>("Production", "Feature.Flag"))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => environmentProvider.GetValue<string>("Production", "Feature.Flag"))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public void GetValue_ReturnsDefaultWhenNoProvidersHaveValue()
    {
        var environmentProvider = A.Fake<IEnvironmentConfigProvider>();
        var logger = A.Fake<ILogger<DefaultConfigManager>>();
        var otherProvider = A.Fake<IConfigProvider>();

        A.CallTo(() => environmentProvider.GetValue<string>(A<string>._, A<string>._)).Returns(null);
        A.CallTo(() => otherProvider.GetValue<string>(A<string>._, A<string>._)).Returns(null);

        var manager = new DefaultConfigManager(environmentProvider, [otherProvider], logger);

        var value = manager.GetValue<string>("Any", "Missing.Key");

        Assert.Null(value);
        A.CallTo(() => environmentProvider.GetValue<string>("Any", "Missing.Key"))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => otherProvider.GetValue<string>("Any", "Missing.Key"))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public void Constructor_HandlesNullOtherProviders()
    {
        var environmentProvider = A.Fake<IEnvironmentConfigProvider>();
        var logger = A.Fake<ILogger<DefaultConfigManager>>();
        A.CallTo(() => environmentProvider.GetValue<string>("Production", "Key"))
            .Returns("env-val");

        var manager = new DefaultConfigManager(environmentProvider, null, logger);

        Assert.Equal("env-val", manager.GetValue<string>("Production", "Key"));
    }

    [Fact]
    public void GetValue_ReturnsDefaultWithNoProviders()
    {
        var environmentProvider = A.Fake<IEnvironmentConfigProvider>();
        var logger = A.Fake<ILogger<DefaultConfigManager>>();

        // Setup environment to return null
        A.CallTo(() => environmentProvider.GetValue<string>(A<string>._, A<string>._))
            .Returns(null);

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

        A.CallTo(() => environmentProvider.GetValue<string>(A<string>._, A<string>._)).Returns(null);
        A.CallTo(() => provider1.Priority).Returns(5);
        A.CallTo(() => provider1.GetValue<string>("Production", "Key")).Returns("val1");
        A.CallTo(() => provider2.Priority).Returns(5);
        A.CallTo(() => provider2.GetValue<string>("Production", "Key")).Returns("val2");

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
        A.CallTo(() => environmentProvider.GetValue<string>(A<string>._, A<string>._)).Returns("val");

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
        A.CallTo(() => environmentProvider.GetValue<string>(A<string>._, A<string>._)).Returns(null);

        var manager = new DefaultConfigManager(environmentProvider, [], logger);
        manager.GetValue<string>("Env", "Missing");

        A.CallTo(logger).Where(c => c.Method.Name == "Log").MustHaveHappenedOnceExactly();
    }

    [Fact]
    public void GetValue_PatchesProviderObjectWithNestedEnvironmentVariable()
    {
        var innerEnvironment = A.Fake<ForgeTrust.AppSurface.Core.IEnvironmentProvider>();
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

        A.CallTo(() => innerEnvironment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        A.CallTo(() => innerEnvironment.GetEnvironmentVariable("MYAPP__SETTINGS__DATABASE__PORT", A<string?>._))
            .Returns("6543");
        A.CallTo(() => fileProvider.Priority).Returns(1);
        A.CallTo(() => fileProvider.Name).Returns("File");
        A.CallTo(() => fileProvider.GetValue<AppSettings>("Production", "MyApp.Settings")).Returns(fileValue);

        var environmentProvider = new EnvironmentConfigProvider(innerEnvironment);
        var manager = new DefaultConfigManager(environmentProvider, [fileProvider], logger);

        var value = manager.GetValue<AppSettings>("Production", "MyApp.Settings");

        Assert.Same(fileValue, value);
        Assert.NotNull(value);
        Assert.Equal("file", value.Mode);
        Assert.Equal("db.from.file", value.Database.Host);
        Assert.Equal(6543, value.Database.Port);
    }

    [Fact]
    public void GetValue_ReturnsDirectEnvironmentObjectBeforePatchingProviderValue()
    {
        var innerEnvironment = A.Fake<ForgeTrust.AppSurface.Core.IEnvironmentProvider>();
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

        A.CallTo(() => innerEnvironment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        A.CallTo(() => innerEnvironment.GetEnvironmentVariable("MYAPP__SETTINGS", A<string?>._))
            .Returns(directJson);

        var environmentProvider = new EnvironmentConfigProvider(innerEnvironment);
        var manager = new DefaultConfigManager(environmentProvider, [fileProvider], logger);

        var value = manager.GetValue<AppSettings>("Production", "MyApp.Settings");

        Assert.NotNull(value);
        Assert.Equal("environment", value.Mode);
        Assert.Equal("db.from.env", value.Database.Host);
        Assert.Equal(7000, value.Database.Port);
        A.CallTo(() => fileProvider.GetValue<AppSettings>(A<string>._, A<string>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public void GetValue_CreatesObjectFromNestedEnvironmentVariableWhenProvidersAreMissing()
    {
        var innerEnvironment = A.Fake<ForgeTrust.AppSurface.Core.IEnvironmentProvider>();
        var logger = A.Fake<ILogger<DefaultConfigManager>>();

        A.CallTo(() => innerEnvironment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        A.CallTo(() => innerEnvironment.GetEnvironmentVariable("MYAPP__SETTINGS__MODE", A<string?>._))
            .Returns("environment");

        var environmentProvider = new EnvironmentConfigProvider(innerEnvironment);
        var manager = new DefaultConfigManager(environmentProvider, [], logger);

        var value = manager.GetValue<AppSettings>("Production", "MyApp.Settings");

        Assert.NotNull(value);
        Assert.Equal("environment", value.Mode);
    }

    [Fact]
    public void GetValue_PatchesProviderObjectWithGetterOnlyNestedObject()
    {
        var innerEnvironment = A.Fake<ForgeTrust.AppSurface.Core.IEnvironmentProvider>();
        var fileProvider = A.Fake<IConfigProvider>();
        var logger = A.Fake<ILogger<DefaultConfigManager>>();
        var fileValue = new GetterOnlyAppSettings
        {
            Mode = "file"
        };
        fileValue.Database.Host = "db.from.file";
        fileValue.Database.Port = 5432;

        A.CallTo(() => innerEnvironment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        A.CallTo(() => innerEnvironment.GetEnvironmentVariable("MYAPP__SETTINGS__DATABASE__PORT", A<string?>._))
            .Returns("6543");
        A.CallTo(() => fileProvider.Priority).Returns(1);
        A.CallTo(() => fileProvider.GetValue<GetterOnlyAppSettings>("Production", "MyApp.Settings"))
            .Returns(fileValue);

        var environmentProvider = new EnvironmentConfigProvider(innerEnvironment);
        var manager = new DefaultConfigManager(environmentProvider, [fileProvider], logger);

        var value = manager.GetValue<GetterOnlyAppSettings>("Production", "MyApp.Settings");

        Assert.Same(fileValue, value);
        Assert.NotNull(value);
        Assert.Equal("file", value.Mode);
        Assert.Equal("db.from.file", value.Database.Host);
        Assert.Equal(6543, value.Database.Port);
    }

    [Fact]
    public void GetValue_PatchesProviderObjectWithGetterOnlyCollection()
    {
        var innerEnvironment = A.Fake<ForgeTrust.AppSurface.Core.IEnvironmentProvider>();
        var fileProvider = A.Fake<IConfigProvider>();
        var logger = A.Fake<ILogger<DefaultConfigManager>>();
        var fileValue = new GetterOnlyAppSettings();
        fileValue.Endpoints.Add("https://file.example");

        A.CallTo(() => innerEnvironment.GetEnvironmentVariable(A<string>._, A<string?>._)).Returns(null);
        A.CallTo(() => innerEnvironment.GetEnvironmentVariable("MYAPP__SETTINGS__ENDPOINTS__0", A<string?>._))
            .Returns("https://one.example");
        A.CallTo(() => innerEnvironment.GetEnvironmentVariable("MYAPP__SETTINGS__ENDPOINTS__1", A<string?>._))
            .Returns("https://two.example");
        A.CallTo(() => fileProvider.Priority).Returns(1);
        A.CallTo(() => fileProvider.GetValue<GetterOnlyAppSettings>("Production", "MyApp.Settings"))
            .Returns(fileValue);

        var environmentProvider = new EnvironmentConfigProvider(innerEnvironment);
        var manager = new DefaultConfigManager(environmentProvider, [fileProvider], logger);

        var value = manager.GetValue<GetterOnlyAppSettings>("Production", "MyApp.Settings");

        Assert.Same(fileValue, value);
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
}
