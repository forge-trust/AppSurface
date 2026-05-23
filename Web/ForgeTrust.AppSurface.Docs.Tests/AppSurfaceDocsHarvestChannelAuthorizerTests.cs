using ForgeTrust.AppSurface.Docs.Services;
using ForgeTrust.RazorWire.Streams;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class AppSurfaceDocsHarvestChannelAuthorizerTests
{
    [Fact]
    public async Task CanSubscribeAsync_WhenChannelIsNotHarvestProgress_DelegatesToInnerAuthorizer()
    {
        var authorizer = new AppSurfaceDocsHarvestChannelAuthorizer(
            Options(AppSurfaceDocsHarvestHealthExposure.Never),
            new TestHostEnvironment { EnvironmentName = Environments.Production },
            new TestChannelAuthorizer(allow: false));

        Assert.False(await authorizer.CanSubscribeAsync(new DefaultHttpContext(), "app-notifications"));
    }

    [Fact]
    public async Task CanSubscribeAsync_WhenHarvestProgressUsesDevelopmentExposure_AllowsDevelopmentOnly()
    {
        var options = Options(AppSurfaceDocsHarvestHealthExposure.DevelopmentOnly);

        var developmentAuthorizer = new AppSurfaceDocsHarvestChannelAuthorizer(
            options,
            new TestHostEnvironment { EnvironmentName = Environments.Development });
        var productionAuthorizer = new AppSurfaceDocsHarvestChannelAuthorizer(
            options,
            new TestHostEnvironment { EnvironmentName = Environments.Production });

        Assert.True(await developmentAuthorizer.CanSubscribeAsync(new DefaultHttpContext(), AppSurfaceDocsHarvestProgressReporter.ChannelName));
        Assert.False(await productionAuthorizer.CanSubscribeAsync(new DefaultHttpContext(), AppSurfaceDocsHarvestProgressReporter.ChannelName));
    }

    [Fact]
    public async Task CanSubscribeAsync_WhenHarvestProgressExposureIsAlwaysOrNever_UsesConfiguredExposure()
    {
        var always = new AppSurfaceDocsHarvestChannelAuthorizer(
            Options(AppSurfaceDocsHarvestHealthExposure.Always),
            new TestHostEnvironment { EnvironmentName = Environments.Production });
        var never = new AppSurfaceDocsHarvestChannelAuthorizer(
            Options(AppSurfaceDocsHarvestHealthExposure.Never),
            new TestHostEnvironment { EnvironmentName = Environments.Development });

        Assert.True(await always.CanSubscribeAsync(new DefaultHttpContext(), AppSurfaceDocsHarvestProgressReporter.ChannelName));
        Assert.False(await never.CanSubscribeAsync(new DefaultHttpContext(), AppSurfaceDocsHarvestProgressReporter.ChannelName));
    }

    [Fact]
    public async Task CanSubscribeAsync_WhenHarvestProgressHasInnerAuthorizer_RequiresBothPolicies()
    {
        var context = new DefaultHttpContext();
        var allowed = new AppSurfaceDocsHarvestChannelAuthorizer(
            Options(AppSurfaceDocsHarvestHealthExposure.Always),
            new TestHostEnvironment { EnvironmentName = Environments.Production },
            new TestChannelAuthorizer(allow: true));
        var deniedByInner = new AppSurfaceDocsHarvestChannelAuthorizer(
            Options(AppSurfaceDocsHarvestHealthExposure.Always),
            new TestHostEnvironment { EnvironmentName = Environments.Production },
            new TestChannelAuthorizer(allow: false));
        var deniedByHarvestVisibility = new AppSurfaceDocsHarvestChannelAuthorizer(
            Options(AppSurfaceDocsHarvestHealthExposure.Never),
            new TestHostEnvironment { EnvironmentName = Environments.Production },
            new TestChannelAuthorizer(allow: true));

        Assert.True(await allowed.CanSubscribeAsync(context, AppSurfaceDocsHarvestProgressReporter.ChannelName));
        Assert.False(await deniedByInner.CanSubscribeAsync(context, AppSurfaceDocsHarvestProgressReporter.ChannelName));
        Assert.False(await deniedByHarvestVisibility.CanSubscribeAsync(context, AppSurfaceDocsHarvestProgressReporter.ChannelName));
    }

    private static AppSurfaceDocsOptions Options(AppSurfaceDocsHarvestHealthExposure exposure)
    {
        return new AppSurfaceDocsOptions
        {
            Harvest = new AppSurfaceDocsHarvestOptions
            {
                Health = new AppSurfaceDocsHarvestHealthOptions
                {
                    ExposeRoutes = exposure
                }
            }
        };
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "AppSurfaceDocsTests";

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();

        public string ContentRootPath { get; set; } = Path.GetTempPath();

        public string EnvironmentName { get; set; } = Environments.Development;
    }

    private sealed class TestChannelAuthorizer(bool allow) : IRazorWireChannelAuthorizer
    {
        public ValueTask<bool> CanSubscribeAsync(HttpContext context, string channel)
        {
            return new ValueTask<bool>(allow);
        }
    }
}
