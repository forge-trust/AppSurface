using ForgeTrust.AppSurface.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Web.Tests;

[Collection("Cors environment variable tests")]
public class CorsOptionsTests
{
    private class TestWebModule : IAppSurfaceWebModule
    {
        public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureServices(StartupContext context, IServiceCollection services)
        {
        }

        public void RegisterDependentModules(ModuleDependencyBuilder builder)
        {
        }

        public void ConfigureWebApplication(StartupContext context, IApplicationBuilder app)
        {
        }
    }

    private class TestStartup : WebStartup<TestWebModule>
    {
        public void ConfigureServicesPublic(StartupContext context, IServiceCollection services) =>
            base.ConfigureServicesForAppType(context, services);
    }

    [Fact]
    public void Defaults_RequireExplicitHeadersAndMethods()
    {
        var options = new CorsOptions();

        Assert.Empty(options.AllowedHeaders);
        Assert.Empty(options.AllowedMethods);
    }

    [Fact]
    public async Task EnableAllOriginsInDevelopment_AllowsAnyOrigin()
    {
        var previous = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", Environments.Development);

            var startup = new TestStartup();
            startup.WithOptions(o =>
            {
                o.Cors.EnableCors = true;
                o.Cors.AllowedOrigins = ["https://example.com"];
                o.Cors.EnableAllOriginsInDevelopment = true;
            });

            var context = new StartupContext([], new TestWebModule());

            var services = new ServiceCollection();
            startup.ConfigureServicesPublic(context, services);

            using var provider = services.BuildServiceProvider();
            var policyProvider = provider.GetRequiredService<ICorsPolicyProvider>();
            var policy = await policyProvider.GetPolicyAsync(new DefaultHttpContext(), "DefaultCorsPolicy");

            Assert.True(policy!.AllowAnyOrigin);
            Assert.True(policy.AllowAnyHeader);
            Assert.True(policy.AllowAnyMethod);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previous);
        }
    }

    [Fact]
    public async Task EnableAllOriginsInDevelopment_PreservesConfiguredHeadersAndMethods()
    {
        var previous = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", Environments.Development);

            var startup = new TestStartup();
            startup.WithOptions(o =>
            {
                o.Cors.EnableCors = true;
                o.Cors.EnableAllOriginsInDevelopment = true;
                o.Cors.AllowedHeaders = ["Content-Type"];
                o.Cors.AllowedMethods = [HttpMethods.Post];
            });

            var context = new StartupContext([], new TestWebModule());

            var services = new ServiceCollection();
            startup.ConfigureServicesPublic(context, services);

            using var provider = services.BuildServiceProvider();
            var policyProvider = provider.GetRequiredService<ICorsPolicyProvider>();
            var policy = await policyProvider.GetPolicyAsync(new DefaultHttpContext(), "DefaultCorsPolicy");

            Assert.True(policy!.AllowAnyOrigin);
            Assert.False(policy.AllowAnyHeader);
            Assert.False(policy.AllowAnyMethod);
            Assert.Equal(["Content-Type"], policy.Headers);
            Assert.Equal([HttpMethods.Post], policy.Methods);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previous);
        }
    }

    [Fact]
    public async Task DisableAllOriginsOutsideDevelopment_UsesConfiguredOrigins()
    {
        var previous = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", Environments.Production);

            var startup = new TestStartup();
            startup.WithOptions(o =>
            {
                o.Cors.EnableCors = true;
                o.Cors.AllowedOrigins = ["https://example.com"];
                o.Cors.EnableAllOriginsInDevelopment = true;
            });

            var context = new StartupContext([], new TestWebModule());

            var services = new ServiceCollection();
            startup.ConfigureServicesPublic(context, services);

            using var provider = services.BuildServiceProvider();
            var policyProvider = provider.GetRequiredService<ICorsPolicyProvider>();
            var policy = await policyProvider.GetPolicyAsync(new DefaultHttpContext(), "DefaultCorsPolicy");

            Assert.False(policy!.AllowAnyOrigin);
            Assert.Contains("https://example.com", policy.Origins);
            Assert.False(policy.AllowAnyHeader);
            Assert.False(policy.AllowAnyMethod);
            Assert.Empty(policy.Headers);
            Assert.Empty(policy.Methods);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previous);
        }
    }

    [Fact]
    public async Task AllowedHeadersAndMethods_RestrictCorsPolicy()
    {
        var previous = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", Environments.Production);

            var startup = new TestStartup();
            startup.WithOptions(o =>
            {
                o.Cors.EnableCors = true;
                o.Cors.AllowedOrigins = ["https://example.com"];
                o.Cors.AllowedHeaders = ["Content-Type", "X-Request-Id"];
                o.Cors.AllowedMethods = [HttpMethods.Get, HttpMethods.Post];
            });

            var context = new StartupContext([], new TestWebModule());

            var services = new ServiceCollection();
            startup.ConfigureServicesPublic(context, services);

            using var provider = services.BuildServiceProvider();
            var policyProvider = provider.GetRequiredService<ICorsPolicyProvider>();
            var policy = await policyProvider.GetPolicyAsync(new DefaultHttpContext(), "DefaultCorsPolicy");

            Assert.False(policy!.AllowAnyHeader);
            Assert.False(policy.AllowAnyMethod);
            Assert.Equal(["Content-Type", "X-Request-Id"], policy.Headers);
            Assert.Equal([HttpMethods.Get, HttpMethods.Post], policy.Methods);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previous);
        }
    }

    [Fact]
    public async Task WildcardHeadersAndMethods_AllowAnyPreflightContract()
    {
        var previous = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", Environments.Production);

            var startup = new TestStartup();
            startup.WithOptions(o =>
            {
                o.Cors.EnableCors = true;
                o.Cors.AllowedOrigins = ["https://example.com"];
                o.Cors.AllowedHeaders = ["*"];
                o.Cors.AllowedMethods = ["*"];
            });

            var context = new StartupContext([], new TestWebModule());

            var services = new ServiceCollection();
            startup.ConfigureServicesPublic(context, services);

            using var provider = services.BuildServiceProvider();
            var policyProvider = provider.GetRequiredService<ICorsPolicyProvider>();
            var policy = await policyProvider.GetPolicyAsync(new DefaultHttpContext(), "DefaultCorsPolicy");

            Assert.True(policy!.AllowAnyHeader);
            Assert.True(policy.AllowAnyMethod);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previous);
        }
    }

    [Fact]
    public void LiteralWildcardOrigin_Production_ThrowsActionableException()
    {
        var previous = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", Environments.Production);

            var startup = new TestStartup();
            startup.WithOptions(o =>
            {
                o.Cors.EnableCors = true;
                o.Cors.AllowedOrigins = ["*"];
            });

            var context = new StartupContext([], new TestWebModule());
            var services = new ServiceCollection();

            var exception = Assert.Throws<InvalidOperationException>(
                () => startup.ConfigureServicesPublic(context, services));

            Assert.Contains("CorsOptions.AllowedOrigins", exception.Message, StringComparison.Ordinal);
            Assert.Contains("Cors:AllowedOrigins", exception.Message, StringComparison.Ordinal);
            Assert.Contains("https://app.example.com", exception.Message, StringComparison.Ordinal);
            Assert.Contains("EnableAllOriginsInDevelopment", exception.Message, StringComparison.Ordinal);
            Assert.Contains("ASP.NET Core CORS", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previous);
        }
    }

    [Fact]
    public void LiteralWildcardOrigin_MixedProductionOrigins_Throws()
    {
        var previous = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", Environments.Production);

            var startup = new TestStartup();
            startup.WithOptions(o =>
            {
                o.Cors.EnableCors = true;
                o.Cors.AllowedOrigins = ["https://app.example.com", "*"];
            });

            var context = new StartupContext([], new TestWebModule());
            var services = new ServiceCollection();

            Assert.Throws<InvalidOperationException>(() => startup.ConfigureServicesPublic(context, services));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previous);
        }
    }

    [Fact]
    public async Task WildcardSubdomainOrigin_Production_UsesConfiguredOrigin()
    {
        var previous = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", Environments.Production);

            var startup = new TestStartup();
            startup.WithOptions(o =>
            {
                o.Cors.EnableCors = true;
                o.Cors.AllowedOrigins = ["https://*.example.com"];
            });

            var context = new StartupContext([], new TestWebModule());

            var services = new ServiceCollection();
            startup.ConfigureServicesPublic(context, services);

            using var provider = services.BuildServiceProvider();
            var policyProvider = provider.GetRequiredService<ICorsPolicyProvider>();
            var policy = await policyProvider.GetPolicyAsync(new DefaultHttpContext(), "DefaultCorsPolicy");

            Assert.NotNull(policy);
            Assert.False(policy.AllowAnyOrigin);
            Assert.Contains("https://*.example.com", policy.Origins);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previous);
        }
    }

    [Fact]
    public async Task LiteralWildcardOrigin_DevelopmentWithoutAllOriginsConvenience_KeepsCompatibility()
    {
        var previous = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", Environments.Development);

            var startup = new TestStartup();
            startup.WithOptions(o =>
            {
                o.Cors.EnableCors = true;
                o.Cors.EnableAllOriginsInDevelopment = false;
                o.Cors.AllowedOrigins = ["*"];
            });

            var context = new StartupContext([], new TestWebModule());

            var services = new ServiceCollection();
            startup.ConfigureServicesPublic(context, services);

            using var provider = services.BuildServiceProvider();
            var policyProvider = provider.GetRequiredService<ICorsPolicyProvider>();
            var policy = await policyProvider.GetPolicyAsync(new DefaultHttpContext(), "DefaultCorsPolicy");

            Assert.NotNull(policy);
            Assert.True(policy.AllowAnyOrigin);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previous);
        }
    }

    [Fact]
    public void EmptyOrigins_WithEnableCors_ThrowsException()
    {
        var previous = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", Environments.Production);

            var startup = new TestStartup();
            startup.WithOptions(o => { o.Cors.EnableCors = true; });

            var context = new StartupContext([], new TestWebModule());
            var services = new ServiceCollection();

            Assert.Throws<InvalidOperationException>(() => startup.ConfigureServicesPublic(context, services));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previous);
        }
    }
}

[CollectionDefinition("Cors environment variable tests", DisableParallelization = true)]
public sealed class CorsEnvironmentVariableTestCollection;
