using ForgeTrust.AppSurface.Docs.Services;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class AppSurfaceDocsOperatorReadPolicyWarningServiceTests
{
    [Fact]
    public async Task StartAsync_WhenProductionDiagnosticsAreExposedWithoutOperatorReadPolicy_LogsStructuredWarning()
    {
        var logger = new RecordingLogger<AppSurfaceDocsOperatorReadPolicyWarningService>();
        var service = new AppSurfaceDocsOperatorReadPolicyWarningService(
            new AppSurfaceDocsOptions
            {
                Harvest = new AppSurfaceDocsHarvestOptions
                {
                    Health = new AppSurfaceDocsHarvestHealthOptions
                    {
                        ExposeRoutes = AppSurfaceDocsHarvestHealthExposure.Always
                    }
                },
                Diagnostics = new AppSurfaceDocsDiagnosticsOptions
                {
                    ExposeRouteInspector = AppSurfaceDocsHarvestHealthExposure.Always
                }
            },
            new TestHostEnvironment { EnvironmentName = Environments.Production },
            logger);

        await service.StartAsync(CancellationToken.None);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(57801, entry.EventId.Id);
        Assert.Contains("AppSurfaceDocs:Diagnostics:OperatorReadPolicy", entry.Message, StringComparison.Ordinal);
        Assert.Contains("_harvest", entry.Message, StringComparison.Ordinal);
        Assert.Contains("appsurfacedocs-harvest", entry.Message, StringComparison.Ordinal);
        Assert.Contains("_routes.json", entry.Message, StringComparison.Ordinal);
        Assert.Contains("https://forge-trust.com/docs/packages/README.md.html#protect-diagnostics-reads", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_WhenOperatorReadPolicyIsConfigured_DoesNotWarn()
    {
        var logger = new RecordingLogger<AppSurfaceDocsOperatorReadPolicyWarningService>();
        var service = new AppSurfaceDocsOperatorReadPolicyWarningService(
            new AppSurfaceDocsOptions
            {
                Harvest = new AppSurfaceDocsHarvestOptions
                {
                    Health = new AppSurfaceDocsHarvestHealthOptions
                    {
                        ExposeRoutes = AppSurfaceDocsHarvestHealthExposure.Always
                    }
                },
                Diagnostics = new AppSurfaceDocsDiagnosticsOptions
                {
                    OperatorReadPolicy = "DocsRead",
                    ExposeRouteInspector = AppSurfaceDocsHarvestHealthExposure.Always
                }
            },
            new TestHostEnvironment { EnvironmentName = Environments.Production },
            logger);

        await service.StartAsync(CancellationToken.None);

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task StartAsync_WhenDevelopmentDiagnosticsAreExposedWithoutOperatorReadPolicy_DoesNotWarn()
    {
        var logger = new RecordingLogger<AppSurfaceDocsOperatorReadPolicyWarningService>();
        var service = new AppSurfaceDocsOperatorReadPolicyWarningService(
            new AppSurfaceDocsOptions(),
            new TestHostEnvironment { EnvironmentName = Environments.Development },
            logger);

        await service.StartAsync(CancellationToken.None);

        Assert.Empty(logger.Entries);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "AppSurfaceDocsTests";

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();

        public string ContentRootPath { get; set; } = Path.GetTempPath();

        public string EnvironmentName { get; set; } = Environments.Development;
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<Entry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new Entry(logLevel, eventId, formatter(state, exception)));
        }
    }

    private sealed record Entry(LogLevel Level, EventId EventId, string Message);
}
