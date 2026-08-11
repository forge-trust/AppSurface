using ForgeTrust.AppSurface.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace NamedCanaryLab.Tests;

public sealed class CanaryLabSettingsTests
{
    [Fact]
    public void Create_RejectsNonDevelopmentEnvironment()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            CanaryLabSettings.Create(CreateConfiguration(), new TestHostEnvironment(Environments.Production)));

        Assert.Equal("The named-canary lab may run only in the Development environment.", exception.Message);
    }

    [Theory]
    [InlineData("OperatorToken")]
    [InlineData("Candidate")]
    [InlineData("Environment")]
    [InlineData("Scenario")]
    public void Create_RejectsMissingRequiredConfigurationWithoutEchoingValues(string missingName)
    {
        const string token = "operator-token-sentinel";
        var values = CreateValues();
        values.Remove($"{CanaryLabSettings.SectionName}:{missingName}");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CanaryLabSettings.Create(configuration, new TestHostEnvironment(Environments.Development)));

        Assert.Contains($"NamedCanaryLab:{missingName}", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(token, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Pass", "Pass")]
    [InlineData("Pending", "Pending")]
    [InlineData("stale", "Stale")]
    public void Create_AcceptsKnownScenariosAndComparesTokensWithoutRetainingTheValue(
        string configuredScenario,
        string expectedScenario)
    {
        var values = CreateValues();
        values[$"{CanaryLabSettings.SectionName}:Scenario"] = configuredScenario;
        var settings = CanaryLabSettings.Create(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
            new TestHostEnvironment(Environments.Development));

        Assert.Equal(Enum.Parse<CanaryLabScenario>(expectedScenario), settings.Scenario);
        Assert.True(settings.MatchesOperatorToken("operator-token-sentinel"));
        Assert.False(settings.MatchesOperatorToken("wrong-token"));
    }

    [Fact]
    public void Create_RejectsUnknownScenario()
    {
        var values = CreateValues();
        values[$"{CanaryLabSettings.SectionName}:Scenario"] = "fail";

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CanaryLabSettings.Create(
                new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
                new TestHostEnvironment(Environments.Development)));

        Assert.Equal("NamedCanaryLab:Scenario must be Pass, Pending, or Stale.", exception.Message);
    }

    [Theory]
    [InlineData("OperatorToken", "")]
    [InlineData("Candidate", " ")]
    [InlineData("Environment", "\t")]
    [InlineData("Scenario", "")]
    public void Create_RejectsBlankRequiredConfiguration(string settingName, string value)
    {
        var values = CreateValues();
        values[$"{CanaryLabSettings.SectionName}:{settingName}"] = value;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CanaryLabSettings.Create(
                new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
                new TestHostEnvironment(Environments.Development)));

        Assert.Equal($"NamedCanaryLab:{settingName} must be configured.", exception.Message);
    }

    [Fact]
    public void MatchesOperatorToken_RejectsNullCandidateTokens()
    {
        var settings = CanaryLabSettings.Create(CreateConfiguration(), new TestHostEnvironment(Environments.Development));

        Assert.Throws<ArgumentNullException>(() => settings.MatchesOperatorToken(null!));
    }

    [Fact]
    public void Module_UsesHostConfigurationAndRegistersStartupValidation()
    {
        var module = new NamedCanaryLabModule();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(CreateConfiguration());

        module.ConfigureServices(
            new StartupContext([], module, EnvironmentProvider: new TestEnvironmentProvider()),
            services);

        using var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<CanaryLabSettings>();

        Assert.Equal(CanaryLabScenario.Pass, settings.Scenario);
        Assert.Contains(
            provider.GetServices<IHostedService>(),
            service => service is CanaryLabStartupValidationService);
    }

    [Fact]
    public void Module_RejectsNonDevelopmentWhenSettingsAreResolved()
    {
        var module = new NamedCanaryLabModule();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(CreateConfiguration());
        module.ConfigureServices(
            new StartupContext([], module, EnvironmentProvider: new TestEnvironmentProvider(isDevelopment: false)),
            services);

        using var provider = services.BuildServiceProvider();
        var exception = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<CanaryLabSettings>());

        Assert.Equal("The named-canary lab may run only in the Development environment.", exception.Message);
    }

    [Fact]
    public async Task Module_ExecutesAllLifecycleHooksAndMapsTheLocalStatusRoute()
    {
        var module = new NamedCanaryLabModule();
        var context = new StartupContext([], module, EnvironmentProvider: new TestEnvironmentProvider());
        var dependencies = new ModuleDependencyBuilder();
        module.RegisterDependentModules(dependencies);
        Assert.Empty(dependencies.Modules);

        var hostBuilder = new HostBuilder();
        module.ConfigureHostBeforeServices(context, hostBuilder);
        module.ConfigureHostAfterServices(context, hostBuilder);

        var builder = WebApplication.CreateBuilder(
            new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(CreateValues());
        module.ConfigureServices(context, builder.Services);

        await using var app = builder.Build();
        module.ConfigureEndpointAwareMiddleware(context, app);
        module.ConfigureEndpoints(context, app);
        await app.StartAsync();

        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();
        using var anonymousCanaryResponse = await client.GetAsync($"/_appsurface/canaries/{NamedCanaryLabApp.CanaryName}");

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("AppSurface named-canary lab is running.", body);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, anonymousCanaryResponse.StatusCode);
    }

    internal static IConfiguration CreateConfiguration() =>
        new ConfigurationBuilder().AddInMemoryCollection(CreateValues()).Build();

    internal static Dictionary<string, string?> CreateValues() => new(StringComparer.Ordinal)
    {
        [$"{CanaryLabSettings.SectionName}:OperatorToken"] = "operator-token-sentinel",
        [$"{CanaryLabSettings.SectionName}:Candidate"] = "candidate-sentinel",
        [$"{CanaryLabSettings.SectionName}:Environment"] = "development",
        [$"{CanaryLabSettings.SectionName}:Scenario"] = "Pass",
    };

    private sealed class TestEnvironmentProvider(bool isDevelopment = true) : IEnvironmentProvider
    {
        public string Environment => isDevelopment ? Environments.Development : Environments.Production;

        public bool IsDevelopment => isDevelopment;

        public string? GetEnvironmentVariable(string name, string? defaultValue = null) => defaultValue;
    }
}
