using AppSurfaceDurableWorker;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using AppSurfaceDurableWorker.Hosting;
using AppSurfaceDurableWorker.Work;
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.PostgreSql;
using ForgeTrust.AppSurface.Durable.Provider;
using ForgeTrust.AppSurface.Workers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using OpenTelemetry.Trace;
using Npgsql;

namespace DurableWorkerTemplate.Tests;

public sealed class HostContractTests
{
    private const string ValidToken = "local-token-for-host-contract-tests";
    private const string SensitiveMarker = "must-never-appear-in-a-host-response";
    private const string ActivationPath = "/private/durable/activate";

    [Fact]
    public void Settings_use_documented_defaults_and_inclusive_limits()
    {
        var defaults = ReadSettings();

        Assert.Equal("dispatcher", new NpgsqlConnectionStringBuilder(defaults.DispatcherConnectionString).Username);
        Assert.Equal("runtime", defaults.RuntimeRole);
        Assert.Equal(Guid.Parse("a8bfbc5e-5660-4bd0-9bd6-83e31d2ec329"), defaults.StoreId);
        Assert.Equal(Guid.Parse("47f80193-0e02-4ea2-9857-4b6a70a62257"), defaults.RuntimeEpoch);
        Assert.Equal(10, defaults.MaximumPoolSize);
        Assert.Equal(5, defaults.ConnectionTimeoutSeconds);
        Assert.Equal(5, defaults.CommandTimeoutSeconds);
        Assert.Equal(32, defaults.Activation.PumpMaximumItems);
        Assert.Equal(TimeSpan.FromSeconds(2), defaults.Activation.PumpDiscoveryBudget);
        Assert.Equal(TimeSpan.FromSeconds(10), defaults.Activation.RequestBudget);
        Assert.Equal(TimeSpan.FromSeconds(5), defaults.Activation.BodyReadBudget);
        Assert.Equal("local-test-token", defaults.DevelopmentToken);
        Assert.True(defaults.IsDevelopment);
        Assert.Null(defaults.OtlpEndpoint);

        var limits = ReadSettings(new Dictionary<string, string?>
        {
            ["Durable:MaximumPoolSize"] = "100",
            ["Durable:ConnectionTimeoutSeconds"] = "300",
            ["Durable:CommandTimeoutSeconds"] = "300",
            ["DurableActivation:PumpMaximumItems"] = "10000",
            ["DurableActivation:PumpDiscoveryBudgetSeconds"] = "300",
            ["DurableActivation:RequestBudgetSeconds"] = "4294967",
            ["DurableActivation:BodyReadBudgetSeconds"] = "300",
            [WorkerApplication.DevelopmentTokenKey] = new string('x', 256),
            ["OpenTelemetry:OtlpEndpoint"] = "https://otel.example.test:4318/v1/traces",
        });

        Assert.Equal(100, limits.MaximumPoolSize);
        Assert.Equal(300, limits.ConnectionTimeoutSeconds);
        Assert.Equal(300, limits.CommandTimeoutSeconds);
        Assert.Equal(10_000, limits.Activation.PumpMaximumItems);
        Assert.Equal(TimeSpan.FromSeconds(300), limits.Activation.PumpDiscoveryBudget);
        Assert.Equal(TimeSpan.FromSeconds(4_294_967), limits.Activation.RequestBudget);
        Assert.Equal(TimeSpan.FromSeconds(300), limits.Activation.BodyReadBudget);
        Assert.Equal(new string('x', 256), limits.DevelopmentToken);
        Assert.Equal(new Uri("https://otel.example.test:4318/v1/traces"), limits.OtlpEndpoint);

        var production = ReadSettings(
            new Dictionary<string, string?> { [WorkerApplication.DevelopmentTokenKey] = null },
            Environments.Production);
        Assert.False(production.IsDevelopment);
        Assert.Null(production.DevelopmentToken);
    }

    [Theory]
    [InlineData("Durable:DispatcherConnectionString", null, "Development")]
    [InlineData("Durable:DispatcherConnectionString", "  ", "Development")]
    [InlineData("Durable:RuntimeConnectionString", null, "Development")]
    [InlineData("Durable:RuntimeConnectionString", "", "Development")]
    [InlineData("Durable:StoreId", "not-a-guid", "Development")]
    [InlineData("Durable:StoreId", "00000000-0000-0000-0000-000000000000", "Development")]
    [InlineData("Durable:RuntimeEpoch", "not-a-guid", "Development")]
    [InlineData("Durable:RuntimeEpoch", "00000000-0000-0000-0000-000000000000", "Development")]
    [InlineData("Durable:MaximumPoolSize", "0", "Development")]
    [InlineData("Durable:MaximumPoolSize", "101", "Development")]
    [InlineData("Durable:MaximumPoolSize", "1.0", "Development")]
    [InlineData("Durable:ConnectionTimeoutSeconds", "0", "Development")]
    [InlineData("Durable:ConnectionTimeoutSeconds", "301", "Development")]
    [InlineData("Durable:CommandTimeoutSeconds", "-1", "Development")]
    [InlineData("Durable:CommandTimeoutSeconds", "301", "Development")]
    [InlineData("DurableActivation:PumpMaximumItems", "0", "Development")]
    [InlineData("DurableActivation:PumpMaximumItems", "10001", "Development")]
    [InlineData("DurableActivation:PumpDiscoveryBudgetSeconds", "0", "Development")]
    [InlineData("DurableActivation:PumpDiscoveryBudgetSeconds", "301", "Development")]
    [InlineData("DurableActivation:RequestBudgetSeconds", "0", "Development")]
    [InlineData("DurableActivation:RequestBudgetSeconds", "4294968", "Development")]
    [InlineData("DurableActivation:BodyReadBudgetSeconds", "0", "Development")]
    [InlineData("DurableActivation:BodyReadBudgetSeconds", "301", "Development")]
    [InlineData("OpenTelemetry:OtlpEndpoint", "relative/path", "Development")]
    [InlineData("OpenTelemetry:OtlpEndpoint", "ftp://collector.example.test", "Development")]
    [InlineData("APPSURFACE_TEMPLATE_ACTIVATION_TOKEN", null, "Development")]
    [InlineData("APPSURFACE_TEMPLATE_ACTIVATION_TOKEN", "   ", "Development")]
    [InlineData("APPSURFACE_TEMPLATE_ACTIVATION_TOKEN", "production-secret", "Production")]
    public void Settings_reject_missing_malformed_or_out_of_range_values(
        string key,
        string? value,
        string environmentName)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => ReadSettings(
            new Dictionary<string, string?> { [key] = value },
            environmentName));

        Assert.DoesNotContain("production-secret", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_measure_development_token_limit_in_utf8_bytes()
    {
        var maximum = ReadSettings(new Dictionary<string, string?>
        {
            [WorkerApplication.DevelopmentTokenKey] = new string('é', 128),
        });
        Assert.Equal(128, maximum.DevelopmentToken!.Length);

        var exception = Assert.Throws<InvalidOperationException>(() => ReadSettings(new Dictionary<string, string?>
        {
            [WorkerApplication.DevelopmentTokenKey] = new string('é', 129),
        }));
        Assert.DoesNotContain(new string('é', 129), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_reject_invalid_or_duplicate_restricted_roles_without_echoing_connections()
    {
        var invalidConnection = $"Host=127.0.0.1;Username={SensitiveMarker};UnknownConnectionOption=true";
        var invalid = Assert.Throws<InvalidOperationException>(() => ReadSettings(new Dictionary<string, string?>
        {
            ["Durable:RuntimeConnectionString"] = invalidConnection,
        }));
        Assert.DoesNotContain(SensitiveMarker, invalid.Message, StringComparison.Ordinal);

        var sameRole = Assert.Throws<InvalidOperationException>(() => ReadSettings(new Dictionary<string, string?>
        {
            ["Durable:DispatcherConnectionString"] = "Host=127.0.0.1;Username=runtime;Password=first",
        }));
        Assert.DoesNotContain("Password", sameRole.Message, StringComparison.OrdinalIgnoreCase);

        var missingRuntimeRole = Assert.Throws<InvalidOperationException>(() => ReadSettings(new Dictionary<string, string?>
        {
            ["Durable:RuntimeConnectionString"] = "Host=127.0.0.1;Database=app",
        }));
        Assert.Equal(
            "Both Durable PostgreSQL connection settings must be valid and identify restricted login roles.",
            missingRuntimeRole.Message);
    }

    [Fact]
    public void Builder_uses_loopback_default_or_explicit_url_and_rejects_null_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => WorkerApplication.CreateBuilder(null!));

        var defaults = WorkerApplication.CreateBuilder([]);
        Assert.Equal("http://127.0.0.1:5080", defaults.WebHost.GetSetting(WebHostDefaults.ServerUrlsKey));

        var configured = WorkerApplication.CreateBuilder(["--urls", "http://127.0.0.1:0"]);
        Assert.Equal("http://127.0.0.1:0", configured.WebHost.GetSetting(WebHostDefaults.ServerUrlsKey));
    }

    [Fact]
    public async Task Host_factory_and_settings_seams_reject_null_arguments()
    {
        var builder = CreateStartupBuilder(Environments.Development);

        await Assert.ThrowsAsync<ArgumentNullException>(() => WorkerApplication.BuildAsync(null!));
        Assert.Throws<ArgumentNullException>(() => WorkerApplication.ValidateAuthenticationComposition(null!));
        Assert.Throws<ArgumentNullException>(() => WorkerHostSettings.Read(null!, builder.Environment));
        Assert.Throws<ArgumentNullException>(() => WorkerHostSettings.Read(builder.Configuration, null!));
        Assert.Throws<ArgumentNullException>(() => WorkerDataSources.Create(null!));
    }

    [Fact]
    public void Authentication_composition_rejects_missing_services_policy_requirements_schemes_and_defaults()
    {
        var noAuthorization = new ServiceCollection();
        Assert.Contains("Register authorization services", Assert.Throws<InvalidOperationException>(
            () => WorkerApplication.ValidateAuthenticationComposition(noAuthorization)).Message, StringComparison.Ordinal);

        var noSchemeProvider = new ServiceCollection();
        noSchemeProvider.AddAuthorization();
        noSchemeProvider.AddAuthentication();
        noSchemeProvider.RemoveAll<IAuthenticationSchemeProvider>();
        Assert.Contains("authentication scheme", Assert.Throws<InvalidOperationException>(
            () => WorkerApplication.ValidateAuthenticationComposition(noSchemeProvider)).Message, StringComparison.OrdinalIgnoreCase);

        var noNamedPolicy = CreateAuthenticationServices(addPolicy: false);
        Assert.Contains("named ActivationAuthorization", Assert.Throws<InvalidOperationException>(
            () => WorkerApplication.ValidateAuthenticationComposition(noNamedPolicy)).Message, StringComparison.Ordinal);

        var anonymousPolicy = CreateAuthenticationServices(requireAuthenticatedUser: false);
        Assert.Contains("must require an authenticated user", Assert.Throws<InvalidOperationException>(
            () => WorkerApplication.ValidateAuthenticationComposition(anonymousPolicy)).Message, StringComparison.Ordinal);

        var unknownSelectedScheme = CreateAuthenticationServices(selectedScheme: "not-registered");
        Assert.Contains("unregistered authentication scheme", Assert.Throws<InvalidOperationException>(
            () => WorkerApplication.ValidateAuthenticationComposition(unknownSelectedScheme)).Message, StringComparison.Ordinal);

        var noDefaults = CreateAuthenticationServices(defaultSchemes: false, addSecondScheme: true);
        Assert.Contains("Register default authenticate and challenge schemes", Assert.Throws<InvalidOperationException>(
            () => WorkerApplication.ValidateAuthenticationComposition(noDefaults)).Message, StringComparison.Ordinal);

        WorkerApplication.ValidateAuthenticationComposition(CreateAuthenticationServices());
        WorkerApplication.ValidateAuthenticationComposition(CreateAuthenticationServices(
            defaultSchemes: false,
            selectedScheme: "contract-test",
            addSelectedScheme: true));
    }

    [Fact]
    public async Task Production_startup_rejects_authentication_before_creating_sources_or_reading_postgres()
    {
        var builder = CreateStartupBuilder(Environments.Production);
        var dataSourceFactoryCalled = false;
        var schemaReaderCalled = false;

        var publicApiException = await Assert.ThrowsAsync<InvalidOperationException>(
            () => WorkerApplication.BuildAsync(builder));
        Assert.Contains("Register authorization services", publicApiException.Message, StringComparison.Ordinal);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => WorkerApplication.BuildWithStartupSeamsAsync(
            builder,
            settings =>
            {
                dataSourceFactoryCalled = true;
                return WorkerDataSources.Create(settings);
            },
            (_, _) =>
            {
                schemaReaderCalled = true;
                return ValueTask.FromException<DurableRuntimeSchemaStatus>(new InvalidOperationException("database reached"));
            },
            TimeSpan.FromSeconds(2)));

        Assert.Contains("Register authorization services", exception.Message, StringComparison.Ordinal);
        Assert.False(dataSourceFactoryCalled);
        Assert.False(schemaReaderCalled);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Authentication_composition_requires_both_default_schemes(bool missingChallenge)
    {
        var services = CreateAuthenticationServices(addSecondScheme: true);
        services.Configure<AuthenticationOptions>(options =>
        {
            if (missingChallenge)
            {
                options.DefaultChallengeScheme = null;
            }
            else
            {
                options.DefaultAuthenticateScheme = null;
            }
        });

        var exception = Assert.Throws<InvalidOperationException>(
            () => WorkerApplication.ValidateAuthenticationComposition(services));

        Assert.Contains("Register default authenticate and challenge schemes", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Public_startup_schema_reader_preserves_preexisting_caller_cancellation()
    {
        var builder = CreateStartupBuilder(Environments.Development);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => WorkerApplication.BuildAsync(builder, cancellation.Token));
    }

    [Fact]
    public async Task Production_startup_rejects_development_token_reloaded_after_settings_validation()
    {
        var builder = CreateStartupBuilder(Environments.Production);
        ((IConfigurationBuilder)builder.Configuration).Add(new DevelopmentTokenReloadSource());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => WorkerApplication.BuildAsync(builder));

        Assert.Contains("allowed only in Development", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SensitiveMarker, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Startup_is_passive_until_started_and_application_disposal_owns_both_sources()
    {
        var builder = CreateStartupBuilder(Environments.Development);
        WorkerDataSources? createdSources = null;
        var app = await WorkerApplication.BuildWithStartupSeamsAsync(
            builder,
            settings => createdSources = WorkerDataSources.Create(settings),
            static (_, _) => ValueTask.FromResult(CompatibleSchemaStatus()),
            TimeSpan.FromSeconds(2));

        try
        {
            Assert.NotNull(createdSources);
            Assert.Same(createdSources, app.Services.GetRequiredService<WorkerDataSources>());
            Assert.NotSame(createdSources.Dispatcher, createdSources.Runtime);
            Assert.False(app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.IsCancellationRequested);

            await WorkerApplication.StartAsync(app);

            Assert.True(app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.IsCancellationRequested);
            Assert.NotNull(app.Services.GetRequiredService<IDurableExternalActivationService>());
        }
        finally
        {
            await app.DisposeAsync();
        }

        await AssertDataSourcesDisposedAsync(createdSources);
    }

    [Fact]
    public async Task Failed_schema_identity_validation_disposes_sources_before_any_listener_is_built()
    {
        var builder = CreateStartupBuilder(Environments.Development);
        WorkerDataSources? createdSources = null;
        var mismatch = CompatibleSchemaStatus(storeId: Guid.NewGuid());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => WorkerApplication.BuildWithStartupSeamsAsync(
            builder,
            settings => createdSources = WorkerDataSources.Create(settings),
            (_, _) => ValueTask.FromResult(mismatch),
            TimeSpan.FromSeconds(2)));

        Assert.Contains("StoreId", exception.Message, StringComparison.Ordinal);
        Assert.NotNull(createdSources);
        await AssertDataSourcesDisposedAsync(createdSources);
    }

    [Fact]
    public async Task Startup_deadline_maps_timeout_and_disposes_sources()
    {
        var builder = CreateStartupBuilder(Environments.Development);
        WorkerDataSources? createdSources = null;

        var exception = await Assert.ThrowsAsync<TimeoutException>(() => WorkerApplication.BuildWithStartupSeamsAsync(
            builder,
            settings => createdSources = WorkerDataSources.Create(settings),
            static async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return CompatibleSchemaStatus();
            },
            TimeSpan.FromMilliseconds(80)));

        Assert.Contains("configured budget", exception.Message, StringComparison.Ordinal);
        Assert.NotNull(createdSources);
        await AssertDataSourcesDisposedAsync(createdSources);
    }

    [Fact]
    public async Task Caller_cancellation_during_startup_is_preserved_and_disposes_sources()
    {
        var builder = CreateStartupBuilder(Environments.Development);
        WorkerDataSources? createdSources = null;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WorkerApplication.BuildWithStartupSeamsAsync(
            builder,
            settings => createdSources = WorkerDataSources.Create(settings),
            static async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return CompatibleSchemaStatus();
            },
            TimeSpan.FromSeconds(2),
            cancellation.Token));

        Assert.NotNull(createdSources);
        await AssertDataSourcesDisposedAsync(createdSources);
    }

    [Fact]
    public async Task Failure_after_application_build_disposes_container_owned_sources()
    {
        var builder = CreateStartupBuilder(Environments.Development);
        var sentinel = new InvalidOperationException("composition sentinel");
        builder.Services.AddSingleton<IDurableExternalActivationService>(_ => throw sentinel);
        WorkerDataSources? createdSources = null;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => WorkerApplication.BuildWithStartupSeamsAsync(
            builder,
            settings => createdSources = WorkerDataSources.Create(settings),
            static (_, _) => ValueTask.FromResult(CompatibleSchemaStatus()),
            TimeSpan.FromSeconds(2)));

        Assert.Same(sentinel, exception);
        Assert.NotNull(createdSources);
        await AssertDataSourcesDisposedAsync(createdSources);
    }

    [Fact]
    public async Task Startup_deadline_uses_monotonic_time_and_start_maps_expiration_and_nulls()
    {
        var active = new WorkerStartupDeadline(Stopwatch.GetTimestamp(), TimeSpan.FromSeconds(1));
        Assert.True(active.GetRemaining() > TimeSpan.Zero);
        active.ThrowIfExpired();

        var expired = new WorkerStartupDeadline(
            Stopwatch.GetTimestamp() - (2 * Stopwatch.Frequency),
            TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.Zero, expired.GetRemaining());
        Assert.Throws<TimeoutException>(expired.ThrowIfExpired);
        await Assert.ThrowsAsync<ArgumentNullException>(() => WorkerApplication.StartAsync(null!));

        var missingDeadline = CreateBareTestBuilder().Build();
        await using (missingDeadline)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => WorkerApplication.StartAsync(missingDeadline));
        }

        var expiredAppBuilder = CreateBareTestBuilder();
        expiredAppBuilder.Services.AddSingleton(expired);
        await using var expiredApp = expiredAppBuilder.Build();
        await Assert.ThrowsAsync<TimeoutException>(() => WorkerApplication.StartAsync(expiredApp));
    }

    [Fact]
    public async Task Start_timeout_during_hosted_service_start_is_translated_but_caller_cancel_is_not()
    {
        var timeoutBuilder = CreateBareTestBuilder();
        timeoutBuilder.Services.AddSingleton(new WorkerStartupDeadline(Stopwatch.GetTimestamp(), TimeSpan.FromMilliseconds(80)));
        timeoutBuilder.Services.AddHostedService<BlockingStartupService>();
        await using var timeoutApp = timeoutBuilder.Build();
        await Assert.ThrowsAsync<TimeoutException>(() => WorkerApplication.StartAsync(timeoutApp));

        var canceledBuilder = CreateBareTestBuilder();
        canceledBuilder.Services.AddSingleton(new WorkerStartupDeadline(Stopwatch.GetTimestamp(), TimeSpan.FromSeconds(2)));
        await using var canceledApp = canceledBuilder.Build();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WorkerApplication.StartAsync(canceledApp, cancellation.Token));
    }

    [Fact]
    public async Task Development_auth_rejects_missing_wrong_malformed_duplicate_and_overlong_tokens_before_body_or_service()
    {
        await using var server = await CreateHttpServerAsync();
        var cases = new (string?[] Values, int StatusCode)[]
        {
            ([], StatusCodes.Status401Unauthorized),
            (["Basic " + ValidToken], StatusCodes.Status401Unauthorized),
            (["Bearer"], StatusCodes.Status401Unauthorized),
            (["Bearer " + new string('x', 257)], StatusCodes.Status401Unauthorized),
            (["Bearer wrong-token"], StatusCodes.Status401Unauthorized),
            (["Bearer " + ValidToken, "Bearer " + ValidToken], StatusCodes.Status401Unauthorized),
            (["Bearer " + ValidToken[..^1] + "x"], StatusCodes.Status401Unauthorized),
        };

        foreach (var testCase in cases)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ActivationPath)
            {
                Content = new ByteArrayContent([42]),
            };
            request.Content.Headers.ContentLength = null;
            if (testCase.Values.Length > 0)
            {
                foreach (var value in testCase.Values)
                {
                    request.Headers.TryAddWithoutValidation("Authorization", value);
                }
            }
            using var response = await server.Client.SendAsync(request);
            Assert.True(
                testCase.StatusCode == (int)response.StatusCode,
                $"Authorization values [{string.Join(" | ", testCase.Values)}] returned {(int)response.StatusCode}; "
                + $"body reads={server.State.BodyReads}, activation calls={server.State.ActivationCalls}, "
                + $"response={await response.Content.ReadAsStringAsync()}");
            Assert.Equal(0, Volatile.Read(ref server.State.BodyReads));
            Assert.Equal(0, server.State.ActivationCalls);
        }
    }

    [Fact]
    public void Development_bearer_validation_covers_header_shape_utf8_bounds_and_exact_matching()
    {
        Assert.False(DevelopmentBearerHandler.IsValidBearerHeader(StringValues.Empty, ValidToken));
        Assert.False(DevelopmentBearerHandler.IsValidBearerHeader(new StringValues(new string?[] { null }), ValidToken));
        Assert.False(DevelopmentBearerHandler.IsValidBearerHeader(
            new StringValues(["Bearer " + ValidToken, "Bearer " + ValidToken]),
            ValidToken));
        Assert.False(DevelopmentBearerHandler.IsValidBearerHeader(new StringValues("Bearer"), ValidToken));
        Assert.False(DevelopmentBearerHandler.IsValidBearerHeader(new StringValues("Basic " + ValidToken), ValidToken));
        Assert.False(DevelopmentBearerHandler.IsValidBearerHeader(new StringValues("Bearer "), ValidToken));
        Assert.False(DevelopmentBearerHandler.IsValidBearerHeader(
            new StringValues("Bearer " + new string('x', 257)),
            ValidToken));
        Assert.False(DevelopmentBearerHandler.IsValidBearerHeader(
            new StringValues("Bearer " + new string('é', 129)),
            ValidToken));
        Assert.False(DevelopmentBearerHandler.IsValidBearerHeader(
            new StringValues("Bearer token with-spaces"),
            ValidToken));
        Assert.False(DevelopmentBearerHandler.IsValidBearerHeader(
            new StringValues("Bearer token\twith-tab"),
            ValidToken));
        Assert.False(DevelopmentBearerHandler.IsValidBearerHeader(
            new StringValues("Bearer token\rwith-return"),
            ValidToken));
        Assert.False(DevelopmentBearerHandler.IsValidBearerHeader(
            new StringValues("Bearer token\nwith-line-feed"),
            ValidToken));
        Assert.False(DevelopmentBearerHandler.IsValidBearerHeader(new StringValues("Bearer wrong-token"), ValidToken));
        Assert.True(DevelopmentBearerHandler.IsValidBearerHeader(
            new StringValues("bEaReR " + ValidToken),
            ValidToken));
        Assert.Throws<ArgumentNullException>(() =>
            DevelopmentBearerHandler.IsValidBearerHeader(new StringValues("Bearer " + ValidToken), null!));
    }

    [Fact]
    public async Task Development_auth_distinguishes_wrong_permission_and_authorized_requests()
    {
        await using var server = await CreateHttpServerAsync();

        using (var request = new HttpRequestMessage(HttpMethod.Post, ActivationPath))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ValidToken);
            request.Headers.Add("X-Test-No-Permission", "true");
            request.Content = new ByteArrayContent([]);
            using var response = await server.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        Assert.Equal(0, server.State.ActivationCalls);
        using (var request = new HttpRequestMessage(HttpMethod.Post, ActivationPath)
        {
            Content = new ByteArrayContent([]),
        })
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ValidToken);
            using var response = await server.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(1, server.State.ActivationCalls);
    }

    [Fact]
    public async Task Activation_rejects_known_nonempty_and_unknown_first_byte_without_calling_service()
    {
        var knownService = new RecordingActivationService(CompletedResult());
        var known = CreateEndpointContext(knownService, contentLength: 512);
        known.Request.Body = new TrackingReadStream([1, 2, 3]);
        var knownResult = await ActivationEndpoints.ActivateAsync(known, TestActivationSettings());
        Assert.Equal(StatusCodes.Status400BadRequest, await ExecuteAsync(knownResult, known));
        Assert.Equal(0, ((TrackingReadStream)known.Request.Body).ReadCalls);
        Assert.Equal(0, knownService.Calls);
        Assert.Equal("WakeBodyMustBeEmpty", await ReadErrorAsync(known.Response.Body));

        var unknownService = new RecordingActivationService(CompletedResult());
        var unknown = CreateEndpointContext(unknownService, contentLength: null);
        unknown.Request.Body = new TrackingReadStream([42, 43, 44]);
        var unknownResult = await ActivationEndpoints.ActivateAsync(unknown, TestActivationSettings());
        Assert.Equal(StatusCodes.Status400BadRequest, await ExecuteAsync(unknownResult, unknown));
        Assert.Equal(1, ((TrackingReadStream)unknown.Request.Body).ReadCalls);
        Assert.Equal(0, unknownService.Calls);
        Assert.Equal("WakeBodyMustBeEmpty", await ReadErrorAsync(unknown.Response.Body));
    }

    [Fact]
    public async Task Activation_accepts_known_empty_and_unknown_eof_then_calls_only_the_work_surface()
    {
        foreach (var contentLength in new long?[] { 0, null })
        {
            var service = new RecordingActivationService(CompletedResult());
            var context = CreateEndpointContext(service, contentLength);
            context.Request.Body = new TrackingReadStream([]);

            var result = await ActivationEndpoints.ActivateAsync(context, TestActivationSettings());

            Assert.Equal(StatusCodes.Status200OK, await ExecuteAsync(result, context));
            Assert.Equal(1, service.Calls);
            Assert.Equal(contentLength is null ? 1 : 0, ((TrackingReadStream)context.Request.Body).ReadCalls);
            Assert.Equal(17, service.LastRequest!.PumpRequest.MaximumItems);
            Assert.Equal(TimeSpan.FromSeconds(3), service.LastRequest.PumpRequest.TimeBudget);
            Assert.Equal(DurableRuntimeSurface.Work, service.LastRequest.PumpRequest.Surfaces);
            Assert.Equal(TimeSpan.FromSeconds(9), service.LastRequest.RequestBudget);
        }
    }

    [Fact]
    public async Task Unknown_body_deadline_returns_safe_504_without_health_or_activation_calls()
    {
        var service = new RecordingActivationService(CompletedResult());
        var context = CreateEndpointContext(service, contentLength: null);
        context.Request.Body = new BlockingReadStream();
        var timer = Stopwatch.StartNew();

        var result = await ActivationEndpoints.ActivateAsync(context, TestActivationSettings(bodyReadBudget: TimeSpan.FromMilliseconds(50)));

        Assert.InRange(timer.Elapsed, TimeSpan.FromMilliseconds(25), TimeSpan.FromSeconds(2));
        Assert.Equal(StatusCodes.Status504GatewayTimeout, await ExecuteAsync(result, context));
        Assert.Equal("WakeBodyReadTimeout", await ReadErrorAsync(context.Response.Body));
        Assert.Equal(0, service.Calls);
    }

    [Fact]
    public async Task Unknown_body_caller_abort_and_read_fault_map_to_fixed_safe_host_failure()
    {
        var canceledService = new RecordingActivationService(CompletedResult());
        var canceled = CreateEndpointContext(canceledService, contentLength: null);
        using var callerAbort = new CancellationTokenSource();
        canceled.RequestAborted = callerAbort.Token;
        var abortBody = new TrackingReadStream(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        });
        canceled.Request.Body = abortBody;
        var canceledTask = ActivationEndpoints.ActivateAsync(canceled, TestActivationSettings());
        await abortBody.ReadStarted.WaitAsync(TimeSpan.FromSeconds(2));
        callerAbort.Cancel();
        var canceledResult = await canceledTask;
        canceled.RequestAborted = CancellationToken.None;
        Assert.Equal(StatusCodes.Status500InternalServerError, await ExecuteAsync(canceledResult, canceled));
        Assert.Equal("HostFailure", await ReadErrorAsync(canceled.Response.Body));
        Assert.Equal(0, canceledService.Calls);

        var faultedService = new RecordingActivationService(CompletedResult());
        var faulted = CreateEndpointContext(faultedService, contentLength: null);
        faulted.Request.Body = new FaultingReadStream(new IOException(SensitiveMarker));
        var faultedResult = await ActivationEndpoints.ActivateAsync(faulted, TestActivationSettings());
        Assert.Equal(StatusCodes.Status500InternalServerError, await ExecuteAsync(faultedResult, faulted));
        Assert.Equal("HostFailure", await ReadErrorAsync(faulted.Response.Body));
        Assert.DoesNotContain(SensitiveMarker, Encoding.UTF8.GetString(((MemoryStream)faulted.Response.Body).ToArray()), StringComparison.Ordinal);
        Assert.Equal(0, faultedService.Calls);
    }

    [Fact]
    public async Task Activation_service_fault_and_cancellation_are_projected_without_exception_details()
    {
        var failing = new RecordingActivationService(exception: new InvalidOperationException(SensitiveMarker));
        var failingContext = CreateEndpointContext(failing, contentLength: 0);
        var failure = await ActivationEndpoints.ActivateAsync(failingContext, TestActivationSettings());
        Assert.Equal(StatusCodes.Status500InternalServerError, await ExecuteAsync(failure, failingContext));
        Assert.Equal("HostFailure", await ReadErrorAsync(failingContext.Response.Body));
        Assert.DoesNotContain(SensitiveMarker, Encoding.UTF8.GetString(((MemoryStream)failingContext.Response.Body).ToArray()), StringComparison.Ordinal);

        using var canceledToken = new CancellationTokenSource();
        canceledToken.Cancel();
        var canceled = new RecordingActivationService(cancellationToken: canceledToken.Token);
        var canceledContext = CreateEndpointContext(canceled, contentLength: 0);
        var cancellation = await ActivationEndpoints.ActivateAsync(canceledContext, TestActivationSettings());
        Assert.Equal(StatusCodes.Status500InternalServerError, await ExecuteAsync(cancellation, canceledContext));
        Assert.Equal("HostFailure", await ReadErrorAsync(canceledContext.Response.Body));

        using var requestAbort = new CancellationTokenSource();
        requestAbort.Cancel();
        var transportCanceled = new RecordingActivationService(cancellationToken: requestAbort.Token);
        var transportContext = CreateEndpointContext(transportCanceled, contentLength: 0);
        transportContext.RequestAborted = requestAbort.Token;
        var transportCancellation = await ActivationEndpoints.ActivateAsync(transportContext, TestActivationSettings());
        transportContext.RequestAborted = CancellationToken.None;
        Assert.Equal(StatusCodes.Status500InternalServerError, await ExecuteAsync(transportCancellation, transportContext));
        Assert.Equal("HostFailure", await ReadErrorAsync(transportContext.Response.Body));

        var fatal = new RecordingActivationService(exception: new OutOfMemoryException(SensitiveMarker));
        var fatalContext = CreateEndpointContext(fatal, contentLength: 0);
        await Assert.ThrowsAsync<OutOfMemoryException>(async () =>
            await ActivationEndpoints.ActivateAsync(fatalContext, TestActivationSettings()));
    }

    [Fact]
    public async Task All_ten_activation_outcomes_keep_exact_status_and_safe_dto_allowlists()
    {
        var dueAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(-5)).AddTicks(1_234_567);
        var aggregate = new DurableRuntimePumpResult(12, 9, 7, 1, 1, true, dueAt, TimeSpan.FromTicks(98_765));
        var cases = new (DurableExternalActivationResult Result, int StatusCode)[]
        {
            (ActivationResult(DurableExternalActivationOutcomeKind.Unavailable, DurableRuntimeHealthState.Unavailable, DurableProblemCodes.StoreUnavailable), StatusCodes.Status503ServiceUnavailable),
            (ActivationResult(DurableExternalActivationOutcomeKind.Incompatible, DurableRuntimeHealthState.Incompatible, DurableProblemCodes.SchemaMissing), StatusCodes.Status503ServiceUnavailable),
            (ActivationResult(DurableExternalActivationOutcomeKind.Draining, DurableRuntimeHealthState.Draining, null), StatusCodes.Status503ServiceUnavailable),
            (ActivationResult(DurableExternalActivationOutcomeKind.Busy, DurableRuntimeHealthState.Healthy, null), StatusCodes.Status409Conflict),
            (ActivationResult(DurableExternalActivationOutcomeKind.CanceledBeforeAdmission, null, null), StatusCodes.Status408RequestTimeout),
            (ActivationResult(DurableExternalActivationOutcomeKind.RequestBudgetExceeded, null, null), StatusCodes.Status504GatewayTimeout),
            (ActivationResult(DurableExternalActivationOutcomeKind.Completed, DurableRuntimeHealthState.Healthy, null, aggregate), StatusCodes.Status200OK),
            (ActivationResult(DurableExternalActivationOutcomeKind.ActivationFailed, null, DurableProblemCodes.ExternalActivationFailed), StatusCodes.Status500InternalServerError),
            (ActivationResult(DurableExternalActivationOutcomeKind.PumpCanceled, DurableRuntimeHealthState.Stale, DurableProblemCodes.ActivatorStale), StatusCodes.Status408RequestTimeout),
            (ActivationResult(DurableExternalActivationOutcomeKind.PumpFailed, DurableRuntimeHealthState.Healthy, DurableProblemCodes.ExternalActivationFailed), StatusCodes.Status500InternalServerError),
        };

        Assert.Equal(10, cases.Length);
        foreach (var (serviceResult, statusCode) in cases)
        {
            var service = new RecordingActivationService(serviceResult);
            var context = CreateEndpointContext(service, contentLength: 0);
            var result = await ActivationEndpoints.ActivateAsync(context, TestActivationSettings());

            Assert.Equal(statusCode, await ExecuteAsync(result, context));
            Assert.Equal(1, service.Calls);
            Assert.Equal(statusCode, ActivationEndpoints.GetStatusCode(serviceResult));
            using var document = JsonDocument.Parse(((MemoryStream)context.Response.Body).ToArray());
            Assert.Equal(
                ["observedHealthState", "outcome", "problemCode", "pumpResult"],
                document.RootElement.EnumerateObject().Select(static property => property.Name).Order(StringComparer.Ordinal));
            Assert.Equal(serviceResult.Kind.ToString(), document.RootElement.GetProperty("outcome").GetString());
            Assert.Equal(serviceResult.ObservedHealthState?.ToString(), document.RootElement.GetProperty("observedHealthState").GetString());
            Assert.Equal(serviceResult.ProblemCode, document.RootElement.GetProperty("problemCode").GetString());

            var pump = document.RootElement.GetProperty("pumpResult");
            if (serviceResult.PumpResult is null)
            {
                Assert.Equal(JsonValueKind.Null, pump.ValueKind);
            }
            else
            {
                Assert.Equal(
                    ["claimed", "deferred", "discovered", "elapsedTicks", "failed", "hasMore", "nextDueAtUtc", "processed"],
                    pump.EnumerateObject().Select(static property => property.Name).Order(StringComparer.Ordinal));
                Assert.Equal(12, pump.GetProperty("discovered").GetInt32());
                Assert.Equal(9, pump.GetProperty("claimed").GetInt32());
                Assert.Equal(7, pump.GetProperty("processed").GetInt32());
                Assert.Equal(1, pump.GetProperty("deferred").GetInt32());
                Assert.Equal(1, pump.GetProperty("failed").GetInt32());
                Assert.True(pump.GetProperty("hasMore").GetBoolean());
                Assert.Equal("2026-01-02T08:04:05.1234567Z", pump.GetProperty("nextDueAtUtc").GetString());
                Assert.Equal(98_765, pump.GetProperty("elapsedTicks").GetInt64());
            }
        }
    }

    [Fact]
    public void Pump_failed_status_distinguishes_store_and_each_compatibility_code_from_operational_failure()
    {
        var compatibilityCodes = new[]
        {
            DurableProblemCodes.RecoveryEpochRequired,
            DurableProblemCodes.SchemaMissing,
            DurableProblemCodes.SchemaUpgradeRequired,
            DurableProblemCodes.SchemaVersionUnsupported,
            DurableProblemCodes.SchemaInconsistent,
        };

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, ActivationEndpoints.GetStatusCode(
            ActivationResult(DurableExternalActivationOutcomeKind.PumpFailed, DurableRuntimeHealthState.Healthy, DurableProblemCodes.StoreUnavailable)));
        foreach (var code in compatibilityCodes)
        {
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, ActivationEndpoints.GetStatusCode(
                ActivationResult(DurableExternalActivationOutcomeKind.PumpFailed, DurableRuntimeHealthState.Healthy, code)));
        }

        Assert.Equal(StatusCodes.Status500InternalServerError, ActivationEndpoints.GetStatusCode(
            ActivationResult(DurableExternalActivationOutcomeKind.PumpFailed, DurableRuntimeHealthState.Healthy, DurableProblemCodes.ExternalActivationFailed)));
    }

    [Fact]
    public async Task Health_probes_keep_liveness_independent_and_distinguish_compatibility_from_readiness()
    {
        var health = new MutableRuntimeHealth(HealthSnapshot(DurableRuntimeHealthState.Healthy));
        var activation = new RecordingActivationService(CompletedResult());
        await using var server = await CreateHttpServerAsync(activation, health);

        using (var live = await server.Client.GetAsync("/live"))
        {
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
            using var document = JsonDocument.Parse(await live.Content.ReadAsStreamAsync());
            Assert.Equal(["status"], document.RootElement.EnumerateObject().Select(static property => property.Name));
            Assert.Equal("Live", document.RootElement.GetProperty("status").GetString());
        }
        Assert.Equal(0, health.Calls);
        Assert.Equal(0, activation.Calls);

        var states = new (DurableRuntimeHealthSnapshot Snapshot, int CompatibilityStatus, int ReadinessStatus)[]
        {
            (HealthSnapshot(DurableRuntimeHealthState.NotStarted), StatusCodes.Status200OK, StatusCodes.Status503ServiceUnavailable),
            (HealthSnapshot(DurableRuntimeHealthState.Healthy), StatusCodes.Status200OK, StatusCodes.Status200OK),
            (HealthSnapshot(DurableRuntimeHealthState.Stale, DurableProblemCodes.ActivatorStale), StatusCodes.Status200OK, StatusCodes.Status503ServiceUnavailable),
            (HealthSnapshot(DurableRuntimeHealthState.Draining, isDraining: true), StatusCodes.Status200OK, StatusCodes.Status503ServiceUnavailable),
            (HealthSnapshot(DurableRuntimeHealthState.Incompatible, DurableProblemCodes.SchemaMissing, schemaCompatible: false), StatusCodes.Status503ServiceUnavailable, StatusCodes.Status503ServiceUnavailable),
            (HealthSnapshot(DurableRuntimeHealthState.Unavailable, DurableProblemCodes.StoreUnavailable, schemaCompatible: false, epochCompatible: false), StatusCodes.Status503ServiceUnavailable, StatusCodes.Status503ServiceUnavailable),
        };

        foreach (var (snapshot, compatibilityStatus, readinessStatus) in states)
        {
            health.Snapshot = snapshot;
            using var compatibility = await server.Client.GetAsync("/compatibility");
            Assert.Equal(compatibilityStatus, (int)compatibility.StatusCode);
            await AssertProbeProjectionAsync(compatibility, snapshot, compatibilityStatus);

            using var readiness = await server.Client.GetAsync("/ready");
            Assert.Equal(readinessStatus, (int)readiness.StatusCode);
            await AssertProbeProjectionAsync(readiness, snapshot, readinessStatus);
        }

        Assert.Equal(12, health.Calls);
    }

    [Fact]
    public void Assessment_validator_accepts_the_closed_states_and_rejects_contradictory_snapshots()
    {
        var valid = new[]
        {
            HealthSnapshot(DurableRuntimeHealthState.Unavailable, DurableProblemCodes.StoreUnavailable, schemaCompatible: false, epochCompatible: false),
            HealthSnapshot(DurableRuntimeHealthState.Incompatible, DurableProblemCodes.SchemaMissing, schemaCompatible: false),
            HealthSnapshot(DurableRuntimeHealthState.NotStarted),
            HealthSnapshot(DurableRuntimeHealthState.NotStarted, DurableProblemCodes.ActivatorStale),
            HealthSnapshot(DurableRuntimeHealthState.Healthy),
            HealthSnapshot(DurableRuntimeHealthState.Draining, isDraining: true),
            HealthSnapshot(DurableRuntimeHealthState.Stale),
            HealthSnapshot(DurableRuntimeHealthState.Stale, DurableProblemCodes.ActivatorStale),
            HealthSnapshot(DurableRuntimeHealthState.Stale, DurableProblemCodes.WorkerIdentityConflict),
        };
        foreach (var snapshot in valid)
        {
            ActivationEndpoints.ValidateAssessment(snapshot);
        }

        var invalid = new[]
        {
            HealthSnapshot(DurableRuntimeHealthState.Unavailable, "UnexpectedCode", schemaCompatible: false, epochCompatible: false),
            HealthSnapshot(DurableRuntimeHealthState.Unavailable, DurableProblemCodes.StoreUnavailable, schemaCompatible: true, epochCompatible: false),
            HealthSnapshot(DurableRuntimeHealthState.Healthy, DurableProblemCodes.SchemaMissing),
            HealthSnapshot(DurableRuntimeHealthState.Healthy, isDraining: true),
            HealthSnapshot(DurableRuntimeHealthState.NotStarted, isDraining: true),
            HealthSnapshot(DurableRuntimeHealthState.NotStarted, "UnexpectedCode"),
            HealthSnapshot(DurableRuntimeHealthState.Draining),
            HealthSnapshot(DurableRuntimeHealthState.Stale, "UnexpectedCode"),
            HealthSnapshot(DurableRuntimeHealthState.Healthy, schemaCompatible: false),
        };
        foreach (var snapshot in invalid)
        {
            Assert.Throws<InvalidDataException>(() => ActivationEndpoints.ValidateAssessment(snapshot));
        }

        Assert.Throws<ArgumentNullException>(() => ActivationEndpoints.ValidateAssessment(null!));
    }

    [Fact]
    public async Task Startup_seams_and_schema_identity_reject_invalid_arguments_and_every_identity_mismatch()
    {
        var builder = CreateStartupBuilder(Environments.Development);
        var settings = WorkerHostSettings.Read(builder.Configuration, builder.Environment);

        await Assert.ThrowsAsync<ArgumentNullException>(() => WorkerApplication.BuildWithStartupSeamsAsync(
            null!,
            static _ => throw new InvalidOperationException(),
            static (_, _) => ValueTask.FromResult(CompatibleSchemaStatus()),
            TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAsync<ArgumentNullException>(() => WorkerApplication.BuildWithStartupSeamsAsync(
            builder,
            null!,
            static (_, _) => ValueTask.FromResult(CompatibleSchemaStatus()),
            TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAsync<ArgumentNullException>(() => WorkerApplication.BuildWithStartupSeamsAsync(
            builder,
            static _ => throw new InvalidOperationException(),
            null!,
            TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => WorkerApplication.BuildWithStartupSeamsAsync(
            builder,
            static _ => throw new InvalidOperationException(),
            static (_, _) => ValueTask.FromResult(CompatibleSchemaStatus()),
            TimeSpan.Zero));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => WorkerApplication.BuildWithStartupSeamsAsync(
            builder,
            static _ => throw new InvalidOperationException(),
            static (_, _) => ValueTask.FromResult(CompatibleSchemaStatus()),
            TimeSpan.FromMilliseconds(uint.MaxValue)));
        Assert.Throws<ArgumentNullException>(() => WorkerApplication.ValidateSchemaIdentity(null!, settings));
        Assert.Throws<ArgumentNullException>(() => WorkerApplication.ValidateSchemaIdentity(CompatibleSchemaStatus(), null!));

        Assert.Throws<InvalidOperationException>(() => WorkerApplication.ValidateSchemaIdentity(
            CompatibleSchemaStatus(compatibility: DurableRuntimeSchemaCompatibility.Missing),
            settings));
        Assert.Throws<InvalidOperationException>(() => WorkerApplication.ValidateSchemaIdentity(
            CompatibleSchemaStatus(storeId: Guid.NewGuid()),
            settings));
        Assert.Throws<InvalidOperationException>(() => WorkerApplication.ValidateSchemaIdentity(
            CompatibleSchemaStatus(storeId: Guid.Empty),
            settings));
        Assert.Throws<InvalidOperationException>(() => WorkerApplication.ValidateSchemaIdentity(
            CompatibleSchemaStatus(activeRuntimeEpoch: Guid.NewGuid()),
            settings));

        WorkerApplication.ValidateSchemaIdentity(CompatibleSchemaStatus(), settings);
    }

    [Fact]
    public async Task Probe_failures_and_cancellation_use_fixed_safe_responses()
    {
        var context = CreateEndpointContext(new RecordingActivationService(CompletedResult()), contentLength: 0);
        await Assert.ThrowsAsync<ArgumentNullException>(() => ActivationEndpoints.ProbeAsync(null!, readiness: false));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ActivationEndpoints.ActivateAsync(null!, TestActivationSettings()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ActivationEndpoints.ActivateAsync(context, null!));
        Assert.Throws<ArgumentNullException>(() => ActivationEndpoints.GetStatusCode(null!));

        var fatalHealth = new MutableRuntimeHealth(
            HealthSnapshot(DurableRuntimeHealthState.Healthy),
            new OutOfMemoryException(SensitiveMarker));
        var fatalContext = CreateProbeContext(fatalHealth);
        await Assert.ThrowsAsync<OutOfMemoryException>(async () =>
            await ActivationEndpoints.ProbeAsync(fatalContext, readiness: false));

        var failedHealth = new MutableRuntimeHealth(
            HealthSnapshot(DurableRuntimeHealthState.Healthy),
            new InvalidOperationException(SensitiveMarker));
        var failedContext = CreateProbeContext(failedHealth);
        var failedResult = await ActivationEndpoints.ProbeAsync(failedContext, readiness: true);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, await ExecuteAsync(failedResult, failedContext));
        using (var failure = JsonDocument.Parse(((MemoryStream)failedContext.Response.Body).ToArray()))
        {
            Assert.Equal("ProbeFailed", failure.RootElement.GetProperty("outcome").GetString());
            Assert.Equal(DurableProblemCodes.ExternalActivationFailed, failure.RootElement.GetProperty("problemCode").GetString());
            Assert.DoesNotContain(SensitiveMarker, failure.RootElement.ToString(), StringComparison.Ordinal);
        }

        using var callerAbort = new CancellationTokenSource();
        callerAbort.Cancel();
        var canceledHealth = new MutableRuntimeHealth(
            HealthSnapshot(DurableRuntimeHealthState.Healthy),
            configuredCancellationToken: callerAbort.Token);
        var canceledContext = CreateProbeContext(canceledHealth);
        canceledContext.RequestAborted = callerAbort.Token;
        var canceledResult = await ActivationEndpoints.ProbeAsync(canceledContext, readiness: false);
        canceledContext.RequestAborted = CancellationToken.None;
        Assert.Equal(StatusCodes.Status408RequestTimeout, await ExecuteAsync(canceledResult, canceledContext));
        using var canceled = JsonDocument.Parse(((MemoryStream)canceledContext.Response.Body).ToArray());
        Assert.Equal("ProbeCanceled", canceled.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, canceled.RootElement.GetProperty("problemCode").ValueKind);
    }

    [Fact]
    public void Telemetry_registration_handles_null_and_optional_exporter_without_network_io()
    {
        Assert.Throws<ArgumentNullException>(() => TemplateTelemetry.Configure(null!, endpoint: null));

        foreach (var endpoint in new Uri?[] { null, new Uri("https://otel.example.test:4318/v1/traces") })
        {
            var services = new ServiceCollection();
            TemplateTelemetry.Configure(services, endpoint);
            using var provider = services.BuildServiceProvider();
            Assert.NotNull(provider.GetRequiredService<TracerProvider>());
        }
    }

    [Fact]
    public async Task Sample_work_executor_and_producer_validate_inputs_and_use_the_registered_typed_contract()
    {
        var executor = new SampleWorkExecutor();
        Assert.Throws<ArgumentNullException>(() => executor.ExecuteAsync(null!));

        var envelope = new DurableWorkerEnvelope<SampleWork>(
            DurableWorkerProjectionOutcome.Claimed,
            "sample-claimed",
            DurableWorkerRetryability.Retryable,
            new DurableWorkerCorrelation("sample-worker", "sample-work", "sample-instance", "attempt-1"),
            new SampleWork("hello"));
        Assert.Equal("processed:hello", (await executor.ExecuteAsync(envelope)).Value);

        var missingPayload = new DurableWorkerEnvelope<SampleWork>(
            DurableWorkerProjectionOutcome.Claimed,
            "sample-claimed",
            DurableWorkerRetryability.Retryable,
            new DurableWorkerCorrelation("sample-worker", "sample-work", "sample-instance", "attempt-1"));
        var missingPayloadException = Assert.Throws<InvalidOperationException>(() =>
        {
            _ = executor.ExecuteAsync(missingPayload).GetAwaiter().GetResult();
        });
        Assert.Contains("payload is absent", missingPayloadException.Message, StringComparison.Ordinal);

        using var canceledExecution = new CancellationTokenSource();
        canceledExecution.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await executor.ExecuteAsync(envelope, canceledExecution.Token));

        var nullClient = Assert.Throws<ArgumentNullException>(() => new SampleWorkProducer(null!));
        Assert.Equal("client", nullClient.ParamName);

        var fakeClient = new RecordingWorkClient();
        var producer = new SampleWorkProducer(fakeClient);
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = producer.AcceptAsync(null!, new DurableCommandId("sample-command"), "sample-key");
        });
        Assert.Throws<ArgumentException>(() => producer.AcceptAsync(
            new SampleWork("hello"), new DurableCommandId("sample-command"), " ").GetAwaiter().GetResult());
        Assert.Throws<ArgumentException>(() => producer.AcceptAsync(
            new SampleWork(" "), new DurableCommandId("sample-command"), "sample-key").GetAwaiter().GetResult());

        using var cancellation = new CancellationTokenSource();
        var commandId = new DurableCommandId("sample-command");
        var expectedAcceptance = new DurableWorkAcceptance(
            new DurableWorkId("sample-work-id"),
            commandId,
            DurableWorkAcceptanceKind.Accepted,
            1,
            DateTimeOffset.Parse("2026-01-02T03:04:05Z", System.Globalization.CultureInfo.InvariantCulture));
        fakeClient.Result = DurableOperationResult<DurableWorkAcceptance>.Success(expectedAcceptance);
        var accepted = await producer.AcceptAsync(new SampleWork("hello"), commandId, "sample-key", cancellation.Token);

        Assert.Same(fakeClient.Result, accepted);
        Assert.Equal(cancellation.Token, fakeClient.CancellationToken);
        Assert.Equal(SampleWorkDefinition.WorkName, fakeClient.Request!.WorkName);
        Assert.Equal(SampleWorkDefinition.WorkVersion, fakeClient.Request.WorkVersion);
        Assert.Equal(SampleWorkDefinition.Scope, fakeClient.Request.ScopeId.Value);
        Assert.Equal(commandId, fakeClient.Request.CommandId);
        Assert.Equal("sample-key", fakeClient.Request.IdempotencyKey);
        Assert.Equal("hello", SampleWorkDefinition.Definition.WorkCodec.Decode(fakeClient.Request.Payload).Value);
    }

    [Fact]
    public void Sample_work_codecs_enforce_application_owned_input_and_result_contracts()
    {
        var workCodec = SampleWorkDefinition.CreateWorkCodec();
        var resultCodec = SampleWorkDefinition.CreateResultCodec();

        Assert.Equal("hello", workCodec.Decode(workCodec.Encode(new SampleWork("hello"))).Value);
        Assert.Equal("done", resultCodec.Decode(resultCodec.Encode(new SampleWorkResult("done"))).Value);
        Assert.Throws<ArgumentException>(() => workCodec.Encode(new SampleWork(" ")));
        Assert.Throws<ArgumentException>(() => workCodec.Encode(new SampleWork(new string('x', 201))));
        Assert.Throws<ArgumentException>(() => resultCodec.Encode(new SampleWorkResult(" ")));
        Assert.Throws<ArgumentException>(() => resultCodec.Encode(new SampleWorkResult(new string('x', 221))));
    }

    [Fact]
    public async Task Data_source_factory_reports_safe_failure_when_runtime_source_cannot_be_created()
    {
        var settings = new WorkerHostSettings(
            "Host=127.0.0.1;Database=app;Username=dispatcher;Password=unused",
            $"Host=127.0.0.1;Database=app;Username=runtime;Password=unused;UnknownOption={SensitiveMarker}",
            Guid.Parse("a8bfbc5e-5660-4bd0-9bd6-83e31d2ec329"),
            Guid.Parse("47f80193-0e02-4ea2-9857-4b6a70a62257"),
            "runtime",
            10,
            5,
            5,
            TestActivationSettings(),
            ValidToken,
            null)
        {
            IsDevelopment = true,
        };

        var exception = Assert.Throws<InvalidOperationException>(() => WorkerDataSources.Create(settings));
        Assert.DoesNotContain(SensitiveMarker, exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Data_source_disposal_attempts_both_sources_and_preserves_password_provider_failures(
        bool dispatcherFails,
        bool runtimeFails)
    {
        var dispatcherFailure = new InvalidOperationException("dispatcher password-provider cancellation failed");
        var runtimeFailure = new InvalidOperationException("runtime password-provider cancellation failed");
        var (dispatcher, dispatcherRegistration) = await CreateDisposalTestSourceAsync("dispatcher", dispatcherFails ? dispatcherFailure : null);
        var (runtime, runtimeRegistration) = await CreateDisposalTestSourceAsync("runtime", runtimeFails ? runtimeFailure : null);
        using var dispatcherCallback = dispatcherRegistration;
        using var runtimeCallback = runtimeRegistration;
        var sources = new WorkerDataSources(dispatcher, runtime, "runtime");

        var exception = await Assert.ThrowsAsync<AggregateException>(async () => await sources.DisposeAsync());

        if (dispatcherFails && runtimeFails)
        {
            Assert.Contains("Both restricted PostgreSQL data sources failed", exception.Message, StringComparison.Ordinal);
            Assert.Collection(exception.InnerExceptions,
                failure => Assert.Same(dispatcherFailure, Assert.IsType<AggregateException>(failure).InnerException),
                failure => Assert.Same(runtimeFailure, Assert.IsType<AggregateException>(failure).InnerException));
        }
        else
        {
            Assert.Same(dispatcherFails ? dispatcherFailure : runtimeFailure, exception.InnerException);
        }

        await AssertDataSourcesDisposedAsync(sources);
        await sources.DisposeAsync();
    }

    private static async Task<(NpgsqlDataSource Source, CancellationTokenRegistration Registration)> CreateDisposalTestSourceAsync(
        string role,
        Exception? disposalFailure)
    {
        var builder = new NpgsqlDataSourceBuilder($"Host=127.0.0.1;Database=app;Username={role}");
        if (disposalFailure is null)
        {
            return (builder.Build(), default);
        }

        // Npgsql cancels this supported provider token when disposing the source.
        // A failing credential-provider cleanup must not prevent the other pool from being disposed.
        var ready = new TaskCompletionSource<CancellationTokenRegistration>(TaskCreationOptions.RunContinuationsAsynchronously);
        builder.UsePeriodicPasswordProvider((_, cancellationToken) =>
        {
            ready.TrySetResult(cancellationToken.Register(() => throw disposalFailure));
            return ValueTask.FromResult("unused");
        }, TimeSpan.FromDays(1), TimeSpan.FromDays(1));
        var source = builder.Build();
        var registration = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        return (source, registration);
    }

    private static WorkerHostSettings ReadSettings(
        Dictionary<string, string?>? overrides = null,
        string environmentName = "Development")
    {
        var builder = CreateStartupBuilder(environmentName, overrides);
        return WorkerHostSettings.Read(builder.Configuration, builder.Environment);
    }

    private sealed class DevelopmentTokenReloadSource : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) => new DevelopmentTokenReloadProvider();
    }

    private sealed class DevelopmentTokenReloadProvider : ConfigurationProvider
    {
        private int _tokenReads;

        public override bool TryGet(string key, out string? value)
        {
            if (!string.Equals(key, WorkerApplication.DevelopmentTokenKey, StringComparison.Ordinal))
            {
                return base.TryGet(key, out value);
            }

            // Model a provider reload between the settings snapshot and authentication composition.
            value = Interlocked.Increment(ref _tokenReads) == 1 ? null : SensitiveMarker;
            return true;
        }
    }

    private static WebApplicationBuilder CreateStartupBuilder(
        string environmentName,
        Dictionary<string, string?>? overrides = null)
    {
        var builder = CreateBareTestBuilder(environmentName);
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Durable:DispatcherConnectionString"] = "Host=127.0.0.1;Database=app;Username=dispatcher;Password=unused",
            ["Durable:RuntimeConnectionString"] = "Host=127.0.0.1;Database=app;Username=runtime;Password=unused",
            ["Durable:StoreId"] = "a8bfbc5e-5660-4bd0-9bd6-83e31d2ec329",
            ["Durable:RuntimeEpoch"] = "47f80193-0e02-4ea2-9857-4b6a70a62257",
            [WorkerApplication.DevelopmentTokenKey] = environmentName == Environments.Development ? "local-test-token" : null,
        };
        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
            {
                values[key] = value;
            }
        }

        builder.Configuration.AddInMemoryCollection(values);
        return builder;
    }

    private static WebApplicationBuilder CreateBareTestBuilder(string environmentName = "Development")
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environmentName,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.UseTestServer();
        return builder;
    }

    private static ServiceCollection CreateAuthenticationServices(
        bool addPolicy = true,
        bool requireAuthenticatedUser = true,
        string? selectedScheme = null,
        bool defaultSchemes = true,
        bool addSecondScheme = false,
        bool addSelectedScheme = false)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var authentication = services.AddAuthentication(options =>
        {
            if (defaultSchemes)
            {
                options.DefaultAuthenticateScheme = DevelopmentBearerHandler.SchemeName;
                options.DefaultChallengeScheme = DevelopmentBearerHandler.SchemeName;
            }
        });
        authentication.AddScheme<DevelopmentBearerOptions, DevelopmentBearerHandler>(
            DevelopmentBearerHandler.SchemeName,
            options => options.Token = ValidToken);
        if (addSecondScheme)
        {
            authentication.AddScheme<DevelopmentBearerOptions, DevelopmentBearerHandler>(
                "second-contract-test",
                options => options.Token = ValidToken);
        }

        if (addSelectedScheme)
        {
            authentication.AddScheme<DevelopmentBearerOptions, DevelopmentBearerHandler>(
                "contract-test",
                options => options.Token = ValidToken);
        }

        services.AddAuthorization(options =>
        {
            if (addPolicy)
            {
                options.AddPolicy(WorkerApplication.ActivationAuthorizationPolicy, policy =>
                {
                    if (requireAuthenticatedUser)
                    {
                        policy.RequireAuthenticatedUser();
                    }
                    else
                    {
                        policy.RequireClaim("permission", "durable-activation");
                    }

                    if (selectedScheme is not null)
                    {
                        policy.AddAuthenticationSchemes(selectedScheme);
                    }
                });
            }
        });
        return services;
    }

    private static DurableRuntimeSchemaStatus CompatibleSchemaStatus(
        DurableRuntimeSchemaCompatibility compatibility = DurableRuntimeSchemaCompatibility.Compatible,
        Guid? storeId = null,
        Guid? activeRuntimeEpoch = null) => new(
        compatibility,
        storeId ?? Guid.Parse("a8bfbc5e-5660-4bd0-9bd6-83e31d2ec329"),
        activeRuntimeEpoch ?? Guid.Parse("47f80193-0e02-4ea2-9857-4b6a70a62257"),
        1,
        1,
        1,
        1,
        1,
        1,
        [1],
        [],
        null);

    private static async Task AssertDataSourcesDisposedAsync(WorkerDataSources? sources)
    {
        Assert.NotNull(sources);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            await using var _ = await sources.Dispatcher.OpenConnectionAsync();
        });
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            await using var _ = await sources.Runtime.OpenConnectionAsync();
        });
    }

    private static ActivationSettings TestActivationSettings(
        TimeSpan? bodyReadBudget = null) => new(
            17,
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(9),
            bodyReadBudget ?? TimeSpan.FromSeconds(1));

    private static DefaultHttpContext CreateEndpointContext(
        IDurableExternalActivationService activationService,
        long? contentLength)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(activationService);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
        };
        context.Request.ContentLength = contentLength;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static DefaultHttpContext CreateProbeContext(IDurableRuntimeHealth health)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(health);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        return new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            Response = { Body = new MemoryStream() },
        };
    }

    private static async Task<int> ExecuteAsync(IResult result, HttpContext context)
    {
        await result.ExecuteAsync(context);
        if (context.Response.Body.CanSeek)
        {
            context.Response.Body.Position = 0;
        }

        return context.Response.StatusCode;
    }

    private static async Task<string?> ReadErrorAsync(Stream body)
    {
        if (body.CanSeek)
        {
            body.Position = 0;
        }

        using var document = await JsonDocument.ParseAsync(body);
        return document.RootElement.GetProperty("error").GetString();
    }

    private static DurableExternalActivationResult CompletedResult() => ActivationResult(
        DurableExternalActivationOutcomeKind.Completed,
        DurableRuntimeHealthState.Healthy,
        problemCode: null,
        new DurableRuntimePumpResult(1, 1, 1, 0, 0, false, null, TimeSpan.FromMilliseconds(1)));

    private static DurableExternalActivationResult ActivationResult(
        DurableExternalActivationOutcomeKind kind,
        DurableRuntimeHealthState? observedHealthState,
        string? problemCode,
        DurableRuntimePumpResult? pumpResult = null) => new(
            kind,
            observedHealthState,
            problemCode,
            pumpResult);

    private static DurableRuntimeHealthSnapshot HealthSnapshot(
        DurableRuntimeHealthState state,
        string? problemCode = null,
        bool schemaCompatible = true,
        bool epochCompatible = true,
        bool isDraining = false)
    {
        var now = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        return new DurableRuntimeHealthSnapshot(
            state,
            problemCode,
            schemaCompatible,
            epochCompatible,
            4,
            4,
            Guid.Parse("47f80193-0e02-4ea2-9857-4b6a70a62257"),
            epochCompatible
                ? Guid.Parse("47f80193-0e02-4ea2-9857-4b6a70a62257")
                : Guid.NewGuid(),
            "template-contract-worker",
            Guid.Parse("8fcd0f2a-fefc-44fb-bcc1-0e82c5af0b3f"),
            DurableRuntimeSurface.Work,
            now,
            now.AddMinutes(-1),
            now,
            now,
            isDraining,
            isPassActive: false,
            dueDispatchCount: 0,
            oldestDueAtUtc: null,
            oldestDueAge: null);
    }

    private static async Task AssertProbeProjectionAsync(
        HttpResponseMessage response,
        DurableRuntimeHealthSnapshot expected,
        int expectedStatus)
    {
        Assert.Equal(expectedStatus, (int)response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        Assert.Equal(
            ["canEnableActivation", "isReady", "observedHealthState", "outcome", "problemCode"],
            document.RootElement.EnumerateObject().Select(static property => property.Name).OrderBy(static name => name, StringComparer.Ordinal));
        Assert.Equal("Assessment", document.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(expected.State.ToString(), document.RootElement.GetProperty("observedHealthState").GetString());
        Assert.Equal(expected.CanEnableActivation, document.RootElement.GetProperty("canEnableActivation").GetBoolean());
        Assert.Equal(expected.IsReady, document.RootElement.GetProperty("isReady").GetBoolean());
        Assert.Equal(expected.ProblemCode, document.RootElement.GetProperty("problemCode").GetString());
    }

    private static async Task<HttpTestServer> CreateHttpServerAsync(
        RecordingActivationService? activation = null,
        MutableRuntimeHealth? health = null)
    {
        var builder = CreateBareTestBuilder();
        builder.Services.AddLogging();
        builder.Services
            .AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = DevelopmentBearerHandler.SchemeName;
                options.DefaultChallengeScheme = DevelopmentBearerHandler.SchemeName;
                options.DefaultForbidScheme = DevelopmentBearerHandler.SchemeName;
            })
            .AddScheme<DevelopmentBearerOptions, DevelopmentBearerHandler>(
                DevelopmentBearerHandler.SchemeName,
                options => options.Token = ValidToken);
        builder.Services.AddAuthorization(options => options.AddPolicy(
            WorkerApplication.ActivationAuthorizationPolicy,
            policy => policy.RequireAuthenticatedUser().RequireClaim("permission", "durable-activation")));

        var state = new HttpTestState();
        activation ??= new RecordingActivationService(CompletedResult());
        health ??= new MutableRuntimeHealth(HealthSnapshot(DurableRuntimeHealthState.Healthy));
        builder.Services.AddSingleton<IDurableExternalActivationService>(new CountingActivationService(activation, state));
        builder.Services.AddSingleton<IDurableRuntimeHealth>(health);

        var app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.Equals(ActivationPath, StringComparison.Ordinal))
            {
                context.Request.Body = new TrackingReadStream(
                    [],
                    () => Interlocked.Increment(ref state.BodyReads));
                context.Request.ContentLength = null;
                if (context.Request.Headers.ContainsKey("X-Test-No-Permission"))
                {
                    foreach (var identity in context.User.Identities)
                    {
                        foreach (var claim in identity.FindAll("permission").ToArray())
                        {
                            identity.RemoveClaim(claim);
                        }
                    }
                }
            }

            await next();
        });
        app.UseAuthorization();
        ActivationEndpoints.Map(app, TestActivationSettings());
        await app.StartAsync();
        return new HttpTestServer(app, app.GetTestClient(), state);
    }

    private sealed class HttpTestServer(WebApplication app, HttpClient client, HttpTestState state) : IAsyncDisposable
    {
        internal HttpClient Client { get; } = client;

        internal HttpTestState State { get; } = state;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
        }
    }

    private sealed class HttpTestState
    {
        internal int BodyReads;

        internal int ActivationCalls;
    }

    private sealed class CountingActivationService(
        IDurableExternalActivationService inner,
        HttpTestState state) : IDurableExternalActivationService
    {
        public ValueTask<DurableExternalActivationResult> ActivateAsync(
            DurableExternalActivationRequest request,
            CancellationToken callerCancellation = default)
        {
            Interlocked.Increment(ref state.ActivationCalls);
            return inner.ActivateAsync(request, callerCancellation);
        }
    }

    private sealed class RecordingWorkClient : IDurableWorkClient
    {
        internal DurableOperationResult<DurableWorkAcceptance> Result { get; set; } = null!;

        internal DurableWorkRequest? Request { get; private set; }

        internal CancellationToken CancellationToken { get; private set; }

        public ValueTask<DurableOperationResult<DurableWorkAcceptance>> EnqueueAsync(
            DurableWorkRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            CancellationToken = cancellationToken;
            return ValueTask.FromResult(Result);
        }
    }

    private sealed class RecordingActivationService(
        DurableExternalActivationResult? result = null,
        Exception? exception = null,
        CancellationToken cancellationToken = default) : IDurableExternalActivationService
    {
        private int _calls;

        internal int Calls => Volatile.Read(ref _calls);

        internal DurableExternalActivationRequest? LastRequest { get; private set; }

        public ValueTask<DurableExternalActivationResult> ActivateAsync(
            DurableExternalActivationRequest request,
            CancellationToken callerCancellation = default)
        {
            Interlocked.Increment(ref _calls);
            LastRequest = request;
            if (exception is not null)
            {
                return ValueTask.FromException<DurableExternalActivationResult>(exception);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromCanceled<DurableExternalActivationResult>(cancellationToken);
            }

            return ValueTask.FromResult(result ?? CompletedResult());
        }
    }

    private sealed class MutableRuntimeHealth(
        DurableRuntimeHealthSnapshot snapshot,
        Exception? exception = null,
        CancellationToken configuredCancellationToken = default) : IDurableRuntimeHealth
    {
        private int _calls;

        internal DurableRuntimeHealthSnapshot Snapshot { get; set; } = snapshot;

        internal int Calls => Volatile.Read(ref _calls);

        public ValueTask<DurableRuntimeHealthSnapshot> GetAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            if (exception is not null)
            {
                return ValueTask.FromException<DurableRuntimeHealthSnapshot>(exception);
            }

            if (configuredCancellationToken.IsCancellationRequested || cancellationToken.IsCancellationRequested)
            {
                var token = configuredCancellationToken.IsCancellationRequested ? configuredCancellationToken : cancellationToken;
                return ValueTask.FromCanceled<DurableRuntimeHealthSnapshot>(token);
            }

            return ValueTask.FromResult(Snapshot);
        }
    }

    private sealed class BlockingStartupService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private class TrackingReadStream : Stream
    {
        private readonly byte[] _bytes;
        private readonly Func<Memory<byte>, CancellationToken, ValueTask<int>>? _reader;
        private readonly Action? _onRead;
        private readonly TaskCompletionSource _readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _position;
        private int _readCalls;

        internal TrackingReadStream(byte[] bytes, Action? onRead = null)
        {
            _bytes = bytes;
            _onRead = onRead;
        }

        internal TrackingReadStream(Func<Memory<byte>, CancellationToken, ValueTask<int>> reader)
        {
            _bytes = [];
            _reader = reader;
        }

        internal int ReadCalls => Volatile.Read(ref _readCalls);

        internal Task ReadStarted => _readStarted.Task;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            OnRead();
            if (_reader is not null)
            {
                throw new NotSupportedException("This test stream supports asynchronous reads only.");
            }

            var copied = Math.Min(buffer.Length, _bytes.Length - _position);
            _bytes.AsSpan(_position, copied).CopyTo(buffer);
            _position += copied;
            return copied;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            OnRead();
            cancellationToken.ThrowIfCancellationRequested();
            if (_reader is not null)
            {
                return _reader(buffer, cancellationToken);
            }

            var copied = Math.Min(buffer.Length, _bytes.Length - _position);
            _bytes.AsMemory(_position, copied).CopyTo(buffer);
            _position += copied;
            return ValueTask.FromResult(copied);
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private void OnRead()
        {
            Interlocked.Increment(ref _readCalls);
            _readStarted.TrySetResult();
            _onRead?.Invoke();
        }
    }

    private sealed class BlockingReadStream() : TrackingReadStream(
        static async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        });

    private sealed class FaultingReadStream(Exception exception) : TrackingReadStream(
        (_, _) => ValueTask.FromException<int>(exception));
}
