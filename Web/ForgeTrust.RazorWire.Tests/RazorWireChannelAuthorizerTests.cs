using ForgeTrust.AppSurface.Auth;
using ForgeTrust.RazorWire.Streams;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.RazorWire.Tests;

public class RazorWireChannelAuthorizerTests
{
    [Fact]
    public async Task DenyAllAuthorizer_ReturnsFalse()
    {
        var authorizer = new DenyAllRazorWireChannelAuthorizer();

        var canSubscribe = await authorizer.CanSubscribeAsync(new DefaultHttpContext(), "public");

        Assert.False(canSubscribe);
    }

    [Fact]
    public async Task AllowAllAuthorizer_ReturnsTrue()
    {
        var authorizer = new AllowAllRazorWireChannelAuthorizer();

        var canSubscribe = await authorizer.CanSubscribeAsync(new DefaultHttpContext(), "public");

        Assert.True(canSubscribe);
    }

    [Fact]
    public void AddRazorWire_DefaultConfiguration_ResolvesDenyAllAuthorizer()
    {
        var services = new ServiceCollection();

        services.AddRazorWire();

        using var provider = services.BuildServiceProvider();
        Assert.IsType<DenyAllRazorWireChannelAuthorizer>(
            provider.GetRequiredService<IRazorWireChannelAuthorizer>());
    }

    [Fact]
    public void AddRazorWire_AllowAllConfiguration_ResolvesAllowAllAuthorizer()
    {
        var services = new ServiceCollection();

        services.AddRazorWire(options =>
        {
            options.Streams.AuthorizationMode = RazorWireStreamAuthorizationMode.AllowAll;
        });

        using var provider = services.BuildServiceProvider();
        Assert.IsType<AllowAllRazorWireChannelAuthorizer>(
            provider.GetRequiredService<IRazorWireChannelAuthorizer>());
    }

    [Fact]
    public void AddRazorWire_CustomAuthorizerRegisteredBeforeAddRazorWire_Wins()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRazorWireChannelAuthorizer, CustomAllowAuthorizer>();

        services.AddRazorWire(options =>
        {
            options.Streams.AuthorizationMode = RazorWireStreamAuthorizationMode.DenyAll;
        });

        using var provider = services.BuildServiceProvider();
        Assert.IsType<CustomAllowAuthorizer>(
            provider.GetRequiredService<IRazorWireChannelAuthorizer>());
    }

    [Fact]
    public async Task AddRazorWire_CustomDenyAuthorizerRegisteredBeforeAddRazorWire_DeniesWhenConfigAllowsAll()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRazorWireChannelAuthorizer, CustomDenyAuthorizer>();

        services.AddRazorWire(options =>
        {
            options.Streams.AuthorizationMode = RazorWireStreamAuthorizationMode.AllowAll;
        });

        using var provider = services.BuildServiceProvider();
        var authorizer = provider.GetRequiredService<IRazorWireChannelAuthorizer>();

        Assert.False(await authorizer.CanSubscribeAsync(new DefaultHttpContext(), "public"));
    }

    [Fact]
    public void AddRazorWire_CustomAuthorizerRegisteredAfterAddRazorWire_Wins()
    {
        var services = new ServiceCollection();

        services.AddRazorWire(options =>
        {
            options.Streams.AuthorizationMode = RazorWireStreamAuthorizationMode.AllowAll;
        });
        services.AddSingleton<IRazorWireChannelAuthorizer, CustomDenyAuthorizer>();

        using var provider = services.BuildServiceProvider();
        Assert.IsType<CustomDenyAuthorizer>(
            provider.GetRequiredService<IRazorWireChannelAuthorizer>());
    }

    [Fact]
    public async Task AddRazorWire_CustomDenyAuthorizerRegisteredAfterAddRazorWire_DeniesWhenConfigAllowsAll()
    {
        var services = new ServiceCollection();

        services.AddRazorWire(options =>
        {
            options.Streams.AuthorizationMode = RazorWireStreamAuthorizationMode.AllowAll;
        });
        services.AddSingleton<IRazorWireChannelAuthorizer, CustomDenyAuthorizer>();

        using var provider = services.BuildServiceProvider();
        var authorizer = provider.GetRequiredService<IRazorWireChannelAuthorizer>();

        Assert.False(await authorizer.CanSubscribeAsync(new DefaultHttpContext(), "public"));
    }

    [Fact]
    public async Task AddRazorWire_DefaultConfiguration_ResolvesResultAdapterOverDenyAll()
    {
        var services = new ServiceCollection();

        services.AddRazorWire();

        using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        var authorizer = provider.GetRequiredService<IRazorWireStreamAuthorizer>();
        var result = await authorizer.AuthorizeAsync(
            new RazorWireStreamAuthorizationContext(
                context,
                "public",
                RazorWireStreamAuthorizationMode.DenyAll));

        Assert.Equal(AppSurfaceAuthOutcome.Forbid, result.Outcome);
        Assert.Equal(AppSurfaceAuthReason.Forbidden, result.Reason);
    }

    [Fact]
    public async Task AddRazorWire_AllowAllConfiguration_ResolvesResultAdapterOverAllowAll()
    {
        var services = new ServiceCollection();

        services.AddRazorWire(options =>
        {
            options.Streams.AuthorizationMode = RazorWireStreamAuthorizationMode.AllowAll;
        });

        using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        var authorizer = provider.GetRequiredService<IRazorWireStreamAuthorizer>();
        var result = await authorizer.AuthorizeAsync(
            new RazorWireStreamAuthorizationContext(
                context,
                "public",
                RazorWireStreamAuthorizationMode.AllowAll));

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public async Task AddRazorWire_CustomResultAuthorizerRegisteredBeforeAddRazorWire_Wins()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRazorWireStreamAuthorizer, CustomChallengeStreamAuthorizer>();

        services.AddRazorWire(options =>
        {
            options.Streams.AuthorizationMode = RazorWireStreamAuthorizationMode.AllowAll;
        });

        using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        var authorizer = provider.GetRequiredService<IRazorWireStreamAuthorizer>();
        var result = await authorizer.AuthorizeAsync(
            new RazorWireStreamAuthorizationContext(
                context,
                "public",
                RazorWireStreamAuthorizationMode.AllowAll));

        Assert.IsType<CustomChallengeStreamAuthorizer>(authorizer);
        Assert.Equal(AppSurfaceAuthOutcome.Challenge, result.Outcome);
    }

    [Fact]
    public async Task AddRazorWire_CustomResultAuthorizerRegisteredAfterAddRazorWire_Wins()
    {
        var services = new ServiceCollection();

        services.AddRazorWire(options =>
        {
            options.Streams.AuthorizationMode = RazorWireStreamAuthorizationMode.AllowAll;
        });
        services.AddSingleton<IRazorWireStreamAuthorizer, CustomChallengeStreamAuthorizer>();

        using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        var authorizer = provider.GetRequiredService<IRazorWireStreamAuthorizer>();
        var result = await authorizer.AuthorizeAsync(
            new RazorWireStreamAuthorizationContext(
                context,
                "public",
                RazorWireStreamAuthorizationMode.AllowAll));

        Assert.IsType<CustomChallengeStreamAuthorizer>(authorizer);
        Assert.Equal(AppSurfaceAuthOutcome.Challenge, result.Outcome);
    }

    [Fact]
    public async Task ResultAdapter_ResolvesScopedBoolAuthorizerFromRequestServices()
    {
        var services = new ServiceCollection();
        services.AddScoped<IRazorWireChannelAuthorizer, ScopedCountingAuthorizer>();

        services.AddRazorWire();

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        var authorizer = provider.GetRequiredService<IRazorWireStreamAuthorizer>();
        var scoped = scope.ServiceProvider.GetRequiredService<IRazorWireChannelAuthorizer>() as ScopedCountingAuthorizer;

        var result = await authorizer.AuthorizeAsync(
            new RazorWireStreamAuthorizationContext(
                context,
                "public",
                RazorWireStreamAuthorizationMode.DenyAll));

        Assert.True(result.IsAllowed);
        Assert.Equal(1, scoped?.CallCount);
    }

    [Fact]
    public void AddRazorWire_InvalidAuthorizationMode_ThrowsClearConfigurationError()
    {
        var services = new ServiceCollection();

        services.AddRazorWire(options =>
        {
            options.Streams.AuthorizationMode = (RazorWireStreamAuthorizationMode)99;
        });

        using var provider = services.BuildServiceProvider();
        var exception = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IRazorWireChannelAuthorizer>());

        Assert.Contains("Unknown RazorWire stream authorization mode '99'", exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(IRazorWireChannelAuthorizer), exception.Message, StringComparison.Ordinal);
    }

    private sealed class CustomAllowAuthorizer : IRazorWireChannelAuthorizer
    {
        public ValueTask<bool> CanSubscribeAsync(HttpContext context, string channel)
        {
            return new ValueTask<bool>(true);
        }
    }

    private sealed class CustomDenyAuthorizer : IRazorWireChannelAuthorizer
    {
        public ValueTask<bool> CanSubscribeAsync(HttpContext context, string channel)
        {
            return new ValueTask<bool>(false);
        }
    }

    private sealed class CustomChallengeStreamAuthorizer : IRazorWireStreamAuthorizer
    {
        public ValueTask<AppSurfaceAuthResult> AuthorizeAsync(RazorWireStreamAuthorizationContext context)
        {
            return new ValueTask<AppSurfaceAuthResult>(AppSurfaceAuthResult.Unauthenticated());
        }
    }

    private sealed class ScopedCountingAuthorizer : IRazorWireChannelAuthorizer
    {
        public int CallCount { get; private set; }

        public ValueTask<bool> CanSubscribeAsync(HttpContext context, string channel)
        {
            CallCount++;
            return new ValueTask<bool>(true);
        }
    }
}
