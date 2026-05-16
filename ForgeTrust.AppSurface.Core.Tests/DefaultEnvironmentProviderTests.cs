using ForgeTrust.AppSurface.Core.Defaults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Core.Tests;

public class DefaultEnvironmentProviderTests
{

    [Theory]
    [InlineData("DOTNET_ENVIRONMENT", "Production", false)]
    [InlineData("DOTNET_ENVIRONMENT", "Staging", false)]
    [InlineData("DOTNET_ENVIRONMENT", "Development", true)]
    [InlineData("ASPNETCORE_ENVIRONMENT", "Production", false)]
    [InlineData("ASPNETCORE_ENVIRONMENT", "Staging", false)]
    [InlineData("ASPNETCORE_ENVIRONMENT", "Development", true)]
    public void DefaultEnvironmentProvider_HandlesDevelopmentFlag(string envVariable, string environment, bool isDev)
    {
        // Clear any existing env vars to avoid test pollution
        Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", null);
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", null);

        Environment.SetEnvironmentVariable(envVariable, environment);

        var root = new NoHostModule();
        var startup = new CaptureIsDevStartup();
        var context = new StartupContext([], root);

        using var host = ((IAppSurfaceStartup)startup).CreateHostBuilder(context).Build();

        Assert.Equal(isDev, startup.CapturedIsDevelopment);
        // Ensure the provider resolved from DI matches what the module registered
        var provider = host.Services.GetRequiredService<IEnvironmentProvider>();
        Assert.IsType<DefaultEnvironmentProvider>(provider);
        Assert.Equal(startup.CapturedIsDevelopment, provider.IsDevelopment);
    }

    [Theory]
    [InlineData("foo", true)]
    [InlineData("Development", false)]
    public void ModulesCanOverrideEnvironmentProvider_AndContextIsDevelopmentReflectsOverride(
        string environment,
        bool isDev)
    {
        var startup = new CaptureIsDevStartup();
        var context = new StartupContext(
            [],
            new NoHostModule(),
            EnvironmentProvider: new TestEnvironmentProvider(environment, isDev));

        using var host = ((IAppSurfaceStartup)startup).CreateHostBuilder(context).Build();

        Assert.Equal(isDev, startup.CapturedIsDevelopment);
        // Ensure the provider resolved from DI matches what the module registered
        var provider = host.Services.GetRequiredService<IEnvironmentProvider>();
        Assert.IsType<TestEnvironmentProvider>(provider);
        Assert.Equal(startup.CapturedIsDevelopment, provider.IsDevelopment);
    }

    [Fact]
    public void DefaultEnvironmentProvider_Prefers_ASPNETCORE_ENVIRONMENT_Over_DOTNET_ENVIRONMENT()
    {
        // Clear any existing env vars to avoid test pollution
        Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", null);
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", null);

        Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Development");
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "foo");

        var provider = new DefaultEnvironmentProvider();

        Assert.Equal("foo", provider.Environment);
        Assert.False(provider.IsDevelopment);
    }

    [Theory]
    [InlineData("--environment", "Development", "Development", true)]
    [InlineData("--environment", "Production", "Production", false)]
    [InlineData("--environment=Development", null, "Development", true)]
    [InlineData("--environment=Production", null, "Production", false)]
    public void DefaultEnvironmentProvider_PrefersCommandLineEnvironmentOverEnvironmentVariables(
        string environmentOption,
        string? environmentValue,
        string expectedEnvironment,
        bool expectedIsDevelopment)
    {
        var previousDotnetEnvironment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        var previousAspNetCoreEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

        try
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Development");
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");

            string[] args = environmentValue is null
                ? [environmentOption]
                : [environmentOption, environmentValue];

            var provider = new DefaultEnvironmentProvider(args);

            Assert.Equal(expectedEnvironment, provider.Environment);
            Assert.Equal(expectedIsDevelopment, provider.IsDevelopment);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", previousDotnetEnvironment);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previousAspNetCoreEnvironment);
        }
    }

    [Theory]
    [InlineData("--environment")]
    [InlineData("--environment=")]
    public void DefaultEnvironmentProvider_IgnoresBlankCommandLineEnvironment(string environmentOption)
    {
        var previousDotnetEnvironment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        var previousAspNetCoreEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

        try
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", null);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Staging");

            var provider = new DefaultEnvironmentProvider([environmentOption]);

            Assert.Equal("Staging", provider.Environment);
            Assert.False(provider.IsDevelopment);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", previousDotnetEnvironment);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previousAspNetCoreEnvironment);
        }
    }

    [Fact]
    public void DefaultEnvironmentProvider_IgnoresBlankSplitCommandLineEnvironment()
    {
        var previousDotnetEnvironment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        var previousAspNetCoreEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

        try
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Production");
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");

            var provider = new DefaultEnvironmentProvider(["--environment", " "]);

            Assert.Equal("Development", provider.Environment);
            Assert.True(provider.IsDevelopment);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", previousDotnetEnvironment);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previousAspNetCoreEnvironment);
        }
    }

    [Fact]
    public void DefaultEnvironmentProvider_Throws_WhenArgsIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new DefaultEnvironmentProvider(null!));
    }

    [Fact]
    public void GetEnvironmentVariable_ReturnsEmptyString_WhenVariableIsExplicitlyEmpty()
    {
        var variableName = $"APPSURFACE_TEST_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(variableName, string.Empty);

        try
        {
            var provider = new DefaultEnvironmentProvider();

            var value = provider.GetEnvironmentVariable(variableName, "fallback");

            Assert.Equal(string.Empty, value);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }

    [Fact]
    public void GetEnvironmentVariable_ReturnsDefault_WhenVariableIsUnset()
    {
        var variableName = $"APPSURFACE_TEST_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(variableName, null);

        var provider = new DefaultEnvironmentProvider();

        var value = provider.GetEnvironmentVariable(variableName, "fallback");

        Assert.Equal("fallback", value);
    }

    private class CaptureIsDevStartup : AppSurfaceStartup<NoHostModule>
    {
        public bool CapturedIsDevelopment { get; private set; }

        protected override void ConfigureServicesForAppType(StartupContext context, IServiceCollection services)
        {
            // Capture the IsDevelopment flag as seen by app-type configuration
            CapturedIsDevelopment = context.IsDevelopment;
        }
    }

    private class TestEnvironmentProvider(string environment, bool isDevelopment) : IEnvironmentProvider
    {
        public string Environment => environment;
        public bool IsDevelopment => isDevelopment;
        public string? GetEnvironmentVariable(string name, string? defaultValue = null) => throw new NotImplementedException();
    }
}
