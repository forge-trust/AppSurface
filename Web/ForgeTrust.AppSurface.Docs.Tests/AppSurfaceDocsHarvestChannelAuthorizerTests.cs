using ForgeTrust.AppSurface.Auth;
using ForgeTrust.AppSurface.Docs.Services;
using ForgeTrust.RazorWire;
using ForgeTrust.RazorWire.Streams;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class AppSurfaceDocsHarvestChannelAuthorizerTests
{
    [Fact]
    public void Constructor_WhenOptionsIsNull_Throws()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => new AppSurfaceDocsHarvestStreamAuthorizer(
                null!,
                new TestHostEnvironment()));

        Assert.Equal("options", exception.ParamName);
    }

    [Fact]
    public void Constructor_WhenEnvironmentIsNull_Throws()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => new AppSurfaceDocsHarvestStreamAuthorizer(
                Options(AppSurfaceDocsHarvestHealthExposure.Always),
                null!));

        Assert.Equal("environment", exception.ParamName);
    }

    [Fact]
    public void Constructor_WhenStreamAuthorizerIsNull_Throws()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => new AppSurfaceDocsHarvestChannelAuthorizer(null!));

        Assert.Equal("streamAuthorizer", exception.ParamName);
    }

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
    public async Task CanSubscribeAsync_WhenRazorWireOptionsAreRegistered_PassesConfiguredAuthorizationMode()
    {
        var streamAuthorizer = new RecordingStreamAuthorizer(AppSurfaceAuthResult.Allowed());
        var authorizer = new AppSurfaceDocsHarvestChannelAuthorizer(streamAuthorizer);
        await using var services = new ServiceCollection()
            .AddSingleton(
                new RazorWireOptions
                {
                    Streams =
                    {
                        AuthorizationMode = RazorWireStreamAuthorizationMode.AllowAll
                    }
                })
            .BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = services };

        Assert.True(await authorizer.CanSubscribeAsync(context, "app-notifications"));
        Assert.Equal(RazorWireStreamAuthorizationMode.AllowAll, streamAuthorizer.LastAuthorizationMode);
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

    [Fact]
    public async Task StreamAuthorizeAsync_WhenChannelIsNotHarvestProgress_DelegatesToInnerResultAuthorizer()
    {
        var authorizer = new AppSurfaceDocsHarvestStreamAuthorizer(
            Options(AppSurfaceDocsHarvestHealthExposure.Never),
            new TestHostEnvironment { EnvironmentName = Environments.Production },
            new TestStreamAuthorizer(AppSurfaceAuthResult.Unauthenticated()));

        var result = await authorizer.AuthorizeAsync(Context("app-notifications"));

        Assert.Equal(AppSurfaceAuthOutcome.Challenge, result.Outcome);
    }

    [Fact]
    public async Task StreamAuthorizeAsync_WhenProductionHarvestProgressHasInnerResultAuthorizer_RequiresInnerAllowedResult()
    {
        var context = Context(AppSurfaceDocsStreamAuthorization.HarvestProgressChannel);
        var allowed = new AppSurfaceDocsHarvestStreamAuthorizer(
            Options(AppSurfaceDocsHarvestHealthExposure.Always),
            new TestHostEnvironment { EnvironmentName = Environments.Production },
            new TestStreamAuthorizer(AppSurfaceAuthResult.Allowed()));
        var denied = new AppSurfaceDocsHarvestStreamAuthorizer(
            Options(AppSurfaceDocsHarvestHealthExposure.Always),
            new TestHostEnvironment { EnvironmentName = Environments.Production },
            new TestStreamAuthorizer(AppSurfaceAuthResult.Forbidden()));

        Assert.True((await allowed.AuthorizeAsync(context)).IsAllowed);
        Assert.Equal(AppSurfaceAuthOutcome.Forbid, (await denied.AuthorizeAsync(context)).Outcome);
    }

    [Fact]
    public async Task StreamAuthorizeAsync_WhenProductionHarvestProgressHasBuiltInBoolAuthorizer_DeniesHarvestButDelegatesHostChannels()
    {
        var authorizer = new AppSurfaceDocsHarvestStreamAuthorizer(
            Options(AppSurfaceDocsHarvestHealthExposure.Always),
            new TestHostEnvironment { EnvironmentName = Environments.Production },
            innerChannelAuthorizer: new AllowAllRazorWireChannelAuthorizer());

        var harvest = await authorizer.AuthorizeAsync(Context(AppSurfaceDocsStreamAuthorization.HarvestProgressChannel));
        var host = await authorizer.AuthorizeAsync(Context("public-host-channel"));

        Assert.Equal(AppSurfaceAuthOutcome.Forbid, harvest.Outcome);
        Assert.True(host.IsAllowed);
    }

    [Theory]
    [InlineData(true, AppSurfaceAuthOutcome.Allowed)]
    [InlineData(false, AppSurfaceAuthOutcome.Forbid)]
    public async Task StreamAuthorizeAsync_WhenReplacementChannelAuthorizerIsRegistered_UsesReplacementBeforeDocsPolicy(
        bool allow,
        AppSurfaceAuthOutcome expectedOutcome)
    {
        await using var services = new ServiceCollection()
            .AddSingleton<IRazorWireChannelAuthorizer>(new TestChannelAuthorizer(allow))
            .BuildServiceProvider();
        var authorizer = new AppSurfaceDocsHarvestStreamAuthorizer(
            Options(AppSurfaceDocsHarvestHealthExposure.Never),
            new TestHostEnvironment { EnvironmentName = Environments.Production });
        var context = Context(AppSurfaceDocsStreamAuthorization.HarvestProgressChannel, services);

        var result = await authorizer.AuthorizeAsync(context);

        Assert.Equal(expectedOutcome, result.Outcome);
    }

    [Fact]
    public async Task StreamAuthorizeAsync_WhenDocsChannelFacadeIsRegistered_DoesNotTreatFacadeAsReplacement()
    {
        var facade = new AppSurfaceDocsHarvestChannelAuthorizer(
            new TestStreamAuthorizer(AppSurfaceAuthResult.Allowed()));
        await using var services = new ServiceCollection()
            .AddSingleton<IRazorWireChannelAuthorizer>(facade)
            .BuildServiceProvider();
        var authorizer = new AppSurfaceDocsHarvestStreamAuthorizer(
            Options(AppSurfaceDocsHarvestHealthExposure.Never),
            new TestHostEnvironment { EnvironmentName = Environments.Production });
        var context = Context(AppSurfaceDocsStreamAuthorization.HarvestProgressChannel, services);

        var result = await authorizer.AuthorizeAsync(context);

        Assert.Equal(AppSurfaceAuthOutcome.Forbid, result.Outcome);
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

    private static RazorWireStreamAuthorizationContext Context(
        string channel,
        IServiceProvider? requestServices = null)
    {
        var httpContext = new DefaultHttpContext();
        if (requestServices is not null)
        {
            httpContext.RequestServices = requestServices;
        }

        return new RazorWireStreamAuthorizationContext(
            httpContext,
            channel,
            RazorWireStreamAuthorizationMode.DenyAll);
    }

    private sealed class TestStreamAuthorizer(AppSurfaceAuthResult result) : IRazorWireStreamAuthorizer
    {
        public ValueTask<AppSurfaceAuthResult> AuthorizeAsync(RazorWireStreamAuthorizationContext context)
        {
            return new ValueTask<AppSurfaceAuthResult>(result);
        }
    }

    private sealed class RecordingStreamAuthorizer(AppSurfaceAuthResult result) : IRazorWireStreamAuthorizer
    {
        public RazorWireStreamAuthorizationMode? LastAuthorizationMode { get; private set; }

        public ValueTask<AppSurfaceAuthResult> AuthorizeAsync(RazorWireStreamAuthorizationContext context)
        {
            LastAuthorizationMode = context.ConfiguredAuthorizationMode;
            return new ValueTask<AppSurfaceAuthResult>(result);
        }
    }
}
