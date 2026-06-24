using ForgeTrust.AppSurface.Auth;
using ForgeTrust.AppSurface.Docs.Services;
using ForgeTrust.RazorWire;
using ForgeTrust.RazorWire.Streams;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Docs.Tests
{
    public sealed class RazorWireBoolChannelAuthorizerAdapterFactoryTests
    {
        [Fact]
        public async Task AddAppSurfaceDocs_WhenFactoryReturnsBoolAdapter_FiltersAdapterAndUsesLegacyChannelAuthorizer()
        {
            var environment = new TestWebHostEnvironment { EnvironmentName = Environments.Production };
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["AppSurfaceDocs:Harvest:Health:ExposeRoutes"] = "Always"
                        })
                    .Build());
            services.AddSingleton<IWebHostEnvironment>(environment);
            services.AddSingleton<IHostEnvironment>(environment);
            services.AddScoped<IRazorWireChannelAuthorizer, AllowAllChannelAuthorizer>();
            services.AddSingleton<IRazorWireStreamAuthorizer>(
                _ => new RazorWireBoolChannelAuthorizerAdapter());
            services.AddLogging();

            services.AddAppSurfaceDocs();

            await using var serviceProvider = services.BuildServiceProvider(validateScopes: true);
            await using var scope = serviceProvider.CreateAsyncScope();
            var authorizer = scope.ServiceProvider.GetRequiredService<IRazorWireStreamAuthorizer>();
            var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };

            var result = await authorizer.AuthorizeAsync(
                new RazorWireStreamAuthorizationContext(
                    context,
                    AppSurfaceDocsStreamAuthorization.HarvestProgressChannel,
                    RazorWireStreamAuthorizationMode.DenyAll));

            Assert.True(result.IsAllowed);
        }

        private sealed class TestWebHostEnvironment : IWebHostEnvironment
        {
            public string ApplicationName { get; set; } = "AppSurfaceDocsTests";

            public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();

            public string WebRootPath { get; set; } = string.Empty;

            public string EnvironmentName { get; set; } = Environments.Development;

            public string ContentRootPath { get; set; } = string.Empty;

            public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        }

        private sealed class AllowAllChannelAuthorizer : IRazorWireChannelAuthorizer
        {
            public ValueTask<bool> CanSubscribeAsync(HttpContext context, string channel)
            {
                return new ValueTask<bool>(true);
            }
        }
    }
}

namespace ForgeTrust.RazorWire.Streams
{
    internal sealed class RazorWireBoolChannelAuthorizerAdapter : IRazorWireStreamAuthorizer
    {
        public ValueTask<AppSurfaceAuthResult> AuthorizeAsync(RazorWireStreamAuthorizationContext context)
        {
            return new ValueTask<AppSurfaceAuthResult>(AppSurfaceAuthResult.Forbidden());
        }
    }
}
