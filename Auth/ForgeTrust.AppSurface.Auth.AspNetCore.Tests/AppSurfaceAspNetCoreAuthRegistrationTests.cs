using ForgeTrust.AppSurface.Auth.AspNetCore;
using ForgeTrust.AppSurface.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Auth.AspNetCore.Tests;

public sealed class AppSurfaceAspNetCoreAuthRegistrationTests
{
    [Fact]
    public void AddAppSurfaceAspNetCoreAuth_RegistersAdapterServicesAndOptions()
    {
        var services = new ServiceCollection();

        services.AddAppSurfaceAspNetCoreAuth(options => options.MapSubjectClaim("custom-sub"));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var adapterOptions = scope.ServiceProvider.GetRequiredService<IOptions<AppSurfaceAspNetCoreAuthOptions>>().Value;

        Assert.Equal("custom-sub", adapterOptions.SubjectClaimTypes[0]);
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IAppSurfaceAspNetCoreAuthContextAccessor>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IAppSurfaceAspNetCorePolicyEvaluator>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IOptions<AppSurfaceAuthOptions>>().Value);
    }

    [Fact]
    public void AddAppSurfaceAspNetCoreAuth_DoesNotRegisterHostOwnedAuthenticationOrAuthorization()
    {
        var services = new ServiceCollection();

        services.AddAppSurfaceAspNetCoreAuth();

        using var provider = services.BuildServiceProvider();

        Assert.Null(provider.GetService<IAuthenticationService>());
        Assert.Null(provider.GetService<IAuthorizationPolicyProvider>());
        Assert.Null(provider.GetService<IPolicyEvaluator>());
    }

    [Fact]
    public void Module_RegistersAdapterServicesAndNeutralAuthDependency()
    {
        var services = new ServiceCollection();
        var module = new AppSurfaceAspNetCoreAuthModule();

        module.ConfigureServices(new StartupContext([], new TestHostModule()), services);
        var builder = new ModuleDependencyBuilder();
        module.RegisterDependentModules(builder);

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IAppSurfaceAspNetCoreAuthContextAccessor>());
        Assert.Contains(builder.Modules, dependency => dependency is AppSurfaceAuthModule);
    }

    private sealed class TestHostModule : IAppSurfaceHostModule
    {
        public void ConfigureHostBeforeServices(StartupContext context, Microsoft.Extensions.Hosting.IHostBuilder builder)
        {
        }

        public void ConfigureHostAfterServices(StartupContext context, Microsoft.Extensions.Hosting.IHostBuilder builder)
        {
        }

        public void ConfigureServices(StartupContext context, IServiceCollection services)
        {
        }

        public void RegisterDependentModules(ModuleDependencyBuilder builder)
        {
        }
    }
}
