using ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth.Tests;

public sealed class AppSurfaceDevAuthRegistrationTests
{
    [Fact]
    public void AddAppSurfaceDevAuth_WithNullServices_ThrowsArgumentNullException()
    {
        IServiceCollection services = null!;

        Assert.Throws<ArgumentNullException>(() =>
            services.AddAppSurfaceDevAuth(Development(), AddAdmin));
    }

    [Fact]
    public void AddAppSurfaceDevAuth_WithNullEnvironment_ThrowsArgumentNullException()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(() =>
            services.AddAppSurfaceDevAuth(null!, AddAdmin));
    }

    [Fact]
    public void AddAppSurfaceDevAuth_WithNullConfigure_ThrowsArgumentNullException()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(() =>
            services.AddAppSurfaceDevAuth(Development(), null!));
    }

    [Fact]
    public void AddAppSurfaceDevAuth_OutsideDevelopment_ThrowsSafeDiagnostic()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<AppSurfaceDevAuthException>(() =>
            services.AddAppSurfaceDevAuth(new TestHostEnvironment("Production"), AddAdmin));

        Assert.Equal(AppSurfaceDevAuthDiagnostics.NonDevelopmentEnvironment, ex.DiagnosticCode);
        Assert.Contains("ASDEV001 Problem:", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddAppSurfaceDevAuth_WithoutPersonas_ThrowsSafeDiagnostic()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<AppSurfaceDevAuthException>(() =>
            services.AddAppSurfaceDevAuth(Development(), _ => { }));

        Assert.Equal(AppSurfaceDevAuthDiagnostics.NoPersonas, ex.DiagnosticCode);
        Assert.Contains("ASDEV003 Problem:", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddAppSurfaceDevAuth_WithoutExplicitSubject_ThrowsSafeDiagnostic()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<AppSurfaceDevAuthException>(() =>
            services.AddAppSurfaceDevAuth(Development(), options =>
                options.Users.Add("admin", user => user.DisplayName("Local Admin"))));

        Assert.Equal(AppSurfaceDevAuthDiagnostics.MissingSubjectClaim, ex.DiagnosticCode);
        Assert.Contains("ASDEV004 Problem:", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddAppSurfaceDevAuth_WithDuplicatePersona_ThrowsSafeDiagnostic()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<AppSurfaceDevAuthException>(() =>
            services.AddAppSurfaceDevAuth(Development(), options =>
            {
                options.Users.Add("admin", user => user.Subject("admin-1"));
                options.Users.Add("admin", user => user.Subject("admin-2"));
            }));

        Assert.Equal(AppSurfaceDevAuthDiagnostics.InvalidPersonaId, ex.DiagnosticCode);
        Assert.Contains("ASDEV006 Problem:", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("admin?next=viewer")]
    [InlineData("admin#viewer")]
    [InlineData("admin%2Fviewer")]
    [InlineData("admin+viewer")]
    [InlineData("secret-token")]
    [InlineData("api-key")]
    [InlineData("admin-email")]
    [InlineData("apiKey")]
    [InlineData("passwordHash")]
    [InlineData("accessToken")]
    [InlineData("secretToken")]
    [InlineData("credentialId")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(" ")]
    [InlineData(" admin ")]
    public void AddAppSurfaceDevAuth_WithRouteUnsafePersonaId_ThrowsSafeDiagnostic(string personaId)
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<AppSurfaceDevAuthException>(() =>
            services.AddAppSurfaceDevAuth(Development(), options =>
                options.Users.Add(personaId, user => user.Subject("admin-1"))));

        Assert.Equal(AppSurfaceDevAuthDiagnostics.InvalidPersonaId, ex.DiagnosticCode);
        Assert.Contains("ASDEV006 Problem:", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("monkey")]
    [InlineData("mailbox")]
    [InlineData("turkey")]
    [InlineData("keynote")]
    public void AddAppSurfaceDevAuth_WithInnocentTokenSubstrings_AllowsPersonaId(string personaId)
    {
        var services = new ServiceCollection();

        services.AddAppSurfaceDevAuth(Development(), options =>
            options.Users.Add(personaId, user => user.Subject("subject-1")));
    }

    [Fact]
    public void AddAppSurfaceDevAuth_WithSlugPersonaId_AllowsSafeRouteCharacters()
    {
        var services = new ServiceCollection();

        services.AddAppSurfaceDevAuth(Development(), options =>
            options.Users.Add("admin.local_1-test", user => user.Subject("admin-1")));
    }

    [Fact]
    public async Task AddAppSurfaceDevAuth_MaterializesOptionsOnceForSchemeAndRuntime()
    {
        var services = new ServiceCollection();
        var configureCallCount = 0;

        services.AddAppSurfaceDevAuth(Development(), options =>
        {
            configureCallCount++;
            options.SchemeName = configureCallCount == 1 ? "Preview.DevAuth" : "Runtime.DevAuth";
            AddAdmin(options);
        });

        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AppSurfaceDevAuthOptions>>().Value;
        var schemeProvider = provider.GetRequiredService<IAuthenticationSchemeProvider>();
        var schemes = await schemeProvider.GetAllSchemesAsync();
        var hostedService = provider.GetServices<IHostedService>()
            .OfType<AppSurfaceDevAuthStartupValidator>()
            .Single();

        await hostedService.StartAsync(CancellationToken.None);

        Assert.Equal(1, configureCallCount);
        Assert.Equal("Preview.DevAuth", options.SchemeName);
        Assert.Contains(schemes, scheme => string.Equals(scheme.Name, "Preview.DevAuth", StringComparison.Ordinal));
        Assert.DoesNotContain(schemes, scheme => string.Equals(scheme.Name, "Runtime.DevAuth", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartupValidator_UsesExistingHostEnvironmentRegistration()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment("Production"));
        services.AddAppSurfaceDevAuth(Development(), AddAdmin);

        await using var provider = services.BuildServiceProvider();
        var hostedService = provider.GetServices<IHostedService>()
            .OfType<AppSurfaceDevAuthStartupValidator>()
            .Single();

        var ex = await Assert.ThrowsAsync<AppSurfaceDevAuthException>(() =>
            hostedService.StartAsync(CancellationToken.None));

        Assert.Equal(AppSurfaceDevAuthDiagnostics.NonDevelopmentEnvironment, ex.DiagnosticCode);
        Assert.Contains("ASDEV001 Problem:", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dev-auth")]
    [InlineData("/dev-auth/")]
    public void AddAppSurfaceDevAuth_WithInvalidPathPrefix_ThrowsSafeDiagnostic(string pathPrefix)
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<AppSurfaceDevAuthException>(() =>
            services.AddAppSurfaceDevAuth(Development(), options =>
            {
                options.PathPrefix = pathPrefix;
                AddAdmin(options);
            }));

        Assert.Equal(AppSurfaceDevAuthDiagnostics.ReservedPathConflict, ex.DiagnosticCode);
        Assert.Contains("ASDEV005 Problem:", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartupValidator_WithRealSchemeConflict_ThrowsSafeDiagnostic()
    {
        var services = new ServiceCollection();
        services.AddAuthentication("Real")
            .AddScheme<AuthenticationSchemeOptions, StaticAuthenticationHandler>(
                "Real",
                options => { _ = options; });
        services.AddAppSurfaceDevAuth(Development(), AddAdmin);

        await using var provider = services.BuildServiceProvider();
        var hostedService = provider.GetServices<IHostedService>()
            .OfType<AppSurfaceDevAuthStartupValidator>()
            .Single();

        var ex = await Assert.ThrowsAsync<AppSurfaceDevAuthException>(() =>
            hostedService.StartAsync(CancellationToken.None));

        Assert.Equal(AppSurfaceDevAuthDiagnostics.RealSchemeConflict, ex.DiagnosticCode);
        Assert.Contains("ASDEV002 Problem:", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartupValidator_WithRealSchemeOverride_AllowsConflict()
    {
        var services = new ServiceCollection();
        services.AddAuthentication("Real")
            .AddScheme<AuthenticationSchemeOptions, StaticAuthenticationHandler>(
                "Real",
                options => { _ = options; });
        services.AddAppSurfaceDevAuth(Development(), options =>
        {
            options.AllowDevAuthOverrideForLocalProof = true;
            AddAdmin(options);
        });

        await using var provider = services.BuildServiceProvider();
        var hostedService = provider.GetServices<IHostedService>()
            .OfType<AppSurfaceDevAuthStartupValidator>()
            .Single();

        await hostedService.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartupValidator_OutsideDevelopment_ThrowsSafeDiagnostic()
    {
        var validator = new AppSurfaceDevAuthStartupValidator(
            new AuthenticationSchemeProvider(Options.Create(new AuthenticationOptions())),
            new TestHostEnvironment("Staging"),
            Options.Create(new AuthenticationOptions()),
            Options.Create(CreateOptions(AddAdmin)));

        var ex = await Assert.ThrowsAsync<AppSurfaceDevAuthException>(() =>
            validator.StartAsync(CancellationToken.None));

        Assert.Equal(AppSurfaceDevAuthDiagnostics.NonDevelopmentEnvironment, ex.DiagnosticCode);
        Assert.Contains("ASDEV001 Problem:", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartupValidator_StopAsync_Completes()
    {
        var validator = new AppSurfaceDevAuthStartupValidator(
            new AuthenticationSchemeProvider(Options.Create(new AuthenticationOptions())),
            Development(),
            Options.Create(new AuthenticationOptions()),
            Options.Create(CreateOptions(AddAdmin)));

        await validator.StopAsync(CancellationToken.None);
    }

    private static TestHostEnvironment Development()
    {
        return new TestHostEnvironment(Environments.Development);
    }

    private static AppSurfaceDevAuthOptions CreateOptions(Action<AppSurfaceDevAuthOptions> configure)
    {
        var options = new AppSurfaceDevAuthOptions();
        configure(options);
        return options;
    }

    private static void AddAdmin(AppSurfaceDevAuthOptions options)
    {
        options.Users.Add(
            "admin",
            user => user
                .DisplayName("Local Admin")
                .Subject("admin-1")
                .Claim("role", "operator"));
    }
}
