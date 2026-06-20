using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Observability.Tests;

public sealed class AppSurfaceObservabilityRegistrationTests
{
    [Fact]
    public void ConfigureAppSurfaceObservability_BindsAndValidatesOptions()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            CreateConfiguration(
                ("AppSurfaceObservability:ExporterMode", "Always"),
                ("AppSurfaceObservability:OtlpEndpoint", "http://collector:4317"),
                ("AppSurfaceObservability:ServiceName", "orders-api")));

        services.ConfigureAppSurfaceObservability(options => options.ServiceVersion = "1.2.3");

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AppSurfaceObservabilityOptions>>().Value;

        Assert.Equal(AppSurfaceOtlpExporterMode.Always, options.ExporterMode);
        Assert.Equal(new Uri("http://collector:4317"), options.OtlpEndpoint);
        Assert.Equal("orders-api", options.ServiceName);
        Assert.Equal("1.2.3", options.ServiceVersion);
    }

    [Fact]
    public void AddAppSurfaceObservability_RegistersServicesOnce()
    {
        var services = new ServiceCollection();
        var context = CreateContext("Catalog API");
        var configuration = CreateConfiguration();

        services.AddAppSurfaceObservability(context, configuration);
        services.AddAppSurfaceObservability(context, configuration);

        Assert.Single(services, static service =>
            service.ServiceType == typeof(AppSurfaceObservabilityServicesRegistrationMarker));
        Assert.Single(services, static service =>
            service.ServiceType == typeof(IHostedService)
            && service.ImplementationType == typeof(AppSurfaceObservabilityStartupDiagnostic));
    }

    [Fact]
    public void AddAppSurfaceObservability_RegistersPlanWithServiceIdentity()
    {
        var services = new ServiceCollection();

        services.AddAppSurfaceObservability(
            CreateContext("Catalog API"),
            CreateConfiguration(("AppSurfaceObservability:ServiceName", "orders-api")));

        using var provider = services.BuildServiceProvider();
        var plan = provider.GetRequiredService<AppSurfaceObservabilityPlan>();

        Assert.Equal("orders-api", plan.ServiceName);
    }

    [Fact]
    public void AddAppSurfaceObservability_BindsOptionsFromSuppliedConfiguration()
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration(
            ("AppSurfaceObservability:ExporterMode", "Never"),
            ("AppSurfaceObservability:ServiceName", "orders-api"));

        services.AddAppSurfaceObservability(CreateContext("Catalog API"), configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AppSurfaceObservabilityOptions>>().Value;

        Assert.Equal(AppSurfaceOtlpExporterMode.Never, options.ExporterMode);
        Assert.Equal("orders-api", options.ServiceName);
        Assert.Equal(
            "orders-api",
            provider.GetRequiredService<AppSurfaceObservabilityPlan>().ServiceName);
    }

    [Fact]
    public void AddAppSurfaceObservability_RepeatedCallsKeepFirstConfiguration()
    {
        var services = new ServiceCollection();
        var context = CreateContext("Catalog API");
        var configuration = CreateConfiguration();

        services.AddAppSurfaceObservability(
            context,
            configuration,
            options => options.ServiceName = "orders-api");
        services.AddAppSurfaceObservability(
            context,
            configuration,
            options => options.ServiceName = "billing-api");

        using var provider = services.BuildServiceProvider();

        Assert.Equal(
            "orders-api",
            provider.GetRequiredService<AppSurfaceObservabilityPlan>().ServiceName);
        Assert.Equal(
            "orders-api",
            provider.GetRequiredService<IOptions<AppSurfaceObservabilityOptions>>().Value.ServiceName);
    }

    [Fact]
    public void AddAppSurfaceObservabilityLogging_RegistersLoggingOnce()
    {
        var services = new ServiceCollection();
        var logging = new LoggingBuilderStub(services);
        var context = CreateContext("Catalog API");
        var configuration = CreateConfiguration();

        logging.AddAppSurfaceObservabilityLogging(context, configuration);
        logging.AddAppSurfaceObservabilityLogging(context, configuration);

        Assert.Single(services, static service =>
            service.ServiceType == typeof(AppSurfaceObservabilityLoggingRegistrationMarker));
    }

    [Fact]
    public void AddAppSurfaceObservabilityLogging_RepeatedCallsKeepFirstConfiguration()
    {
        var services = new ServiceCollection();
        var logging = new LoggingBuilderStub(services);
        var context = CreateContext("Catalog API");
        var configuration = CreateConfiguration();

        logging.AddAppSurfaceObservabilityLogging(
            context,
            configuration,
            options => options.ServiceName = "orders-api");
        logging.AddAppSurfaceObservabilityLogging(
            context,
            configuration,
            options => options.ServiceName = "billing-api");

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AppSurfaceObservabilityOptions>>().Value;

        Assert.Equal("orders-api", options.ServiceName);
    }

    [Fact]
    public void AddAppSurfaceObservabilityLogging_BindsOptionsFromSuppliedConfiguration()
    {
        var services = new ServiceCollection();
        var logging = new LoggingBuilderStub(services);
        var configuration = CreateConfiguration(
            ("AppSurfaceObservability:ExporterMode", "Never"),
            ("AppSurfaceObservability:ServiceName", "orders-api"));

        logging.AddAppSurfaceObservabilityLogging(CreateContext("Catalog API"), configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AppSurfaceObservabilityOptions>>().Value;

        Assert.Equal(AppSurfaceOtlpExporterMode.Never, options.ExporterMode);
        Assert.Equal("orders-api", options.ServiceName);
    }

    [Fact]
    public void ConfigureAppSurfaceObservability_RepeatedHostBuilderCallsKeepFirstConfiguration()
    {
        var context = CreateContext("Catalog API");
        using var host = Host.CreateDefaultBuilder([])
            .ConfigureAppConfiguration(configuration =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AppSurfaceObservability:ExporterMode"] = "Never"
                });
            })
            .ConfigureAppSurfaceObservability(
                context,
                options => options.ServiceName = "orders-api")
            .ConfigureAppSurfaceObservability(
                context,
                options => options.ServiceName = "billing-api")
            .Build();

        Assert.Equal(
            "orders-api",
            host.Services.GetRequiredService<AppSurfaceObservabilityPlan>().ServiceName);
        Assert.Equal(
            "orders-api",
            host.Services.GetRequiredService<IOptions<AppSurfaceObservabilityOptions>>().Value.ServiceName);
    }

    [Fact]
    public async Task StartupDiagnostic_LogsSkippedExporterMessageOnce()
    {
        var logger = new CapturingLogger<AppSurfaceObservabilityStartupDiagnostic>();
        var plan = AppSurfaceObservabilityPlan.Resolve(
            CreateContext("Catalog API"),
            CreateConfiguration(),
            environment: new TestEnvironmentReader());
        var diagnostic = new AppSurfaceObservabilityStartupDiagnostic(plan, logger);

        await diagnostic.StartAsync(CancellationToken.None);
        await diagnostic.StartAsync(CancellationToken.None);

        Assert.Equal(2, logger.Messages.Count);
        Assert.All(logger.Messages, message =>
            Assert.Contains("no endpoint was configured", message, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Module_UsesHostBuilderHooksToRegisterObservability()
    {
        var module = new AppSurfaceObservabilityModule();
        var context = CreateContext("Catalog API");
        using var host = Host.CreateDefaultBuilder([])
            .ConfigureAppConfiguration(configuration =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AppSurfaceObservability:ExporterMode"] = "Never"
                });
            })
            .ConfigureServices(services => module.ConfigureServices(context, services))
            .ConfigureHostConfiguration(configuration => { })
            .UseConsoleLifetime()
            .ConfigureServices((_, services) =>
            {
                module.ConfigureHostBeforeServices(context, new DelegatingHostBuilder(services));
                module.ConfigureHostAfterServices(context, new DelegatingHostBuilder(services));
            })
            .Build();

        var options = host.Services.GetRequiredService<IOptions<AppSurfaceObservabilityOptions>>().Value;
        Assert.Equal(AppSurfaceOtlpExporterMode.Never, options.ExporterMode);
    }

    private static StartupContext CreateContext(string applicationName)
    {
        return new StartupContext([], new TestHostModule(), applicationName);
    }

    private static IConfiguration CreateConfiguration(params (string Key, string Value)[] values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build();
    }

    private sealed class LoggingBuilderStub(IServiceCollection services) : ILoggingBuilder
    {
        public IServiceCollection Services { get; } = services;
    }
}
