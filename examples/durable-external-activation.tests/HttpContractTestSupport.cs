using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using ForgeTrust.AppSurface.Durable.Provider;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Examples.DurableExternalActivation.Tests;

internal sealed class ActivationHostOptions
{
    internal string EnvironmentName { get; init; } = Environments.Development;

    internal string? DevelopmentToken { get; init; } = ActivationTestHost.ValidToken;

    internal bool UseProductionActivationService { get; init; }

    internal bool RegisterHealth { get; init; } = true;

    internal bool RegisterAdmission { get; init; } = true;

    internal bool UseKestrel { get; init; }

    internal IReadOnlyDictionary<string, string?> ConfigurationValues { get; init; } =
        new Dictionary<string, string?>(StringComparer.Ordinal);

    internal Action<IServiceCollection>? ConfigureServices { get; init; }

    internal Action<AuthenticationBuilder>? ConfigureExternalAuthentication { get; init; }

    internal Action<AuthorizationOptions>? ConfigureExternalAuthorization { get; init; }
}

internal sealed class ActivationTestDependencies
{
    internal RecordingRuntimeHealth Health { get; } = new();

    internal RecordingPumpAdmission Admission { get; } = new();

    internal RecordingActivationService Activation { get; } = new();

    internal BodyReadProbe BodyReads { get; } = new();
}

internal sealed class ActivationTestHost : IAsyncDisposable
{
    internal const string ValidToken = "external-activation-test-token";

    private readonly HttpClient client;

    private ActivationTestHost(WebApplication app, HttpClient client, ActivationTestDependencies dependencies)
    {
        App = app;
        this.client = client;
        Dependencies = dependencies;
    }

    internal WebApplication App { get; }

    internal HttpClient Client => client;

    internal ActivationTestDependencies Dependencies { get; }

    internal static WebApplicationBuilder CreateBuilder(ActivationHostOptions options)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = options.EnvironmentName,
        });

        if (options.UseKestrel)
        {
            builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        }
        else
        {
            builder.WebHost.UseTestServer();
        }

        var configurationValues = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["DurableActivation:DevelopmentBearerToken"] = options.DevelopmentToken,
            ["DurableActivation:PumpMaximumItems"] = null,
            ["DurableActivation:PumpDiscoveryBudgetSeconds"] = null,
            ["DurableActivation:RequestBudgetSeconds"] = null,
        };
        foreach (var (key, value) in options.ConfigurationValues)
        {
            configurationValues[key] = value;
        }

        builder.Configuration.AddInMemoryCollection(configurationValues);
        return builder;
    }

    internal static Action<IServiceCollection> ConfigureTestServices(
        ActivationHostOptions options,
        ActivationTestDependencies dependencies)
    {
        return services =>
        {
            services.AddSingleton(dependencies.BodyReads);
            services.AddSingleton<IStartupFilter, BodyReadCountingStartupFilter>();

            if (options.RegisterHealth)
            {
                services.RemoveAll<IDurableRuntimeHealth>();
                services.AddSingleton<IDurableRuntimeHealth>(dependencies.Health);
            }
            else
            {
                services.RemoveAll<IDurableRuntimeHealth>();
            }

            if (options.RegisterAdmission)
            {
                services.RemoveAll<IDurableRuntimePumpAdmission>();
                services.AddSingleton<IDurableRuntimePumpAdmission>(dependencies.Admission);
            }
            else
            {
                services.RemoveAll<IDurableRuntimePumpAdmission>();
            }

            if (!options.UseProductionActivationService)
            {
                services.RemoveAll<IDurableExternalActivationService>();
                services.AddSingleton<IDurableExternalActivationService>(dependencies.Activation);
            }

            options.ConfigureServices?.Invoke(services);
        };
    }

    internal static async Task<ActivationTestHost> StartAsync(
        ActivationHostOptions? options = null,
        ActivationTestDependencies? dependencies = null)
    {
        options ??= new ActivationHostOptions();
        dependencies ??= new ActivationTestDependencies();

        var builder = CreateBuilder(options);
        var app = ForgeTrust.AppSurface.Examples.DurableExternalActivation.DurableExternalActivationProgram.BuildApplication(
            builder,
            ConfigureTestServices(options, dependencies),
            options.ConfigureExternalAuthentication,
            options.ConfigureExternalAuthorization);

        await app.StartAsync().ConfigureAwait(false);

        HttpClient client;
        if (options.UseKestrel)
        {
            var server = app.Services.GetRequiredService<IServer>();
            var address = server.Features.Get<IServerAddressesFeature>()?.Addresses.Single()
                ?? throw new InvalidOperationException("Kestrel did not publish its loopback address.");
            client = new HttpClient { BaseAddress = new Uri(address, UriKind.Absolute) };
        }
        else
        {
            client = app.GetTestClient();
        }

        return new ActivationTestHost(app, client, dependencies);
    }

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        await App.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Detects whether application startup was reached after authentication or policy composition failed.</summary>
internal sealed class ListenerStartupProbe : IHostedService
{
    private int startCount;

    /// <summary>Gets the number of host-start invocations; zero is required when application construction fails.</summary>
    internal int StartCount => Volatile.Read(ref startCount);

    /// <summary>Records host startup without opening a listener or starting background work.</summary>
    /// <param name="cancellationToken">Host-start cancellation token.</param>
    /// <returns>An already completed startup task.</returns>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref startCount);
        return Task.CompletedTask;
    }

    /// <summary>Completes host shutdown without additional work.</summary>
    /// <param name="cancellationToken">Host-stop cancellation token.</param>
    /// <returns>An already completed shutdown task.</returns>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Controls a real HTTP/1.1 loopback connection whose chunked body remains pending until the test releases it.</summary>
/// <remarks>
/// The request asks for connection close so response completion can be observed at transport EOF. Tests may inspect
/// the raw response, including any chunk framing. This helper never manufactures a server request-abort signal.
/// </remarks>
internal sealed class LoopbackActivationConnection : IAsyncDisposable
{
    private readonly TcpClient client;
    private readonly NetworkStream stream;
    private int aborted;

    /// <summary>Captures the connected transport and its network stream.</summary>
    /// <param name="client">Connected loopback client owned by this helper.</param>
    private LoopbackActivationConnection(TcpClient client)
    {
        this.client = client;
        stream = client.GetStream();
    }

    /// <summary>Connects to the test's Kestrel listener and sends authorized chunked headers without body bytes.</summary>
    /// <param name="address">HTTP loopback address published by the test host.</param>
    /// <param name="cancellationToken">Watchdog cancellation for transport setup, not a service request budget.</param>
    /// <returns>The connection with a still-open, empty request-body transport.</returns>
    /// <exception cref="ArgumentException">The supplied address is not an HTTP loopback address.</exception>
    internal static async Task<LoopbackActivationConnection> OpenAsync(Uri address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!address.IsLoopback || address.Scheme != Uri.UriSchemeHttp)
        {
            throw new ArgumentException("The transport fixture requires an HTTP loopback address.", nameof(address));
        }

        var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, address.Port, cancellationToken).ConfigureAwait(false);
            var connection = new LoopbackActivationConnection(client);
            await connection.SendAsync(
                $"POST /private/durable/activate HTTP/1.1\r\nHost: {address.Authority}\r\nAuthorization: Bearer {ActivationTestHost.ValidToken}\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n",
                cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Releases exact ASCII transport bytes, allowing the test to choose when the body becomes readable.</summary>
    /// <param name="wireBytes">HTTP chunk framing and payload to send.</param>
    /// <param name="cancellationToken">Watchdog cancellation for the transport write.</param>
    /// <returns>A task completed after the chosen bytes have been written and flushed.</returns>
    internal async Task SendAsync(string wireBytes, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(wireBytes).AsMemory(), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the bounded raw HTTP response until the server closes the connection.</summary>
    /// <param name="cancellationToken">Watchdog cancellation if transport completion never arrives.</param>
    /// <returns>Status line, response headers, and raw response body, including any chunk framing.</returns>
    /// <exception cref="InvalidDataException">The fixture response exceeds 16 KiB.</exception>
    internal async Task<string> ReadResponseAsync(CancellationToken cancellationToken)
    {
        using var response = new MemoryStream();
        var buffer = new byte[1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return Encoding.ASCII.GetString(response.ToArray());
            }

            if (response.Length + read > 16 * 1024)
            {
                throw new InvalidDataException("The loopback fixture received an unexpectedly large response.");
            }

            response.Write(buffer, 0, read);
        }
    }

    /// <summary>Resets the actual client socket so Kestrel must discover the disconnect and cancel the request.</summary>
    internal void Abort()
    {
        if (Interlocked.Exchange(ref aborted, 1) == 0)
        {
            var socket = client.Client;
            socket.LingerState = new LingerOption(enable: true, seconds: 0);
            // TcpClient disposes its owning NetworkStream by shutting down the socket before closing it.
            // Close the socket directly so this test sends an abortive reset rather than a graceful shutdown.
            socket.Close(0);
        }
    }

    /// <summary>Closes the transport after either normal response completion or a deliberate disconnect.</summary>
    /// <returns>A completed disposal task once the network stream has been released.</returns>
    public async ValueTask DisposeAsync()
    {
        await stream.DisposeAsync().ConfigureAwait(false);
        client.Dispose();
    }
}

internal sealed class RecordingRuntimeHealth : IDurableRuntimeHealth
{
    private Func<CancellationToken, ValueTask<DurableRuntimeHealthSnapshot>> reader =
        _ => ValueTask.FromResult(ActivationTestData.Snapshot(DurableRuntimeHealthState.NotStarted));

    private long callCount;

    internal long CallCount => Interlocked.Read(ref callCount);

    internal ConcurrentQueue<CancellationToken> CancellationTokens { get; } = new();

    internal Func<CancellationToken, ValueTask<DurableRuntimeHealthSnapshot>> Reader
    {
        get => reader;
        set => reader = value ?? throw new ArgumentNullException(nameof(value));
    }

    public ValueTask<DurableRuntimeHealthSnapshot> GetAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref callCount);
        CancellationTokens.Enqueue(cancellationToken);
        return Reader(cancellationToken);
    }
}

internal sealed class RecordingPumpAdmission : IDurableRuntimePumpAdmission
{
    private Func<DurableRuntimePumpRequest, CancellationToken, ValueTask<DurableRuntimePumpAttempt>> runner =
        (_, _) => ValueTask.FromResult(ActivationTestData.CompletedAttempt());

    private long callCount;

    internal long CallCount => Interlocked.Read(ref callCount);

    internal ConcurrentQueue<(DurableRuntimePumpRequest Request, CancellationToken CancellationToken)> Calls { get; } = new();

    internal Func<DurableRuntimePumpRequest, CancellationToken, ValueTask<DurableRuntimePumpAttempt>> Runner
    {
        get => runner;
        set => runner = value ?? throw new ArgumentNullException(nameof(value));
    }

    public ValueTask<DurableRuntimePumpAttempt> TryRunOnceAsync(
        DurableRuntimePumpRequest request,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref callCount);
        Calls.Enqueue((request, cancellationToken));
        return Runner(request, cancellationToken);
    }
}

internal sealed class RecordingActivationService : IDurableExternalActivationService
{
    private Func<DurableExternalActivationRequest, CancellationToken, ValueTask<DurableExternalActivationResult>> activator =
        (_, _) => ValueTask.FromResult(ActivationTestData.Result(
            DurableExternalActivationOutcomeKind.Completed,
            DurableRuntimeHealthState.Healthy,
            pumpResult: ActivationTestData.PumpResult()));

    private long callCount;

    internal long CallCount => Interlocked.Read(ref callCount);

    internal ConcurrentQueue<(DurableExternalActivationRequest Request, CancellationToken CancellationToken)> Calls { get; } = new();

    internal Func<DurableExternalActivationRequest, CancellationToken, ValueTask<DurableExternalActivationResult>> Activator
    {
        get => activator;
        set => activator = value ?? throw new ArgumentNullException(nameof(value));
    }

    public ValueTask<DurableExternalActivationResult> ActivateAsync(
        DurableExternalActivationRequest request,
        CancellationToken callerCancellation = default)
    {
        Interlocked.Increment(ref callCount);
        Calls.Enqueue((request, callerCancellation));
        return Activator(request, callerCancellation);
    }
}

internal static class ActivationTestData
{
    internal static DurableRuntimeHealthSnapshot Snapshot(
        DurableRuntimeHealthState state,
        string? problemCode = null,
        bool? schemaCompatible = null,
        bool? epochCompatible = null,
        bool? isDraining = null)
    {
        var schemaMatches = schemaCompatible ?? state is not DurableRuntimeHealthState.Unavailable and not DurableRuntimeHealthState.Incompatible;
        var epochMatches = epochCompatible ?? state != DurableRuntimeHealthState.Unavailable;
        var epoch = Guid.Parse("c9a39f4a-5936-4c1a-b58d-e1af9280c857");

        return new DurableRuntimeHealthSnapshot(
            state,
            problemCode,
            schemaMatches,
            epochMatches,
            installedSchemaVersion: 1,
            requiredSchemaVersion: 1,
            configuredRuntimeEpoch: epoch,
            activeRuntimeEpoch: epochMatches ? epoch : null,
            workerId: "external-activation-http-test",
            workerInstanceId: null,
            hostedSurfaces: DurableRuntimeSurface.Work,
            observedAtUtc: DateTimeOffset.UnixEpoch,
            startedAtUtc: null,
            lastHeartbeatAtUtc: null,
            lastSuccessfulSweepAtUtc: null,
            isDraining: isDraining ?? state == DurableRuntimeHealthState.Draining,
            isPassActive: false,
            dueDispatchCount: 0,
            oldestDueAtUtc: null,
            oldestDueAge: null);
    }

    internal static DurableRuntimePumpResult PumpResult(
        int discovered = 0,
        int claimed = 0,
        int processed = 0,
        int deferred = 0,
        int failed = 0,
        bool hasMore = false,
        DateTimeOffset? nextDueAtUtc = null,
        long elapsedTicks = 0) => new(
        discovered,
        claimed,
        processed,
        deferred,
        failed,
        hasMore,
        nextDueAtUtc,
        TimeSpan.FromTicks(elapsedTicks));

    internal static DurableRuntimePumpAttempt CompletedAttempt(DurableRuntimePumpResult? result = null) => new(
        DurableRuntimePumpAttemptKind.Completed,
        result ?? PumpResult(),
        problemCode: null);

    internal static DurableExternalActivationResult Result(
        DurableExternalActivationOutcomeKind kind,
        DurableRuntimeHealthState? observedState,
        string? problemCode = null,
        DurableRuntimePumpResult? pumpResult = null) => new(kind, observedState, problemCode, pumpResult);
}

/// <summary>Records the endpoint's consumed body bytes and any test-injected stream failure.</summary>
/// <param name="BytesRead">Bytes successfully returned to the application handler.</param>
/// <param name="Failed">Whether the test-injected failure was raised.</param>
internal sealed record BodyReadObservation(long BytesRead, bool Failed);

/// <summary>Records bounded body reads and exposes barriers for real transport lifecycle tests.</summary>
internal sealed class BodyReadProbe
{
    private readonly ConcurrentQueue<BodyReadObservation> observations = new();
    private readonly ConcurrentQueue<int> requestedReadSizes = new();
    private readonly TaskCompletionSource readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource readPending = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource observationRecorded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource requestAborted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<CancellationToken> readFailed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Gets completed-request observations, excluding requests still blocked in a read.</summary>
    internal IReadOnlyList<BodyReadObservation> Observations => observations.ToArray();

    /// <summary>Gets a barrier completed once the handler requests its first body read.</summary>
    internal Task ReadStarted => readStarted.Task;

    /// <summary>Gets a barrier completed only after the real body stream returns an incomplete asynchronous read.</summary>
    internal Task ReadPending => readPending.Task;

    /// <summary>Gets a barrier completed after the request pipeline has returned or thrown.</summary>
    internal Task ObservationRecorded => observationRecorded.Task;

    /// <summary>Gets the requested buffer lengths, including reads blocked before receiving transport bytes.</summary>
    internal IReadOnlyList<int> RequestedReadSizes => requestedReadSizes.ToArray();

    /// <summary>Gets a barrier completed by Kestrel's actual request-abort cancellation signal.</summary>
    internal Task RequestAborted => requestAborted.Task;

    /// <summary>Gets the token supplied to the first body read that fails with a transport or cancellation exception.</summary>
    internal Task<CancellationToken> ReadFailed => readFailed.Task;

    /// <summary>Gets or sets whether a failed transport read waits for Kestrel's actual request-abort notification.</summary>
    /// <remarks>
    /// The disconnect test enables this barrier so body poisoning cannot finish the request and dispose its abort
    /// registration before Kestrel's separately scheduled cancellation callback runs. It never cancels the request.
    /// </remarks>
    internal bool AwaitRequestAbortedOnReadFailure { get; set; }

    /// <summary>Gets or sets the test watchdog that bounds the optional transport-failure observation barrier.</summary>
    /// <remarks>This token cancels only the test observer's wait and is never passed into the endpoint's body read.</remarks>
    internal CancellationToken ReadFailureObservationCancellation { get; set; }

    /// <summary>Records the requested bound before signaling that the endpoint has entered a body read.</summary>
    /// <param name="requestedBytes">Number of bytes the caller allowed this stream read to receive.</param>
    internal void MarkReadStarted(int requestedBytes)
    {
        requestedReadSizes.Enqueue(requestedBytes);
        readStarted.TrySetResult();
    }

    /// <summary>Signals that the underlying body stream has started a read that still awaits transport input.</summary>
    internal void MarkReadPending() => readPending.TrySetResult();

    /// <summary>Signals that the observed request's transport cancellation token has fired.</summary>
    internal void MarkRequestAborted() => requestAborted.TrySetResult();

    /// <summary>Records a failed read's original token and optionally waits for genuine transport cancellation.</summary>
    /// <param name="readCancellationToken">The endpoint's cancellation token passed unchanged to the real body stream.</param>
    /// <returns>A task completed after observation, including request cancellation when the disconnect barrier is enabled.</returns>
    internal async Task ObserveReadFailureAsync(CancellationToken readCancellationToken)
    {
        readFailed.TrySetResult(readCancellationToken);
        if (AwaitRequestAbortedOnReadFailure)
        {
            await RequestAborted.WaitAsync(ReadFailureObservationCancellation).ConfigureAwait(false);
        }
    }

    /// <summary>Records a completed request before releasing observers waiting for its final consumed-byte count.</summary>
    /// <param name="observation">Request-body consumption and injected-failure facts.</param>
    internal void Record(BodyReadObservation observation)
    {
        observations.Enqueue(observation);
        observationRecorded.TrySetResult();
    }
}

internal sealed class BodyReadCountingStartupFilter(BodyReadProbe probe) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, continuation) =>
        {
            if (!HttpMethods.IsPost(context.Request.Method)
                || !context.Request.Path.Equals("/private/durable/activate", StringComparison.Ordinal))
            {
                await continuation(context).ConfigureAwait(false);
                return;
            }

            var originalBody = context.Request.Body;
            using var requestAbortRegistration = context.RequestAborted.Register(probe.MarkRequestAborted);
            var countingBody = new CountingRequestBodyStream(
                originalBody,
                context.Request.Headers.ContainsKey("X-Test-Body-Read-Failure"),
                probe);
            context.Request.Body = countingBody;
            try
            {
                await continuation(context).ConfigureAwait(false);
            }
            finally
            {
                context.Request.Body = originalBody;
                probe.Record(new BodyReadObservation(countingBody.BytesRead, countingBody.Failed));
            }
        });
        next(app);
    };
}

internal sealed class CountingRequestBodyStream(Stream inner, bool throwOnRead, BodyReadProbe probe) : Stream
{
    private long bytesRead;
    private int failed;

    internal long BytesRead => Interlocked.Read(ref bytesRead);

    internal bool Failed => Volatile.Read(ref failed) != 0;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        probe.MarkReadStarted(count);
        ThrowIfRequested();
        var read = inner.Read(buffer, offset, count);
        Interlocked.Add(ref bytesRead, read);
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        probe.MarkReadStarted(buffer.Length);
        ThrowIfRequested();
        var read = inner.Read(buffer);
        Interlocked.Add(ref bytesRead, read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        probe.MarkReadStarted(buffer.Length);
        ThrowIfRequested();
        try
        {
            var pendingRead = inner.ReadAsync(buffer, cancellationToken);
            if (!pendingRead.IsCompleted)
            {
                probe.MarkReadPending();
            }

            var read = await pendingRead.ConfigureAwait(false);
            Interlocked.Add(ref bytesRead, read);
            return read;
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            await probe.ObserveReadFailureAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int ReadByte()
    {
        Span<byte> oneByte = stackalloc byte[1];
        return Read(oneByte) == 0 ? -1 : oneByte[0];
    }

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
    }

    private void ThrowIfRequested()
    {
        if (throwOnRead)
        {
            Volatile.Write(ref failed, 1);
            throw new IOException("Test request-body read failure.");
        }
    }
}

internal sealed class PermissionTestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal const string SchemeName = "ExternalActivationTest";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-Permission", out var values) || values.Count == 0)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "http-contract-test") };
        if (string.Equals(values[0], "durable-activation", StringComparison.Ordinal))
        {
            claims.Add(new Claim("permission", "durable-activation"));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(principal, SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}

internal static class PermissionTestAuthentication
{
    internal static void Configure(AuthenticationBuilder builder)
    {
        builder.Services.Configure<AuthenticationOptions>(options =>
        {
            options.DefaultAuthenticateScheme = PermissionTestAuthenticationHandler.SchemeName;
            options.DefaultChallengeScheme = PermissionTestAuthenticationHandler.SchemeName;
            options.DefaultForbidScheme = PermissionTestAuthenticationHandler.SchemeName;
        });
        builder.AddScheme<AuthenticationSchemeOptions, PermissionTestAuthenticationHandler>(
            PermissionTestAuthenticationHandler.SchemeName,
            _ => { });
    }

    internal static void ConfigurePolicy(AuthorizationOptions options) => options.AddPolicy(
        ForgeTrust.AppSurface.Examples.DurableExternalActivation.ActivationHttpEndpoints.AuthorizationPolicy,
        policy => policy.RequireAuthenticatedUser().RequireClaim("permission", "durable-activation"));
}
