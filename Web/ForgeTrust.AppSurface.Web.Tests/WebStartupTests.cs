using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using ForgeTrust.AppSurface.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Web.Tests;

public class WebStartupTests
{
    [Fact]
    public void WithOptions_SetsConfigurationCallback()
    {
        var root = new TestWebModule();
        var startup = new TestWebStartup(root);
        var called = false;

        startup.WithOptions(_ => called = true);

        var context = new StartupContext([], root);
        var builder = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);

        // Configuration callback is invoked during service registration
        builder.Build();

        Assert.True(called);
    }

    [Fact]
    public async Task RunAsync_UsesOriginalArguments_WhenDevelopmentPortFallbackDoesNotApply()
    {
        var args = new[] { "--urls", "http://127.0.0.1:5005" };
        var startup = new CapturingRunWebStartup(
            new TestWebModule(),
            new(args, null, null));

        await startup.RunAsync(args);

        Assert.Same(args, startup.ResolveInputArgs);
        Assert.Same(args, startup.RunArgs);
    }

    [Fact]
    public async Task RunAsync_UsesResolvedArguments_WhenDevelopmentPortFallbackApplies()
    {
        var originalArgs = Array.Empty<string>();
        var resolvedArgs = new[] { "--urls", "http://localhost:6123" };
        var startup = new CapturingRunWebStartup(
            new TestWebModule(),
            new(resolvedArgs, 6123, "/workspace"));

        await startup.RunAsync(originalArgs);

        Assert.Same(originalArgs, startup.ResolveInputArgs);
        Assert.Same(resolvedArgs, startup.RunArgs);
    }

    [Fact]
    public void ResolveDevelopmentPortDefaults_UsesProcessInputs()
    {
        var args = new[] { "--urls", "http://127.0.0.1:5005" };
        var startup = new TestWebStartup(new TestWebModule());

        var resolution = startup.ResolveDevelopmentPortDefaults(args);

        Assert.Null(resolution.AppliedPort);
        Assert.Same(args, resolution.Args);
    }

    [Fact]
    public async Task RunResolvedAsync_DelegatesToBaseRunPath()
    {
        var startup = new StoppingWebStartup(new StoppingWebModule());

        var exception = await Record.ExceptionAsync(
            () => startup.RunResolvedAsync(["--urls", "http://127.0.0.1:0"]));

        Assert.Null(exception);
    }

    [Fact]
    public async Task RunResolvedAsync_FailsFast_WhenHostStartupDoesNotCompleteBeforeTimeout()
    {
        var startup = new HangingWebStartup(new HangingWebModule());
        startup.WithOptions(options => options.StartupTimeout = TimeSpan.FromMilliseconds(250));
        var originalExitCode = Environment.ExitCode;

        try
        {
            Environment.ExitCode = 0;

            await startup.RunResolvedAsync(["--urls", "http://127.0.0.1:0"]);

            Assert.Equal(-100, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
        }
    }

    [Fact]
    public void StartupTimeoutDiagnostic_Detects_CodexSandboxMarkers()
    {
        var diagnostic = AppSurfaceWebStartupTimeoutDiagnostic.Create(
            TimeSpan.FromSeconds(10),
            "StartHost",
            "/workspace/app",
            "/workspace/app/bin",
            staticWebAssetsEnabled: false,
            ["--urls", "http://127.0.0.1:5000", "--name", "docs preview", "--ConnectionStrings:Default", "Server=secret"],
            key => key switch
            {
                "CODEX_SANDBOX" => "seatbelt",
                "CODEX_SANDBOX_NETWORK_DISABLED" => "1",
                _ => null
            });

        Assert.True(diagnostic.SandboxDetected);
        Assert.Contains("CODEX_SANDBOX=seatbelt", diagnostic.SandboxSummary, StringComparison.Ordinal);
        Assert.Contains("CODEX_SANDBOX_NETWORK_DISABLED=1", diagnostic.SandboxSummary, StringComparison.Ordinal);
        Assert.Contains("Rerun the command outside the sandbox", diagnostic.RecommendedAction, StringComparison.Ordinal);
        Assert.Contains("--urls http://127.0.0.1:5000", diagnostic.StartupArgsSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("docs preview", diagnostic.StartupArgsSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("ConnectionStrings", diagnostic.StartupArgsSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", diagnostic.StartupArgsSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupTimeoutDiagnostic_Renders_Only_EndpointArgumentForms()
    {
        var diagnostic = AppSurfaceWebStartupTimeoutDiagnostic.Create(
            TimeSpan.FromSeconds(10),
            "StartHost",
            "/workspace/app",
            "/workspace/app/bin",
            staticWebAssetsEnabled: true,
            [
                "--urls=http://127.0.0.1:5000",
                "--password=secret",
                "--port",
                "5189",
                "--port",
                "--ApiSecret=secret",
                "--ApiKey",
                "secret",
                "--Kestrel:Endpoints:Http:Url",
                "http://127.0.0.1:5190",
                "--Kestrel:Endpoints:Admin:Url",
                "--Password=secret",
                "--Kestrel__Endpoints__Https__Url=https://127.0.0.1:5191",
                "--Kestrel:Endpoints:Https:Certificate:Password",
                "cert-secret",
                "--Kestrel__Endpoints__Https__Certificate__Password=cert-secret"
            ],
            _ => null);

        Assert.Contains("--urls=http://127.0.0.1:5000", diagnostic.StartupArgsSummary, StringComparison.Ordinal);
        Assert.Contains("--port 5189", diagnostic.StartupArgsSummary, StringComparison.Ordinal);
        Assert.Contains("--Kestrel:Endpoints:Http:Url http://127.0.0.1:5190", diagnostic.StartupArgsSummary, StringComparison.Ordinal);
        Assert.Contains("--Kestrel__Endpoints__Https__Url=https://127.0.0.1:5191", diagnostic.StartupArgsSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("password", diagnostic.StartupArgsSummary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ApiKey", diagnostic.StartupArgsSummary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ApiSecret", diagnostic.StartupArgsSummary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", diagnostic.StartupArgsSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("Certificate", diagnostic.StartupArgsSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("cert-secret", diagnostic.StartupArgsSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupTimeoutDiagnostic_UsesNone_WhenNoEndpointArgumentsSurviveFiltering()
    {
        var diagnostic = AppSurfaceWebStartupTimeoutDiagnostic.Create(
            TimeSpan.FromSeconds(10),
            "StartHost",
            "/workspace/app",
            "/workspace/app/bin",
            staticWebAssetsEnabled: true,
            ["--name", "docs preview", "--ConnectionStrings:Default", "Server=secret"],
            _ => null);

        Assert.Equal("<none>", diagnostic.StartupArgsSummary);
    }

    [Fact]
    public void StartupTimeoutDiagnostic_Uses_GenericRecommendation_WhenSandboxMarkersAreAbsent()
    {
        var diagnostic = AppSurfaceWebStartupTimeoutDiagnostic.Create(
            TimeSpan.FromSeconds(10),
            "",
            "/workspace/app",
            "/workspace/app/bin",
            staticWebAssetsEnabled: true,
            [],
            _ => null);

        Assert.False(diagnostic.SandboxDetected);
        Assert.Equal("none detected", diagnostic.SandboxSummary);
        Assert.Equal("unknown", diagnostic.StartupPhase);
        Assert.Equal("<none>", diagnostic.StartupArgsSummary);
        Assert.Contains("Check static web asset discovery", diagnostic.RecommendedAction, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunResolvedAsync_FailsFast_WhenStartupCancellationCallbackDoesNotComplete()
    {
        var module = new HangingCancellationWebModule();
        var startup = new HangingCancellationWebStartup(module);
        startup.WithOptions(options => options.StartupTimeout = TimeSpan.FromSeconds(1));
        var originalExitCode = Environment.ExitCode;

        try
        {
            Environment.ExitCode = 0;

            var runTask = startup.RunResolvedAsync(["--urls", "http://127.0.0.1:0"]);
            await module.Probe.CancellationRegistered.WaitAsync(TimeSpan.FromSeconds(5));

            var completedTask = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(10)));

            Assert.Same(runTask, completedTask);
            await runTask;
            await module.Probe.CancellationStarted.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(-100, Environment.ExitCode);
        }
        finally
        {
            module.Probe.ReleaseCancellation();
            Environment.ExitCode = originalExitCode;
        }
    }

    [Fact]
    public async Task RunResolvedAsync_Rejects_NonPositive_Configured_StartupTimeout()
    {
        var startup = new StoppingWebStartup(new StoppingWebModule());
        startup.WithOptions(options => options.StartupTimeout = TimeSpan.Zero);
        var originalExitCode = Environment.ExitCode;

        try
        {
            Environment.ExitCode = 0;

            await startup.RunResolvedAsync(["--urls", "http://127.0.0.1:0"]);

            Assert.Equal(-100, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
        }
    }

    [Fact]
    public async Task RunResolvedAsync_Handles_OperationCanceledException_WithoutChangingExitCode()
    {
        var startup = new CancelingWebStartup(new CancelingWebModule());
        var originalExitCode = Environment.ExitCode;

        try
        {
            Environment.ExitCode = 17;

            await startup.RunResolvedAsync(["--urls", "http://127.0.0.1:0"]);

            Assert.Equal(17, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
        }
    }

    [Fact]
    public async Task RunResolvedAsync_Handles_GeneralException_BySettingExitCode()
    {
        var startup = new FaultingWebStartup(new FaultingWebModule());
        var originalExitCode = Environment.ExitCode;

        try
        {
            Environment.ExitCode = 0;

            await startup.RunResolvedAsync(["--urls", "http://127.0.0.1:0"]);

            Assert.Equal(-100, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
        }
    }

    [Fact]
    public async Task RunResolvedAsync_UsesDependencyConfiguredStartupTimeout_BeforeHostBuild()
    {
        var startup = new DependencyTimeoutWebStartup(new DependencyTimeoutRootModule());
        var originalExitCode = Environment.ExitCode;

        try
        {
            Environment.ExitCode = 0;

            await startup.RunResolvedAsync(["--urls", "http://127.0.0.1:0"]);

            Assert.Equal(-100, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
        }
    }


    [Fact]
    public void BuildModules_CorrectlyCollectsWebModules()
    {
        var root = new TestWebModule();
        var context = new StartupContext([], root);
        context.Dependencies.AddModule<TestWebModule>();

        var startup = new TestWebStartup(root);
        var builder = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);

        // Simply building the host ensures BuildModules is initialized
        using var host = builder.Build();

        // Assertions are tricky without public state, but this verifies no exceptions 
        // and hits the dependency iteration logic.
    }

    [Theory]
    [InlineData(MvcSupport.None)]
    [InlineData(MvcSupport.Controllers)]
    [InlineData(MvcSupport.ControllersWithViews)]
    [InlineData(MvcSupport.Full)]
    public void BuildWebOptions_MvcLevel_ExercisesBranches(MvcSupport level)
    {
        var root = new TestWebModule { MvcLevel = level };
        var startup = new TestWebStartup(root);
        var context = new StartupContext([], root);

        var builder = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);
        using var host = builder.Build();

        // Verify MVC services are present if level > None
        if (level > MvcSupport.None)
        {
            Assert.NotNull(
                host.Services
                    .GetService<IActionDescriptorCollectionProvider>());
        }
    }

    [Fact]
    public void ConfigureServices_EnablesCors_WhenConfigured()
    {
        var root = new TestWebModule();
        var startup = new TestWebStartup(root);
        startup.WithOptions(o =>
        {
            o.Cors.EnableCors = true;
            o.Cors.AllowedOrigins = new[] { "https://example.com" };
        });
        var context = new StartupContext([], root);

        var builder = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);
        using var host = builder.Build();

        Assert.NotNull(host.Services.GetService<ICorsService>());
    }

    [Fact]
    public void ConfigureServices_Cors_EnableAllOriginsInDevelopment_Works()
    {
        var previous = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", Environments.Development);

            var root = new TestWebModule();
            var startup = new TestWebStartup(root);
            startup.WithOptions(o => o.Cors.EnableAllOriginsInDevelopment = true);

            var context = new StartupContext([], root);
            var builder = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);
            using var host = builder.Build();

            Assert.NotNull(host.Services.GetService<ICorsService>());
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previous);
        }
    }

    [Fact]
    public void ConfigureServices_Cors_EnableAllOriginsInDevelopment_WorksWithCommandLineEnvironment()
    {
        var previousDotnetEnvironment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        var previousAspNetCoreEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

        try
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", null);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", null);

            var root = new TestWebModule();
            var startup = new TestWebStartup(root);
            startup.WithOptions(o => o.Cors.EnableAllOriginsInDevelopment = true);

            var context = new StartupContext(["--environment", Environments.Development], root);
            var builder = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);
            using var host = builder.Build();

            Assert.True(context.IsDevelopment);
            Assert.NotNull(host.Services.GetService<ICorsService>());
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", previousDotnetEnvironment);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previousAspNetCoreEnvironment);
        }
    }

    [Fact]
    public async Task ConfigureServices_Cors_SpecificOrigins_Works()
    {
        var previous = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", Environments.Production);

            var root = new TestWebModule();
            var startup = new TestWebStartup(root);
            startup.WithOptions(o =>
            {
                o.Cors.EnableCors = true;
                o.Cors.AllowedOrigins = new[] { "https://example.com" };
            });

            var context = new StartupContext([], root);

            var builder = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);
            using var host = builder.Build();

            var corsService = host.Services
                .GetRequiredService<ICorsPolicyProvider>();
            var policy = await corsService.GetPolicyAsync(
                new DefaultHttpContext(),
                "DefaultCorsPolicy");

            Assert.NotNull(policy);
            Assert.Contains("https://example.com", policy.Origins);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previous);
        }
    }

    [Fact]
    public void ConfigureBuilderForAppType_ExercisesWebHostConfiguration()
    {
        var root = new TestWebModule();
        var startup = new TestWebStartup(root);
        var context = new StartupContext([], root);

        var builder = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);
        using var host = builder.Build();

        // Simply building the host confirms web host defaults were configured
        Assert.NotNull(host.Services.GetService<IWebHostEnvironment>());
    }

    [Fact]
    public void CreateHostBuilder_UsesArgsForUrlsOverride()
    {
        var root = new TestWebModule();
        var startup = new TestWebStartup(root);
        // We pass --urls to override the default
        var context = new StartupContext(["--urls", "http://127.0.0.1:5005"], root);

        var builder = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);
        using var host = builder.Build();

        var config = host.Services.GetRequiredService<IConfiguration>();
        Assert.Equal("http://127.0.0.1:5005", config["urls"]);
    }

    [Fact]
    public void CreateHostBuilder_UsesPortArgOverride()
    {
        var root = new TestWebModule();
        var startup = new TestWebStartup(root);
        // We pass --port to override with the shortcut
        var context = new StartupContext(["--port", "5005"], root);

        var builder = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);
        using var host = builder.Build();

        var config = host.Services.GetRequiredService<IConfiguration>();
        Assert.Equal("http://localhost:5005", config["urls"]);
    }

    [Fact]
    public void CreateHostBuilder_UsesAllHostsPortArgOverride()
    {
        var root = new TestWebModule();
        var startup = new TestWebStartup(root);
        var context = new StartupContext(["--port", "5005", "--all-hosts"], root);

        var builder = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);
        using var host = builder.Build();

        var config = host.Services.GetRequiredService<IConfiguration>();
        Assert.Equal("http://localhost:5005;http://*:5005", config["urls"]);
    }

    [Fact]
    public void BuildWebOptions_UsesCachedOptions()
    {
        var root = new TestWebModule();
        var startup = new TestWebStartup(root);
        var context = new StartupContext([], root);

        // First call populates _options
        var builder1 = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);
        using var host1 = builder1.Build();

        // Second call on SAME startup instance should hit the cached options branch
        var builder2 = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);
        using var host2 = builder2.Build();
    }

    [Fact]
    public void ConfigureServices_AddsMultipleApplicationParts()
    {
        var root = new TestWebModule();
        var context = new StartupContext([], root);

        // Override entry point to something else so root module assembly is different from entry point
        context.OverrideEntryPointAssembly = typeof(WebApplication).Assembly;

        var startup = new TestWebStartup(root);
        var builder = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);

        using var host = builder.Build();
    }

    [Fact]
    public void ConfigureBuilder_RespectsEnableStaticWebAssets()
    {
        var root = new TestWebModule();
        var startup = new TestWebStartup(root);
        startup.WithOptions(o => o.StaticFiles.EnableStaticWebAssets = true);
        var context = new StartupContext([], root);

        var builder = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);
        using var host = builder.Build();

        // This exercises the EnableStaticWebAssets branch in ConfigureBuilderForAppType
    }

    [Fact]
    public async Task InitializeWebApplication_ExercisesAllMiddleware()
    {
        var root = new TestWebModule();
        var startup = new TestWebStartup(root);
        startup.WithOptions(o =>
        {
            o.Cors.EnableCors = true;
            o.Cors.AllowedOrigins = ["https://example.com"];
            o.StaticFiles.EnableStaticFiles = true;
            o.Mvc = o.Mvc with { MvcSupportLevel = MvcSupport.Controllers };
            o.MapEndpoints = endpoints => { endpoints.MapGet("/test-direct", () => "Direct"); };
        });

        var context = new StartupContext([], root);
        var builder = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);

        // Configure a dynamic port to avoid "address already in use" conflicts
        builder.ConfigureWebHost(webHost => webHost.UseUrls("http://127.0.0.1:0"));

        // Using StartAsync triggers the actual WebHost initialization which calls InitializeWebApplication
        using var host = builder.Build();
        await host.StartAsync();

        // Verify we can access the host effectively
        Assert.NotNull(host.Services.GetService<IWebHostEnvironment>());

        await host.StopAsync();
    }

    [Fact]
    public async Task ConfigureEndpointAwareMiddleware_SeesSelectedEndpointsAndToleratesNoMatch()
    {
        EndpointAwareLifecycleModule.Reset();
        var root = new EndpointAwareLifecycleModule();
        var startup = new ModuleWebStartup<EndpointAwareLifecycleModule>(root);
        startup.WithOptions(o =>
        {
            o.MapEndpoints = endpoints =>
            {
                endpoints.MapGet(
                        "/direct-aware",
                        static async context =>
                        {
                            await context.Response.WriteAsync(
                                (string?)context.Items[EndpointAwareLifecycleModule.EndpointAwareNameItemKey]
                                ?? "<missing>");
                        })
                    .WithDisplayName("direct-aware endpoint")
                    .WithMetadata(new EndpointAwareTestMetadata("direct-aware"));
            };
        });

        await using var host = await StartedWebHost.StartAsync(startup, root);

        Assert.Equal("module-aware", await host.Client.GetStringAsync("/module-aware"));
        Assert.Equal("direct-aware", await host.Client.GetStringAsync("/direct-aware"));

        using var missing = await host.Client.GetAsync("/not-mapped");

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.All(EndpointAwareLifecycleModule.PreRoutingEndpointNames, name => Assert.Equal("<null>", name));
        Assert.Contains("module-aware", EndpointAwareLifecycleModule.EndpointAwareNames);
        Assert.Contains("direct-aware", EndpointAwareLifecycleModule.EndpointAwareNames);
        Assert.Contains("<null>", EndpointAwareLifecycleModule.EndpointAwareNames);
    }

    [Fact]
    public async Task ConfigureEndpointAwareMiddleware_RunsRootFirstAndSupportsAuthorization()
    {
        EndpointAwareAuthRootModule.Reset();
        var root = new EndpointAwareAuthRootModule();
        var startup = new ModuleWebStartup<EndpointAwareAuthRootModule>(root);
        startup.WithOptions(o =>
        {
            o.Cors.EnableCors = true;
            o.Cors.AllowedOrigins = ["https://app.example.test"];
        });

        await using var host = await StartedWebHost.StartAsync(startup, root);

        using var authorized = new HttpRequestMessage(HttpMethod.Get, "/ordered-auth");
        authorized.Headers.Add("Origin", "https://app.example.test");
        authorized.Headers.Add(HeaderAuthenticationHandler.UserHeaderName, "alice");
        using var authorizedResponse = await host.Client.SendAsync(authorized);
        var body = await authorizedResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, authorizedResponse.StatusCode);
        Assert.Equal("alice", body);
        Assert.Equal("https://app.example.test", Assert.Single(authorizedResponse.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Collection(
            EndpointAwareAuthRootModule.Events,
            first => Assert.Equal("root", first),
            second => Assert.Equal("feature:True", second));

        EndpointAwareAuthRootModule.ResetEvents();

        using var unauthorized = new HttpRequestMessage(HttpMethod.Get, "/ordered-auth");
        unauthorized.Headers.Add("Origin", "https://app.example.test");
        using var unauthorizedResponse = await host.Client.SendAsync(unauthorized);

        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedResponse.StatusCode);
        Assert.Equal("https://app.example.test", Assert.Single(unauthorizedResponse.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task ConfigureEndpointAwareMiddleware_SupportsAuthorizedControllers()
    {
        var root = new EndpointAwareMvcAuthRootModule();
        var startup = new ModuleWebStartup<EndpointAwareMvcAuthRootModule>(root);

        await using var host = await StartedWebHost.StartAsync(startup, root);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/endpoint-aware-auth-controller");
        request.Headers.Add(HeaderAuthenticationHandler.UserHeaderName, "controller-user");
        using var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("controller-user", body);
    }

    [Fact]
    public void ConfigureServices_Cors_WildcardInProduction_Throws()
    {
        var previous = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", Environments.Production);

            var root = new TestWebModule();
            var startup = new TestWebStartup(root);
            startup.WithOptions(o =>
            {
                o.Cors.EnableCors = true;
                o.Cors.AllowedOrigins = ["*"];
            });

            var context = new StartupContext([], root);
            var builder = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);

            var exception = Assert.Throws<InvalidOperationException>(() => builder.Build());

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
    public void ConfigureServices_Cors_WildcardInCommandLineProduction_Throws()
    {
        var previous = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", Environments.Development);

            var root = new TestWebModule();
            var startup = new TestWebStartup(root);
            startup.WithOptions(o =>
            {
                o.Cors.EnableCors = true;
                o.Cors.AllowedOrigins = ["*"];
            });

            var context = new StartupContext(["--environment", Environments.Production], root);
            var builder = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);

            Assert.Throws<InvalidOperationException>(() => builder.Build());
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previous);
        }
    }

    [Fact]
    public void ConfigureServices_Cors_WildcardInDevelopment_DoesNotThrow()
    {
        var previous = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", Environments.Development);

            var root = new TestWebModule();
            var startup = new TestWebStartup(root);
            startup.WithOptions(o =>
            {
                o.Cors.EnableCors = true;
                o.Cors.AllowedOrigins = ["*"];
            });

            var context = new StartupContext([], root);
            var builder = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);
            using var host = builder.Build();

            // Verify CORS service is registered
            Assert.NotNull(host.Services.GetService<ICorsService>());
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previous);
        }
    }

    [Fact]
    public void ConfigureServices_ConfigureMvcCallback_IsInvoked()
    {
        var root = new TestWebModule { MvcLevel = MvcSupport.Controllers };
        var startup = new TestWebStartup(root);
        var mvcConfigured = false;

        startup.WithOptions(o =>
        {
            o.Mvc = o.Mvc with
            {
                MvcSupportLevel = MvcSupport.Controllers,
                ConfigureMvc = _ => { mvcConfigured = true; }
            };
        });

        var context = new StartupContext([], root);
        var builder = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);
        using var host = builder.Build();

        Assert.True(mvcConfigured, "ConfigureMvc callback should have been invoked");
    }

    [Fact]
    public void ConfigureServices_ModuleWithIncludeAsApplicationPartFalse_NotAdded()
    {
        var root = new TestWebModuleNoApplicationPart();
        var startup = new TestWebStartupNoAppPart(root);
        startup.WithOptions(o => o.Mvc = o.Mvc with { MvcSupportLevel = MvcSupport.Controllers });

        var context = new StartupContext([], root);
        var builder = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);
        using var host = builder.Build();

        // Verify MVC services are present
        Assert.NotNull(host.Services.GetService<IActionDescriptorCollectionProvider>());
    }

    [Fact]
    public void BuildModules_NonWebModule_NotIncluded()
    {
        var root = new TestWebModule();
        var context = new StartupContext([], root);

        // Add a non-web module
        context.Dependencies.AddModule<NonWebModule>();

        var startup = new TestWebStartup(root);
        var builder = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);
        using var host = builder.Build();

        // Should build successfully without including the non-web module
        Assert.NotNull(host);
    }

    private class TestWebStartup : WebStartup<TestWebModule>
    {
        private readonly TestWebModule _module;

        public TestWebStartup(TestWebModule module)
        {
            _module = module;
        }

        protected override TestWebModule CreateRootModule() => _module;
    }

    private class TestWebStartupNoAppPart : WebStartup<TestWebModuleNoApplicationPart>
    {
        private readonly TestWebModuleNoApplicationPart _module;

        public TestWebStartupNoAppPart(TestWebModuleNoApplicationPart module)
        {
            _module = module;
        }

        protected override TestWebModuleNoApplicationPart CreateRootModule() => _module;
    }

    private sealed class ModuleWebStartup<TModule> : WebStartup<TModule>
        where TModule : IAppSurfaceWebModule, new()
    {
        private readonly TModule _module;

        public ModuleWebStartup(TModule module)
        {
            _module = module;
        }

        protected override TModule CreateRootModule() => _module;
    }

    private sealed class StartedWebHost : IAsyncDisposable
    {
        private readonly IHost _host;

        private StartedWebHost(IHost host, HttpClient client)
        {
            _host = host;
            Client = client;
        }

        public HttpClient Client { get; }

        public static async Task<StartedWebHost> StartAsync<TModule>(
            WebStartup<TModule> startup,
            TModule root)
            where TModule : IAppSurfaceWebModule, new()
        {
            var context = new StartupContext([], root);
            var builder = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);
            builder.ConfigureWebHost(webHost => webHost.UseUrls("http://127.0.0.1:0"));

            var host = builder.Build();
            await host.StartAsync();

            var server = host.Services.GetRequiredService<IServer>();
            var addresses = server.Features.Get<IServerAddressesFeature>();
            var baseAddress = Assert.Single(addresses!.Addresses);
            var client = new HttpClient
            {
                BaseAddress = new Uri(baseAddress)
            };

            return new StartedWebHost(host, client);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    private class TestWebModule : IAppSurfaceWebModule
    {
        public MvcSupport MvcLevel { get; init; } = MvcSupport.None;
        public bool IncludeAsApplicationPart => true;

        public void ConfigureWebOptions(StartupContext context, WebOptions options)
        {
            options.Mvc = options.Mvc with { MvcSupportLevel = MvcLevel };
        }

        public void ConfigureServices(StartupContext context, IServiceCollection services)
        {
        }

        public void ConfigureWebApplication(StartupContext context, IApplicationBuilder app)
        {
        }

        public void ConfigureEndpoints(StartupContext context, IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/test-module", () => "Module");
        }

        public void RegisterDependentModules(ModuleDependencyBuilder builder)
        {
        }

        public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
        {
        }
    }

    private sealed class EndpointAwareLifecycleModule : IAppSurfaceWebModule
    {
        public const string EndpointAwareNameItemKey = "endpoint-aware-name";
        private static readonly List<string> PreRoutingEndpoints = new();
        private static readonly List<string> EndpointAwareEndpointNames = new();

        public static IReadOnlyList<string> PreRoutingEndpointNames => PreRoutingEndpoints;

        public static IReadOnlyList<string> EndpointAwareNames => EndpointAwareEndpointNames;

        public static void Reset()
        {
            PreRoutingEndpoints.Clear();
            EndpointAwareEndpointNames.Clear();
        }

        public void ConfigureServices(StartupContext context, IServiceCollection services)
        {
        }

        public void ConfigureWebApplication(StartupContext context, IApplicationBuilder app)
        {
            app.Use(
                async (httpContext, next) =>
                {
                    PreRoutingEndpoints.Add(httpContext.GetEndpoint()?.DisplayName ?? "<null>");
                    await next();
                });
        }

        public void ConfigureEndpointAwareMiddleware(StartupContext context, IApplicationBuilder app)
        {
            app.Use(
                async (httpContext, next) =>
                {
                    var endpointName = httpContext
                        .GetEndpoint()
                        ?.Metadata
                        .GetMetadata<EndpointAwareTestMetadata>()
                        ?.Name
                        ?? "<null>";
                    EndpointAwareEndpointNames.Add(endpointName);
                    httpContext.Items[EndpointAwareNameItemKey] = endpointName;

                    await next();
                });
        }

        public void ConfigureEndpoints(StartupContext context, IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet(
                    "/module-aware",
                    static async context =>
                    {
                        await context.Response.WriteAsync(
                            (string?)context.Items[EndpointAwareNameItemKey] ?? "<missing>");
                    })
                .WithDisplayName("module-aware endpoint")
                .WithMetadata(new EndpointAwareTestMetadata("module-aware"));
        }

        public void RegisterDependentModules(ModuleDependencyBuilder builder)
        {
        }

        public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
        {
        }
    }

    private sealed record EndpointAwareTestMetadata(string Name);

    private sealed class EndpointAwareAuthRootModule : IAppSurfaceWebModule
    {
        private static readonly List<string> RecordedEvents = new();

        public static IReadOnlyList<string> Events => RecordedEvents;

        public static void Reset()
        {
            ResetEvents();
        }

        public static void ResetEvents()
        {
            RecordedEvents.Clear();
        }

        public static void RecordEvent(string name)
        {
            RecordedEvents.Add(name);
        }

        public void ConfigureServices(StartupContext context, IServiceCollection services)
        {
            services
                .AddAuthentication(HeaderAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(
                    HeaderAuthenticationHandler.SchemeName,
                    _ => { });
            services.AddAuthorization();
        }

        public void ConfigureEndpointAwareMiddleware(StartupContext context, IApplicationBuilder app)
        {
            app.Use(
                async (_, next) =>
                {
                    RecordEvent("root");
                    await next();
                });
            app.UseAuthentication();
            app.UseAuthorization();
        }

        public void RegisterDependentModules(ModuleDependencyBuilder builder)
        {
            builder.AddModule<EndpointAwareAuthFeatureModule>();
        }

        public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
        {
        }
    }

    private sealed class EndpointAwareAuthFeatureModule : IAppSurfaceWebModule
    {
        public void ConfigureServices(StartupContext context, IServiceCollection services)
        {
        }

        public void ConfigureEndpointAwareMiddleware(StartupContext context, IApplicationBuilder app)
        {
            app.Use(
                async (httpContext, next) =>
                {
                    EndpointAwareAuthRootModule.RecordEvent(
                        $"feature:{httpContext.User.Identity?.IsAuthenticated == true}");
                    await next();
                });
        }

        public void ConfigureEndpoints(StartupContext context, IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet(
                    "/ordered-auth",
                    static async context =>
                    {
                        await context.Response.WriteAsync(context.User.Identity?.Name ?? "<anonymous>");
                    })
                .RequireAuthorization();
        }

        public void RegisterDependentModules(ModuleDependencyBuilder builder)
        {
        }

        public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
        {
        }
    }

    private sealed class EndpointAwareMvcAuthRootModule : IAppSurfaceWebModule
    {
        public bool IncludeAsApplicationPart => true;

        public void ConfigureWebOptions(StartupContext context, WebOptions options)
        {
            options.Mvc = options.Mvc with { MvcSupportLevel = MvcSupport.Controllers };
        }

        public void ConfigureServices(StartupContext context, IServiceCollection services)
        {
            services
                .AddAuthentication(HeaderAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(
                    HeaderAuthenticationHandler.SchemeName,
                    _ => { });
            services.AddAuthorization();
        }

        public void ConfigureEndpointAwareMiddleware(StartupContext context, IApplicationBuilder app)
        {
            app.UseAuthentication();
            app.UseAuthorization();
        }

        public void RegisterDependentModules(ModuleDependencyBuilder builder)
        {
        }

        public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
        {
        }
    }

    private sealed class HeaderAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "HeaderTest";
        public const string UserHeaderName = "X-Test-User";

        public HeaderAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(UserHeaderName, out var userValues)
                || string.IsNullOrWhiteSpace(userValues[0]))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var userName = userValues[0]!;
            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.Name, userName),
                    new Claim(ClaimTypes.NameIdentifier, userName)
                ],
                SchemeName);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, SchemeName);

            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    private class TestWebModuleNoApplicationPart : IAppSurfaceWebModule
    {
        public bool IncludeAsApplicationPart => false;

        public void ConfigureWebOptions(StartupContext context, WebOptions options)
        {
        }

        public void ConfigureServices(StartupContext context, IServiceCollection services)
        {
        }

        public void ConfigureWebApplication(StartupContext context, IApplicationBuilder app)
        {
        }

        public void ConfigureEndpoints(StartupContext context, IEndpointRouteBuilder endpoints)
        {
        }

        public void RegisterDependentModules(ModuleDependencyBuilder builder)
        {
        }

        public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
        {
        }
    }

    [Fact]
    public void ConfigureServices_Cors_EmptyOrigins_Throws()
    {
        var root = new TestWebModule();
        var startup = new TestWebStartup(root);
        startup.WithOptions(o =>
        {
            o.Cors.EnableCors = true;
            o.Cors.EnableAllOriginsInDevelopment = false;
            o.Cors.AllowedOrigins = []; // Empty
        });

        var context = new StartupContext([], root);
        var builder = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);

        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public async Task InitializeWebApplication_CustomEndpoints_Invoked()
    {
        var root = new TestWebModule();
        var startup = new TestWebStartup(root);
        var directMappingInvoked = false;

        startup.WithOptions(o =>
        {
            o.MapEndpoints = endpoints =>
            {
                directMappingInvoked = true;
                endpoints.MapGet("/custom", () => "Custom");
            };
        });

        var context = new StartupContext([], root);
        var builder = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);

        // Configure a dynamic port to avoid "address already in use" conflicts
        builder.ConfigureWebHost(webHost => webHost.UseUrls("http://127.0.0.1:0"));

        using var host = builder.Build();
        await host.StartAsync();

        Assert.True(directMappingInvoked);
        await host.StopAsync();
    }

    [Fact]
    public void ConfigureServices_MvcSupportNone_NoMvcServices()
    {
        var root = new TestWebModule { MvcLevel = MvcSupport.None };
        var startup = new TestWebStartup(root);
        var context = new StartupContext([], root);

        var builder = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);
        using var host = builder.Build();

        Assert.Null(
            host.Services.GetService<IActionDescriptorCollectionProvider>());
    }

    [Fact]
    public void ConfigureServices_MultipleModules_DistinctAssemblies()
    {
        var root = new TestWebModule();
        // Set a different entry point assembly to hit the assembly filtering branch
        var entryAssembly = typeof(WebStartup<>).Assembly;
        var context = new StartupContext([], root) { OverrideEntryPointAssembly = entryAssembly };

        // Add multiple modules from the same assembly (which is different from entryAssembly)
        context.Dependencies.AddModule<TestWebModule>();
        context.Dependencies.AddModule<AnotherTestWebModuleInSameAssembly>();

        var startup = new TestWebStartup(root);
        var builder = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);

        using var host = builder.Build();
        // Simply verifying no collision or redundancy issues during distinct assembly iteration
        // and that line 127 in WebStartup.cs is hit (AddApplicationPart for non-entry assembly)
        Assert.NotNull(host);
    }

    [Fact]
    public void BuildWebOptions_EnablesStaticFiles_ForControllersWithViews()
    {
        var root = new TestWebModule { MvcLevel = MvcSupport.ControllersWithViews };
        var startup = new TestWebStartup(root);
        var context = new StartupContext([], root);

        var builder = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);
        using var host = builder.Build();

        // Internally _options.StaticFiles.EnableStaticFiles should be true
        // We can verify this via middleware behavior or reflection if needed, 
        // but here we just ensure the branch is hit during build.
    }

    [Fact]
    public async Task ConfigureServices_Cors_Wildcard_Development_AllowsAnyOrigin()
    {
        var previous = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", Environments.Development);

            var root = new TestWebModule();
            var startup = new TestWebStartup(root);
            startup.WithOptions(o =>
            {
                o.Cors.EnableCors = true;
                o.Cors.AllowedOrigins = ["*"];
            });

            var context = new StartupContext([], root);
            var builder = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);
            using var host = builder.Build();

            var corsService = host.Services
                .GetRequiredService<ICorsPolicyProvider>();
            var policy = await corsService.GetPolicyAsync(
                new DefaultHttpContext(),
                "DefaultCorsPolicy");

            Assert.NotNull(policy);
            Assert.True(policy.AllowAnyOrigin);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previous);
        }
    }

    private class AnotherTestWebModuleInSameAssembly : TestWebModule;

    private sealed class CapturingRunWebStartup : WebStartup<TestWebModule>
    {
        private readonly TestWebModule _module;
        private readonly AppSurfaceWebDevelopmentPortResolution _resolution;

        public CapturingRunWebStartup(
            TestWebModule module,
            AppSurfaceWebDevelopmentPortResolution resolution)
        {
            _module = module;
            _resolution = resolution;
        }

        public string[]? ResolveInputArgs { get; private set; }

        public string[]? RunArgs { get; private set; }

        protected override TestWebModule CreateRootModule() => _module;

        internal override AppSurfaceWebDevelopmentPortResolution ResolveDevelopmentPortDefaults(string[] args)
        {
            ResolveInputArgs = args;
            return _resolution;
        }

        internal override Task RunResolvedAsync(string[] args)
        {
            RunArgs = args;
            return Task.CompletedTask;
        }
    }

    private sealed class StoppingWebStartup : WebStartup<StoppingWebModule>
    {
        private readonly StoppingWebModule _module;

        public StoppingWebStartup(StoppingWebModule module)
        {
            _module = module;
        }

        protected override StoppingWebModule CreateRootModule() => _module;
    }

    private sealed class HangingWebStartup : WebStartup<HangingWebModule>
    {
        private readonly HangingWebModule _module;

        public HangingWebStartup(HangingWebModule module)
        {
            _module = module;
        }

        protected override HangingWebModule CreateRootModule() => _module;
    }

    private sealed class HangingCancellationWebStartup : WebStartup<HangingCancellationWebModule>
    {
        private readonly HangingCancellationWebModule _module;

        public HangingCancellationWebStartup(HangingCancellationWebModule module)
        {
            _module = module;
        }

        protected override HangingCancellationWebModule CreateRootModule() => _module;
    }

    private sealed class DependencyTimeoutWebStartup : WebStartup<DependencyTimeoutRootModule>
    {
        private readonly DependencyTimeoutRootModule _module;

        public DependencyTimeoutWebStartup(DependencyTimeoutRootModule module)
        {
            _module = module;
        }

        protected override DependencyTimeoutRootModule CreateRootModule() => _module;
    }

    private sealed class CancelingWebStartup : WebStartup<CancelingWebModule>
    {
        private readonly CancelingWebModule _module;

        public CancelingWebStartup(CancelingWebModule module)
        {
            _module = module;
        }

        protected override CancelingWebModule CreateRootModule() => _module;
    }

    private sealed class FaultingWebStartup : WebStartup<FaultingWebModule>
    {
        private readonly FaultingWebModule _module;

        public FaultingWebStartup(FaultingWebModule module)
        {
            _module = module;
        }

        protected override FaultingWebModule CreateRootModule() => _module;
    }

    private sealed class CancelingWebModule : IAppSurfaceWebModule
    {
        public bool IncludeAsApplicationPart => false;

        public void ConfigureWebOptions(StartupContext context, WebOptions options)
        {
        }

        public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
        {
            throw new OperationCanceledException();
        }

        public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureServices(StartupContext context, IServiceCollection services)
        {
        }

        public void ConfigureWebApplication(StartupContext context, IApplicationBuilder app)
        {
        }

        public void ConfigureEndpoints(StartupContext context, IEndpointRouteBuilder endpoints)
        {
        }

        public void RegisterDependentModules(ModuleDependencyBuilder builder)
        {
        }
    }

    private sealed class FaultingWebModule : IAppSurfaceWebModule
    {
        public bool IncludeAsApplicationPart => false;

        public void ConfigureWebOptions(StartupContext context, WebOptions options)
        {
        }

        public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
        {
            throw new InvalidOperationException("startup failed");
        }

        public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureServices(StartupContext context, IServiceCollection services)
        {
        }

        public void ConfigureWebApplication(StartupContext context, IApplicationBuilder app)
        {
        }

        public void ConfigureEndpoints(StartupContext context, IEndpointRouteBuilder endpoints)
        {
        }

        public void RegisterDependentModules(ModuleDependencyBuilder builder)
        {
        }
    }

    private sealed class DependencyTimeoutRootModule : IAppSurfaceWebModule
    {
        public bool IncludeAsApplicationPart => false;

        public void ConfigureWebOptions(StartupContext context, WebOptions options)
        {
        }

        public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureServices(StartupContext context, IServiceCollection services)
        {
        }

        public void ConfigureWebApplication(StartupContext context, IApplicationBuilder app)
        {
        }

        public void ConfigureEndpoints(StartupContext context, IEndpointRouteBuilder endpoints)
        {
        }

        public void RegisterDependentModules(ModuleDependencyBuilder builder)
        {
            builder.AddModule<DependencyTimeoutHangingModule>();
        }
    }

    private sealed class DependencyTimeoutHangingModule : IAppSurfaceWebModule
    {
        public bool IncludeAsApplicationPart => false;

        public void ConfigureWebOptions(StartupContext context, WebOptions options)
        {
            options.StartupTimeout = TimeSpan.FromMilliseconds(250);
        }

        public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureServices(StartupContext context, IServiceCollection services)
        {
            services.AddHostedService<NeverStartingHostedService>();
        }

        public void ConfigureWebApplication(StartupContext context, IApplicationBuilder app)
        {
        }

        public void ConfigureEndpoints(StartupContext context, IEndpointRouteBuilder endpoints)
        {
        }

        public void RegisterDependentModules(ModuleDependencyBuilder builder)
        {
        }
    }

    private sealed class HangingWebModule : IAppSurfaceWebModule
    {
        public bool IncludeAsApplicationPart => false;

        public void ConfigureWebOptions(StartupContext context, WebOptions options)
        {
        }

        public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureServices(StartupContext context, IServiceCollection services)
        {
            services.AddHostedService<NeverStartingHostedService>();
        }

        public void ConfigureWebApplication(StartupContext context, IApplicationBuilder app)
        {
        }

        public void ConfigureEndpoints(StartupContext context, IEndpointRouteBuilder endpoints)
        {
        }

        public void RegisterDependentModules(ModuleDependencyBuilder builder)
        {
        }
    }

    private sealed class HangingCancellationWebModule : IAppSurfaceWebModule
    {
        public HangingCancellationProbe Probe { get; } = new();

        public bool IncludeAsApplicationPart => false;

        public void ConfigureWebOptions(StartupContext context, WebOptions options)
        {
        }

        public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureServices(StartupContext context, IServiceCollection services)
        {
            services.AddSingleton(Probe);
            services.AddHostedService<HangingCancellationHostedService>();
        }

        public void ConfigureWebApplication(StartupContext context, IApplicationBuilder app)
        {
        }

        public void ConfigureEndpoints(StartupContext context, IEndpointRouteBuilder endpoints)
        {
        }

        public void RegisterDependentModules(ModuleDependencyBuilder builder)
        {
        }
    }

    private sealed class NeverStartingHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class HangingCancellationHostedService(HangingCancellationProbe probe) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.Register(probe.BlockCancellationUntilReleased);
            probe.MarkCancellationRegistered();
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class HangingCancellationProbe
    {
        private readonly TaskCompletionSource<object?> _cancellationRegistered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource<object?> _cancellationStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource<object?> _releaseCancellation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task CancellationRegistered => _cancellationRegistered.Task;

        public Task CancellationStarted => _cancellationStarted.Task;

        public void MarkCancellationRegistered()
        {
            _cancellationRegistered.TrySetResult(null);
        }

        public void BlockCancellationUntilReleased()
        {
            _cancellationStarted.TrySetResult(null);
            _releaseCancellation.Task.GetAwaiter().GetResult();
        }

        public void ReleaseCancellation()
        {
            _releaseCancellation.TrySetResult(null);
        }
    }

    private sealed class StoppingWebModule : IAppSurfaceWebModule
    {
        public bool IncludeAsApplicationPart => false;

        public void ConfigureWebOptions(StartupContext context, WebOptions options)
        {
        }

        public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureServices(StartupContext context, IServiceCollection services)
        {
            services.AddHostedService<StopApplicationHostedService>();
        }

        public void ConfigureWebApplication(StartupContext context, IApplicationBuilder app)
        {
        }

        public void ConfigureEndpoints(StartupContext context, IEndpointRouteBuilder endpoints)
        {
        }

        public void RegisterDependentModules(ModuleDependencyBuilder builder)
        {
        }
    }

    private sealed class StopApplicationHostedService : IHostedService
    {
        private readonly IHostApplicationLifetime _applicationLifetime;

        public StopApplicationHostedService(IHostApplicationLifetime applicationLifetime)
        {
            _applicationLifetime = applicationLifetime;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _applicationLifetime.StopApplication();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private class NonWebModule : IAppSurfaceModule
    {
        public void ConfigureServices(StartupContext context, IServiceCollection services)
        {
        }

        public void RegisterDependentModules(ModuleDependencyBuilder builder)
        {
        }
    }
}

[ApiController]
[Authorize]
[Route("endpoint-aware-auth-controller")]
public sealed class EndpointAwareAuthTestController : ControllerBase
{
    [HttpGet]
    public string Get()
    {
        return User.Identity?.Name ?? "<anonymous>";
    }
}

public class WebOptionsTests
{
    [Fact]
    public void WebOptions_DefaultStartupTimeout_IsTenSeconds()
    {
        var opts = new WebOptions();

        Assert.Equal(TimeSpan.FromSeconds(10), opts.StartupTimeout);
    }
}

public class StaticFilesOptionsTests
{
    [Fact]
    public void DefaultOptions_HaveExpectedDefaults()
    {
        var opts = new StaticFilesOptions();
        Assert.False(opts.EnableStaticFiles);
        Assert.False(opts.EnableStaticWebAssets);
    }

    [Fact]
    public void PropertySetters_WorkCorrectly()
    {
        var opts = new StaticFilesOptions
        {
            EnableStaticFiles = true,
            EnableStaticWebAssets = true
        };
        Assert.True(opts.EnableStaticFiles);
        Assert.True(opts.EnableStaticWebAssets);
    }
}
