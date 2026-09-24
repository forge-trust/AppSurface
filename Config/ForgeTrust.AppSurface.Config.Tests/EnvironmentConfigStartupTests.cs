using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config.Tests;

public class EnvironmentConfigStartupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalizedMappingsAndDeclarationsFailValidateOnStartBeforeAnyValueRead(bool mappingFirst)
    {
        var environment = new EnvironmentFixture([]);
        using var host = new HostBuilder().ConfigureServices(services =>
        {
            new AppSurfaceConfigModule().ConfigureServices(new([], new TestHostModule(), EnvironmentProvider: environment), services);
            services.AddSingleton<IEnvironmentProvider>(environment);
            if (mappingFirst) services.Configure<AppSurfaceEnvironmentConfigOptions>(options => options.MapKey("A:B", "C_D"));
            services.AddConfigAuditKey<string>(AppSurfaceConfigKey.Parse("C_D"));
            if (!mappingFirst) services.PostConfigure<AppSurfaceEnvironmentConfigOptions>(options => options.MapKey("A:B", "C_D"));
        }).Build();

        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Equal(0, environment.Captures);
    }

    [Fact]
    public async Task HostStartupFreezesValidMappingsAndPreservesHostIsolation()
    {
        foreach (var suffix in new[] { "First", "Second" })
        {
            var environment = new EnvironmentFixture([($"PRODUCTION__{suffix}", suffix)]);
            using var host = new HostBuilder().ConfigureServices(services =>
            {
                new AppSurfaceConfigModule().ConfigureServices(new([], new TestHostModule(), EnvironmentProvider: environment), services);
                services.AddSingleton<IEnvironmentProvider>(environment);
                services.AddConfigAuditKey<string>(AppSurfaceConfigKey.Parse("A:B"));
                services.Configure<AppSurfaceEnvironmentConfigOptions>(options => options.MapKey("A:B", suffix));
            }).Build();
            await host.StartAsync();
            var manager = host.Services.GetRequiredService<IConfigManager>();
            Assert.Equal(suffix, manager.GetValue<string>("Production", AppSurfaceConfigKey.Parse("A:B")));
            host.Services.GetRequiredService<IOptions<AppSurfaceEnvironmentConfigOptions>>().Value.MapKey("Other", "Later");
            Assert.Equal(suffix, manager.GetValue<string>("Production", AppSurfaceConfigKey.Parse("a:b")));
            await host.StopAsync();
        }
    }
}
