using System.Diagnostics;
using AppSurfaceDurableWorker.Hosting;
using AppSurfaceDurableWorker.Work;
using ForgeTrust.AppSurface.Durable.PostgreSql;
using ForgeTrust.AppSurface.Durable.Provider;
using ForgeTrust.AppSurface.Observability;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AppSurfaceDurableWorker;

/// <summary>Builds and starts the passive, application-owned Durable external-activation host.</summary>
/// <remarks>
/// Call <see cref="CreateBuilder(string[])"/>, configure a production authentication scheme and the named
/// <c>ActivationAuthorization</c> policy when outside Development, then call <see cref="BuildAsync(WebApplicationBuilder, CancellationToken)"/>
/// followed by <see cref="StartAsync(WebApplication, CancellationToken)"/>. Build validates authentication before
/// opening a database connection; startup then performs read-only schema, StoreId, and epoch checks before listening.
/// The returned application owns the restricted data sources and OpenTelemetry provider and must be disposed by its
/// caller. No worker service, migration, grant, or epoch mutation is started here.
/// </remarks>
public static class WorkerApplication
{
    /// <summary>The authorization policy required by every private activation request.</summary>
    public const string ActivationAuthorizationPolicy = "ActivationAuthorization";

    /// <summary>The Development-only configuration key for the sample bearer token.</summary>
    public const string DevelopmentTokenKey = "APPSURFACE_TEMPLATE_ACTIVATION_TOKEN";

    /// <summary>Creates a web builder rooted at the generated settings copy with loopback-only listener defaults and DI validation.</summary>
    /// <param name="args">Command-line arguments consumed by ASP.NET Core configuration.</param>
    /// <returns>A builder that the application may configure before calling <see cref="BuildAsync(WebApplicationBuilder, CancellationToken)"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is null.</exception>
    public static WebApplicationBuilder CreateBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://127.0.0.1:5080");
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateOnBuild = true;
            options.ValidateScopes = true;
        });
        return builder;
    }

    /// <summary>Validates composition, performs bounded read-only PostgreSQL startup checks, and returns an unstarted host.</summary>
    /// <param name="builder">Application-owned web builder, including any production authentication registrations.</param>
    /// <param name="cancellationToken">Cancels the complete ten-second startup phase.</param>
    /// <returns>A passive application that has not opened its listener.</returns>
    /// <remarks>
    /// In Development this method registers the sample bearer scheme and named policy from the required
    /// <see cref="DevelopmentTokenKey"/> configuration value. In other environments the caller must register a real
    /// application-selected scheme and a named policy that requires an authenticated user. Policy and scheme
    /// validation runs before any PostgreSQL connection is opened. Store validation is read-only; a missing or
    /// incompatible schema, configured StoreId mismatch, or epoch mismatch fails closed.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Settings or authentication composition is invalid, or storage is incompatible.</exception>
    /// <exception cref="TimeoutException">The combined build and startup validation phase exceeds ten seconds.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    public static Task<WebApplication> BuildAsync(
        WebApplicationBuilder builder,
        CancellationToken cancellationToken = default) =>
        BuildAsyncCoreAsync(
            builder,
            WorkerDataSources.Create,
            static (dataSources, token) => new PostgreSqlDurableRuntimeSchemaManager(dataSources.Runtime)
                .GetStatusAsync(token),
            TimeSpan.FromSeconds(10),
            cancellationToken);

    /// <summary>Builds through explicit startup seams for friend-assembly host contract tests.</summary>
    /// <param name="builder">Application-owned builder.</param>
    /// <param name="dataSourceFactory">Creates the pair of restricted data sources after authentication validation.</param>
    /// <param name="schemaStatusReader">Returns the read-only schema observation for the runtime source.</param>
    /// <param name="startupBudget">Positive bounded budget for the test startup lifecycle.</param>
    /// <param name="cancellationToken">Cancels the startup lifecycle.</param>
    /// <returns>A built but unstarted application when the configured observation is compatible.</returns>
    /// <remarks>This seam is internal so installed application consumers continue to use the production PostgreSQL reader.</remarks>
    internal static Task<WebApplication> BuildWithStartupSeamsAsync(
        WebApplicationBuilder builder,
        Func<WorkerHostSettings, WorkerDataSources> dataSourceFactory,
        Func<WorkerDataSources, CancellationToken, ValueTask<DurableRuntimeSchemaStatus>> schemaStatusReader,
        TimeSpan startupBudget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(dataSourceFactory);
        ArgumentNullException.ThrowIfNull(schemaStatusReader);
        if (startupBudget <= TimeSpan.Zero || startupBudget > TimeSpan.FromMilliseconds(uint.MaxValue - 1))
        {
            throw new ArgumentOutOfRangeException(nameof(startupBudget));
        }

        return BuildAsyncCoreAsync(builder, dataSourceFactory, schemaStatusReader, startupBudget, cancellationToken);
    }

    private static async Task<WebApplication> BuildAsyncCoreAsync(
        WebApplicationBuilder builder,
        Func<WorkerHostSettings, WorkerDataSources> dataSourceFactory,
        Func<WorkerDataSources, CancellationToken, ValueTask<DurableRuntimeSchemaStatus>> schemaStatusReader,
        TimeSpan startupBudget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(dataSourceFactory);
        ArgumentNullException.ThrowIfNull(schemaStatusReader);
        var startupDeadline = new WorkerStartupDeadline(Stopwatch.GetTimestamp(), startupBudget);
        using var startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startupCancellation.CancelAfter(startupDeadline.Budget);

        var settings = WorkerHostSettings.Read(builder.Configuration, builder.Environment);
        ConfigureAuthentication(builder, settings.DevelopmentToken);
        ValidateAuthenticationComposition(builder.Services);

        WorkerDataSources? dataSources = null;
        WebApplication? app = null;
        try
        {
            dataSources = dataSourceFactory(settings);
            var schemaStatus = await schemaStatusReader(dataSources, startupCancellation.Token).ConfigureAwait(false);
            ValidateSchemaIdentity(schemaStatus, settings);
            startupDeadline.ThrowIfExpired();

            builder.Services.AddSingleton(startupDeadline);
            builder.Services.AddSingleton<WorkerDataSources>(_ => dataSources);
            _ = builder.Services.AddSampleWork();
            _ = builder.Services.AddAppSurfaceDurablePostgreSql(
                dataSources.Dispatcher,
                dataSources.Runtime,
                new PostgreSqlDurableWorkOptions(settings.RuntimeEpoch, settings.StoreId),
                new PostgreSqlDurableScheduleOptions(dataSources.RuntimeRole));
            builder.Services.AddDurableExternalActivation();
            TemplateTelemetry.Configure(builder.Services, settings.OtlpEndpoint);

            app = builder.Build();
            _ = app.Services.GetRequiredService<WorkerDataSources>();
            using (var scope = app.Services.CreateScope())
            {
                _ = scope.ServiceProvider.GetRequiredService<IDurableExternalActivationService>();
            }

            app.UseAuthentication();
            app.UseAuthorization();
            ActivationEndpoints.Map(app, settings.Activation);
            startupDeadline.ThrowIfExpired();
            return app;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            await DisposeFailedBuildAsync(app, dataSources).ConfigureAwait(false);
            throw new TimeoutException(
                "Durable host startup exceeded its configured budget. Check PostgreSQL reachability and the configured read-only startup identity.",
                exception);
        }
        catch
        {
            await DisposeFailedBuildAsync(app, dataSources).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Starts the listener using the remaining portion of the same ten-second startup budget.</summary>
    /// <param name="app">Built host returned by <see cref="BuildAsync(WebApplicationBuilder, CancellationToken)"/>.</param>
    /// <param name="cancellationToken">Cancels listener startup.</param>
    /// <returns>A task that completes when the HTTP listener is active.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is null.</exception>
    /// <exception cref="TimeoutException">The remaining ten-second startup budget expires before the listener opens.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    public static async Task StartAsync(WebApplication app, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        var deadline = app.Services.GetRequiredService<WorkerStartupDeadline>();
        var remaining = deadline.GetRemaining();
        if (remaining <= TimeSpan.Zero)
        {
            throw new TimeoutException("Durable host startup exceeded its configured budget before the listener opened.");
        }

        using var startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startupCancellation.CancelAfter(remaining);
        try
        {
            await app.StartAsync(startupCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Durable host startup exceeded its configured budget while opening the listener.", exception);
        }
    }

    /// <summary>Validates the named authenticated policy and every scheme it can select before database access.</summary>
    /// <param name="services">Builder registrations containing application-selected authentication and authorization.</param>
    /// <remarks>
    /// This intentional short-lived provider resolves only framework auth/authorization metadata. The registered
    /// application host is built once after validation; no PostgreSQL source is present in this validation provider.
    /// </remarks>
    internal static void ValidateAuthenticationComposition(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

#pragma warning disable ASP0000 // Resolve policy/scheme metadata before opening the database; this provider is immediately disposed.
        using var provider = services.BuildServiceProvider();
#pragma warning restore ASP0000
        var policyProvider = provider.GetService<IAuthorizationPolicyProvider>()
            ?? throw new InvalidOperationException(
                $"Register authorization services and the named {ActivationAuthorizationPolicy} policy before host startup.");
        var schemeProvider = provider.GetService<IAuthenticationSchemeProvider>()
            ?? throw new InvalidOperationException(
                "Register the application's authentication scheme before host startup.");

        var policy = policyProvider.GetPolicyAsync(ActivationAuthorizationPolicy).GetAwaiter().GetResult()
            ?? throw new InvalidOperationException(
                $"The named {ActivationAuthorizationPolicy} authorization policy is missing.");
        if (!policy.Requirements.Any(static requirement => requirement is DenyAnonymousAuthorizationRequirement))
        {
            throw new InvalidOperationException(
                $"The {ActivationAuthorizationPolicy} policy must require an authenticated user.");
        }

        if (policy.AuthenticationSchemes.Count > 0)
        {
            foreach (var schemeName in policy.AuthenticationSchemes)
            {
                if (schemeProvider.GetSchemeAsync(schemeName).GetAwaiter().GetResult() is null)
                {
                    throw new InvalidOperationException(
                        $"The {ActivationAuthorizationPolicy} policy selects an unregistered authentication scheme.");
                }
            }

            return;
        }

        if (schemeProvider.GetDefaultAuthenticateSchemeAsync().GetAwaiter().GetResult() is null
            || schemeProvider.GetDefaultChallengeSchemeAsync().GetAwaiter().GetResult() is null)
        {
            throw new InvalidOperationException(
                $"Register default authenticate and challenge schemes, or select registered schemes in {ActivationAuthorizationPolicy}.");
        }
    }

    /// <summary>Maps only the supported Development auth defaults and closed authorization policy.</summary>
    /// <param name="builder">Application web builder.</param>
    /// <param name="developmentToken">Validated Development token, or null outside Development.</param>
    private static void ConfigureAuthentication(WebApplicationBuilder builder, string? developmentToken)
    {
        if (builder.Environment.IsDevelopment())
        {
            builder.Services
                .AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = DevelopmentBearerHandler.SchemeName;
                    options.DefaultChallengeScheme = DevelopmentBearerHandler.SchemeName;
                    options.DefaultForbidScheme = DevelopmentBearerHandler.SchemeName;
                })
                .AddScheme<DevelopmentBearerOptions, DevelopmentBearerHandler>(
                    DevelopmentBearerHandler.SchemeName,
                    options => options.Token = developmentToken!);
            builder.Services.AddAuthorization(options => options.AddPolicy(
                ActivationAuthorizationPolicy,
                policy => policy.RequireAuthenticatedUser().RequireClaim("permission", "durable-activation")));
        }
        else if (builder.Configuration[DevelopmentTokenKey] is not null)
        {
            throw new InvalidOperationException(
                $"{DevelopmentTokenKey} is allowed only in Development and must be unset in every other environment.");
        }
    }

    /// <summary>Checks the read-only schema observation against both explicitly configured process identities.</summary>
    /// <param name="status">Published PostgreSQL schema-manager observation.</param>
    /// <param name="settings">Validated application settings.</param>
    internal static void ValidateSchemaIdentity(DurableRuntimeSchemaStatus status, WorkerHostSettings settings)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(settings);
        if (!status.IsCompatible
            || status.StoreId == Guid.Empty
            || status.StoreId != settings.StoreId
            || status.ActiveRuntimeEpoch != settings.RuntimeEpoch)
        {
            throw new InvalidOperationException(
                "Read-only Durable startup validation found an incompatible schema, StoreId, or active runtime epoch. "
                + "Provision storage out of band, then verify the configured runtime identity.");
        }
    }

    /// <summary>Attempts every resource disposal after an unsuccessful build.</summary>
    /// <param name="app">Built application, when ownership has transferred to its service provider.</param>
    /// <param name="dataSources">Pre-build data-source owner, when the application did not take ownership.</param>
    private static async ValueTask DisposeFailedBuildAsync(WebApplication? app, WorkerDataSources? dataSources)
    {
        if (app is not null)
        {
            await app.DisposeAsync().ConfigureAwait(false);
        }
        else if (dataSources is not null)
        {
            await dataSources.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>Contains validated runtime settings captured before resources or listeners are created.</summary>
/// <param name="DispatcherConnectionString">Restricted payload-free discovery connection.</param>
/// <param name="RuntimeConnectionString">Distinct restricted mutation and heartbeat connection.</param>
/// <param name="StoreId">Expected provisioned StoreId.</param>
/// <param name="RuntimeEpoch">Expected provisioned active runtime epoch.</param>
/// <param name="RuntimeRole">Exact login role used by the runtime safety options.</param>
/// <param name="MaximumPoolSize">Bounded pool size applied to both sources.</param>
/// <param name="ConnectionTimeoutSeconds">Bounded connection timeout applied to both sources.</param>
/// <param name="CommandTimeoutSeconds">Bounded command timeout applied to both sources.</param>
/// <param name="Activation">Validated Work-only pump, request, and body-read budgets.</param>
/// <param name="DevelopmentToken">Development-only bearer token; null outside Development.</param>
/// <param name="OtlpEndpoint">Validated optional OTLP endpoint.</param>
internal sealed record WorkerHostSettings(
    string DispatcherConnectionString,
    string RuntimeConnectionString,
    Guid StoreId,
    Guid RuntimeEpoch,
    string RuntimeRole,
    int MaximumPoolSize,
    int ConnectionTimeoutSeconds,
    int CommandTimeoutSeconds,
    ActivationSettings Activation,
    string? DevelopmentToken,
    Uri? OtlpEndpoint)
{
    /// <summary>Gets whether the settings were read for the Development environment.</summary>
    internal bool IsDevelopment { get; init; }

    /// <summary>Reads all non-secret settings and rejects malformed or out-of-range values without echoing them.</summary>
    /// <param name="configuration">Application configuration.</param>
    /// <param name="environment">Web host environment.</param>
    /// <returns>Immutable settings snapshot.</returns>
    /// <exception cref="InvalidOperationException">A required value is missing or a configured value is outside its contract.</exception>
    internal static WorkerHostSettings Read(IConfiguration configuration, IWebHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var dispatcherConnection = RequireText(configuration, "Durable:DispatcherConnectionString");
        var runtimeConnection = RequireText(configuration, "Durable:RuntimeConnectionString");
        var storeId = RequireGuid(configuration, "Durable:StoreId");
        var runtimeEpoch = RequireGuid(configuration, "Durable:RuntimeEpoch");
        var maximumPoolSize = ReadInteger(configuration, "Durable:MaximumPoolSize", 10, 1, 100);
        var connectionTimeout = ReadInteger(configuration, "Durable:ConnectionTimeoutSeconds", 5, 1, 300);
        var commandTimeout = ReadInteger(configuration, "Durable:CommandTimeoutSeconds", 5, 1, 300);

        var pumpMaximumItems = ReadInteger(configuration, "DurableActivation:PumpMaximumItems", 32, 1, 10_000);
        var discoveryBudgetSeconds = ReadInteger(configuration, "DurableActivation:PumpDiscoveryBudgetSeconds", 2, 1, 300);
        var requestBudgetSeconds = ReadInteger(configuration, "DurableActivation:RequestBudgetSeconds", 10, 1, 4_294_967);
        var bodyReadBudgetSeconds = ReadInteger(configuration, "DurableActivation:BodyReadBudgetSeconds", 5, 1, 300);
        var activation = new ActivationSettings(
            pumpMaximumItems,
            TimeSpan.FromSeconds(discoveryBudgetSeconds),
            TimeSpan.FromSeconds(requestBudgetSeconds),
            TimeSpan.FromSeconds(bodyReadBudgetSeconds));

        var development = environment.IsDevelopment();
        var developmentToken = configuration[WorkerApplication.DevelopmentTokenKey];
        if (development)
        {
            if (string.IsNullOrWhiteSpace(developmentToken)
                || System.Text.Encoding.UTF8.GetByteCount(developmentToken) > 256)
            {
                throw new InvalidOperationException(
                    $"{WorkerApplication.DevelopmentTokenKey} must contain 1 to 256 UTF-8 bytes in Development.");
            }
        }
        else if (developmentToken is not null)
        {
            throw new InvalidOperationException(
                $"{WorkerApplication.DevelopmentTokenKey} must be unset outside Development.");
        }

        var runtimeRole = ParseRuntimeRole(runtimeConnection);
        if (string.Equals(ParseRuntimeRole(dispatcherConnection), runtimeRole, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Durable:DispatcherConnectionString and Durable:RuntimeConnectionString must use distinct restricted roles.");
        }

        var otlpEndpointText = configuration["OpenTelemetry:OtlpEndpoint"];
        Uri? otlpEndpoint = null;
        if (!string.IsNullOrWhiteSpace(otlpEndpointText)
            && (!Uri.TryCreate(otlpEndpointText, UriKind.Absolute, out otlpEndpoint)
                || otlpEndpoint.Scheme is not ("http" or "https")))
        {
            throw new InvalidOperationException(
                "OpenTelemetry:OtlpEndpoint must be empty or an absolute HTTP(S) endpoint.");
        }

        return new WorkerHostSettings(
            dispatcherConnection,
            runtimeConnection,
            storeId,
            runtimeEpoch,
            runtimeRole,
            maximumPoolSize,
            connectionTimeout,
            commandTimeout,
            activation,
            development ? developmentToken : null,
            otlpEndpoint)
        {
            IsDevelopment = development,
        };
    }

    /// <summary>Reads a required bounded identity from a connection string without logging the connection text.</summary>
    /// <param name="connectionString">Configured connection string.</param>
    /// <returns>The nonblank login role.</returns>
    private static string ParseRuntimeRole(string connectionString)
    {
        try
        {
            var role = new NpgsqlConnectionStringBuilder(connectionString).Username;
            return string.IsNullOrWhiteSpace(role)
                ? throw new InvalidOperationException("A restricted PostgreSQL login role is required.")
                : role;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "Both Durable PostgreSQL connection settings must be valid and identify restricted login roles.");
        }
    }

    /// <summary>Reads a nonblank configuration value and reports only its key on failure.</summary>
    /// <param name="configuration">Configuration source.</param>
    /// <param name="key">Required setting name.</param>
    /// <returns>The configured value.</returns>
    private static string RequireText(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{key} is required; configure the restricted host identity.");
        }

        return value;
    }

    /// <summary>Reads a nonempty Guid setting.</summary>
    /// <param name="configuration">Configuration source.</param>
    /// <param name="key">Setting name.</param>
    /// <returns>The parsed identity.</returns>
    private static Guid RequireGuid(IConfiguration configuration, string key)
    {
        if (!Guid.TryParse(configuration[key], out var value) || value == Guid.Empty)
        {
            throw new InvalidOperationException($"{key} must be a configured nonempty GUID.");
        }

        return value;
    }

    /// <summary>Reads an invariant integer and enforces its inclusive range.</summary>
    /// <param name="configuration">Configuration source.</param>
    /// <param name="key">Setting name.</param>
    /// <param name="defaultValue">Value used only when the key is absent.</param>
    /// <param name="minimum">Inclusive minimum.</param>
    /// <param name="maximum">Inclusive maximum.</param>
    /// <returns>The validated value.</returns>
    private static int ReadInteger(
        IConfiguration configuration,
        string key,
        int defaultValue,
        int minimum,
        int maximum)
    {
        var raw = configuration[key];
        if (raw is null)
        {
            return defaultValue;
        }

        if (!int.TryParse(raw, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value)
            || value < minimum
            || value > maximum)
        {
            throw new InvalidOperationException($"{key} must be an integer from {minimum} through {maximum}.");
        }

        return value;
    }
}

/// <summary>Captures the host-only numeric contracts for a payload-free activation and its ingress body phase.</summary>
/// <param name="PumpMaximumItems">Configured maximum claims, 1–10,000.</param>
/// <param name="PumpDiscoveryBudget">Discovery duration, 1–300 seconds.</param>
/// <param name="RequestBudget">Cooperative service duration, 1–4,294,967 seconds.</param>
/// <param name="BodyReadBudget">Independent unknown-length body deadline, 1–300 seconds.</param>
internal sealed record ActivationSettings(
    int PumpMaximumItems,
    TimeSpan PumpDiscoveryBudget,
    TimeSpan RequestBudget,
    TimeSpan BodyReadBudget);

/// <summary>Owns one shared monotonic deadline for configuration, validation, and listener startup.</summary>
/// <param name="StartedAtTimestamp">Monotonic timestamp captured at build entry.</param>
/// <param name="Budget">The fixed total startup/listener budget.</param>
internal sealed record WorkerStartupDeadline(long StartedAtTimestamp, TimeSpan Budget)
{
    /// <summary>Gets the remaining startup time without consulting wall-clock time.</summary>
    /// <returns>Zero after the fixed budget expires.</returns>
    internal TimeSpan GetRemaining()
    {
        var remaining = Budget - Stopwatch.GetElapsedTime(StartedAtTimestamp);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>Throws a stable timeout when synchronous composition exhausts the shared budget.</summary>
    internal void ThrowIfExpired()
    {
        if (GetRemaining() <= TimeSpan.Zero)
        {
            throw new TimeoutException("Durable host startup exceeded its configured budget.");
        }
    }
}

/// <summary>Owns distinct dispatcher and runtime sources and disposes both even if one disposal fails.</summary>
/// <param name="Dispatcher">Payload-free global discovery source.</param>
/// <param name="Runtime">Restricted mutation and heartbeat source.</param>
/// <param name="RuntimeRole">Exact configured runtime login role.</param>
internal sealed record WorkerDataSources(
    NpgsqlDataSource Dispatcher,
    NpgsqlDataSource Runtime,
    string RuntimeRole) : IAsyncDisposable
{
    /// <summary>Creates distinct bounded data sources only after authentication composition is validated.</summary>
    /// <param name="settings">Validated connection and pool settings.</param>
    /// <returns>Owned source pair.</returns>
    internal static WorkerDataSources Create(WorkerHostSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var dispatcher = CreateDataSource(
            settings.DispatcherConnectionString,
            settings.MaximumPoolSize,
            settings.ConnectionTimeoutSeconds,
            settings.CommandTimeoutSeconds,
            "Durable:DispatcherConnectionString");
        try
        {
            var runtime = CreateDataSource(
                settings.RuntimeConnectionString,
                settings.MaximumPoolSize,
                settings.ConnectionTimeoutSeconds,
                settings.CommandTimeoutSeconds,
                "Durable:RuntimeConnectionString");
            return new WorkerDataSources(dispatcher, runtime, settings.RuntimeRole);
        }
        catch
        {
            dispatcher.Dispose();
            throw;
        }
    }

    /// <summary>Disposes both restricted sources and preserves every disposal failure.</summary>
    public async ValueTask DisposeAsync()
    {
        List<Exception>? failures = null;
        try
        {
            await Dispatcher.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not StackOverflowException
            and not OutOfMemoryException and not AccessViolationException)
        {
            (failures ??= []).Add(exception);
        }

        try
        {
            await Runtime.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not StackOverflowException
            and not OutOfMemoryException and not AccessViolationException)
        {
            (failures ??= []).Add(exception);
        }

        if (failures is { Count: 1 })
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        if (failures is { Count: > 1 })
        {
            throw new AggregateException("Both restricted PostgreSQL data sources failed during disposal.", failures);
        }
    }

    /// <summary>Applies the documented bounded pool and timeout defaults without changing credentials or role identity.</summary>
    /// <param name="connectionString">Restricted application-owned connection string.</param>
    /// <param name="maximumPoolSize">Validated pool size.</param>
    /// <param name="connectionTimeoutSeconds">Validated connection timeout.</param>
    /// <param name="commandTimeoutSeconds">Validated command timeout.</param>
    /// <param name="settingName">Configuration key used for safe failure reporting.</param>
    /// <returns>A newly constructed, unopened data source.</returns>
    private static NpgsqlDataSource CreateDataSource(
        string connectionString,
        int maximumPoolSize,
        int connectionTimeoutSeconds,
        int commandTimeoutSeconds,
        string settingName)
    {
        try
        {
            var options = new NpgsqlConnectionStringBuilder(connectionString)
            {
                MaxPoolSize = maximumPoolSize,
                Timeout = connectionTimeoutSeconds,
                CommandTimeout = commandTimeoutSeconds,
            };
            return NpgsqlDataSource.Create(options.ConnectionString);
        }
        catch (Exception exception) when (exception is not StackOverflowException
            and not OutOfMemoryException and not AccessViolationException)
        {
            throw new InvalidOperationException(
                $"{settingName} could not create its restricted PostgreSQL data source; check its format and role.",
                exception);
        }
    }
}
