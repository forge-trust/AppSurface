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
        var allowedAuthorizer = new AppSurfaceDocsHarvestChannelAuthorizer(
            Options(AppSurfaceDocsHarvestHealthExposure.Never),
            new TestHostEnvironment { EnvironmentName = Environments.Production },
            new TestChannelAuthorizer(allow: true));
        var deniedAuthorizer = new AppSurfaceDocsHarvestChannelAuthorizer(
            Options(AppSurfaceDocsHarvestHealthExposure.Never),
            new TestHostEnvironment { EnvironmentName = Environments.Production },
            new TestChannelAuthorizer(allow: false));

        Assert.True(await allowedAuthorizer.CanSubscribeAsync(new DefaultHttpContext(), "app-notifications"));
        Assert.False(await deniedAuthorizer.CanSubscribeAsync(new DefaultHttpContext(), "app-notifications"));
    }

    [Fact]
    public async Task CanSubscribeAsync_WhenChannelIsNotHarvestProgressAndNoInnerAuthorizer_Denies()
    {
        var authorizer = new AppSurfaceDocsHarvestChannelAuthorizer(
            Options(AppSurfaceDocsHarvestHealthExposure.Always),
            new TestHostEnvironment { EnvironmentName = Environments.Production });

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

        Assert.True(await developmentAuthorizer.CanSubscribeAsync(
            new DefaultHttpContext(),
            AppSurfaceDocsStreamAuthorization.HarvestProgressChannel));
        Assert.False(await productionAuthorizer.CanSubscribeAsync(
            new DefaultHttpContext(),
            AppSurfaceDocsStreamAuthorization.HarvestProgressChannel));
    }

    [Fact]
    public async Task CanSubscribeAsync_WhenProductionHarvestProgressExposureIsAlwaysWithoutCustomAuthorizer_Denies()
    {
        var always = new AppSurfaceDocsHarvestChannelAuthorizer(
            Options(AppSurfaceDocsHarvestHealthExposure.Always),
            new TestHostEnvironment { EnvironmentName = Environments.Production });

        Assert.False(await always.CanSubscribeAsync(
            new DefaultHttpContext(),
            AppSurfaceDocsStreamAuthorization.HarvestProgressChannel));
    }

    [Fact]
    public async Task CanSubscribeAsync_WhenHarvestProgressExposureIsNever_DeniesEvenInDevelopment()
    {
        var never = new AppSurfaceDocsHarvestChannelAuthorizer(
            Options(AppSurfaceDocsHarvestHealthExposure.Never),
            new TestHostEnvironment { EnvironmentName = Environments.Development });

        Assert.False(await never.CanSubscribeAsync(
            new DefaultHttpContext(),
            AppSurfaceDocsStreamAuthorization.HarvestProgressChannel));
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

        Assert.True(await allowed.CanSubscribeAsync(context, AppSurfaceDocsStreamAuthorization.HarvestProgressChannel));
        Assert.False(await deniedByInner.CanSubscribeAsync(context, AppSurfaceDocsStreamAuthorization.HarvestProgressChannel));
        Assert.False(await deniedByHarvestVisibility.CanSubscribeAsync(
            context,
            AppSurfaceDocsStreamAuthorization.HarvestProgressChannel));
    }

    [Fact]
    public async Task CanSubscribeAsync_WhenHarvestProgressHasBuiltInAuthorizerOutsideDevelopment_Denies()
    {
        var context = new DefaultHttpContext();
        var denyAll = new AppSurfaceDocsHarvestChannelAuthorizer(
            Options(AppSurfaceDocsHarvestHealthExposure.Always),
            new TestHostEnvironment { EnvironmentName = Environments.Production },
            new DenyAllRazorWireChannelAuthorizer());
        var allowAll = new AppSurfaceDocsHarvestChannelAuthorizer(
            Options(AppSurfaceDocsHarvestHealthExposure.Always),
            new TestHostEnvironment { EnvironmentName = Environments.Production },
            new AllowAllRazorWireChannelAuthorizer());

        Assert.False(await denyAll.CanSubscribeAsync(context, AppSurfaceDocsStreamAuthorization.HarvestProgressChannel));
        Assert.False(await allowAll.CanSubscribeAsync(context, AppSurfaceDocsStreamAuthorization.HarvestProgressChannel));
        Assert.True(await allowAll.CanSubscribeAsync(context, "public-host-channel"));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("appsurfacedocs-harvest", true)]
    [InlineData("AppSurfaceDocs-Harvest", false)]
    [InlineData("host-channel", false)]
    public void IsHarvestProgressChannel_ShouldMatchOnlyExactHarvestChannel(string? channel, bool expected)
    {
        Assert.Equal(expected, AppSurfaceDocsStreamAuthorization.IsHarvestProgressChannel(channel));
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
