using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using ForgeTrust.AppSurface.Auth.Keycloak;
using ForgeTrust.AppSurface.Core;
using static ForgeTrust.AppSurface.Auth.Aspire.Keycloak.AppSurfaceKeycloakDiagnosticCodes;
using static ForgeTrust.AppSurface.Auth.Aspire.Keycloak.AppSurfaceKeycloakSupervisedOptions;
using static ForgeTrust.AppSurface.Auth.Aspire.Keycloak.SupervisedKeycloakOwnership;
using static ForgeTrust.AppSurface.Auth.Aspire.Keycloak.SupervisedKeycloakRunLayout;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

/// <summary>Coordinates actual container execution behind normal Aspire registration and observable completion handles.</summary>
/// <remarks>
/// Candidate supervised backend. The graph handles are explicitly started diagnostic/completion resources; the package
/// owns the container namespace. Call RunAsync rather than app.RunAsync so teardown has an explicit nonzero result.
/// Host ProjectResource seeds are rejected. This profile currently qualifies macOS ARM64 with Linux ARM64 Docker.
/// </remarks>
public sealed class AppSurfaceKeycloakSupervisedRun
{
    private readonly IDistributedApplicationBuilder _builder;
    private readonly AppSurfaceKeycloakOptions _options;
    private readonly SupervisedKeycloakRunStager _stager;
    private readonly AppSurfaceKeycloakSupervisedOptions _execution;
    private readonly AppSurfaceKeycloakCredentialSnapshot _bindings;
    private readonly AppSurfaceKeycloakThemeRegistrationState? _theme;
    private readonly List<(AppSurfaceKeycloakSupervisedWorkload Workload, bool Web, IResourceBuilder<ExecutableResource> Handle)> _workloads = [];
    private readonly TaskCompletionSource<string> _failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _work = new();
    private readonly SupervisedDocker _docker;
    private readonly Func<HttpClient>? _httpClient;
    private IReadOnlyList<string> _privateValues = [];
    private SupervisedVolumeLease? _lease;
    private SupervisedKeycloakOwnership? _ownership;
    private ResourceNotificationService? _notifications;
    private Task? _workflow;
    private AppSurfaceKeycloakOwnerConnection? _ownerConnection;
    private int _ownerPort;
    private bool _useHistoricalServerUser;
    private volatile bool _ready;
    private volatile string? _terminalCode;
    private string? _anchorId;
    private IResourceBuilder<KeycloakResource> _keycloak = null!;
    private bool _sealed;
    private readonly string _workerSource;
    private ImmutableDictionary<string, string> _publicInputs = ImmutableDictionary<string, string>.Empty;

    private AppSurfaceKeycloakSupervisedRun(IDistributedApplicationBuilder builder, AppSurfaceKeycloakRegistrationSnapshot registration,
        AppSurfaceKeycloakCredentialSnapshot bindings, SupervisedDocker? docker, Func<HttpClient>? httpClient)
    {
        _builder = builder;
        _options = registration.Options;
        _execution = _options.SupervisedExecution!;
        _bindings = bindings;
        _docker = docker ?? new(_execution.DockerEndpoint);
        _theme = registration.Theme;
        _httpClient = httpClient;
        OwnerId = Guid.NewGuid().ToString("N");
        RunDirectory = PathUtils.PathUnder(_execution.RunRoot, OwnerId);
        _stager = new(RunDirectory, OwnerId, registration, SafeException);
        _workerSource = AppSurfaceKeycloakRealmReadyWorker.Resolve().WorkingDirectory;
    }

    /// <summary>Gets this run's opaque owner id; supplying it never authorizes recovery without verification.</summary>
    public string OwnerId { get; }
    /// <summary>Gets the private run directory that will be created only under ownership.</summary>
    public string RunDirectory { get; }
    /// <summary>Gets the normal Aspire completion handle for the actual finite generic worker.</summary>
    public AppSurfaceKeycloakRealmReady RealmReady { get; private set; } = null!;
    /// <summary>Gets the terminal diagnostic, the first latched failure, or the current readiness status.</summary>
    public string DiagnosticCode => _terminalCode ?? FailureCode ?? (_ready ? "Ready" : UnexpectedFailure);

    private string? FailureCode => _failure.Task.IsCompletedSuccessfully ? _failure.Task.Result : null;
    internal Func<CancellationToken, Task>? BeforeCleanObservation { get; set; }
    internal bool InjectUnverifiedStop { get; set; }
    /// <summary>Exposes durable launch/cancellation barriers only to coordinated lifecycle tests.</summary>
    internal Func<string, CancellationToken, Task>? LifecycleObservation { get; set; }

    /// <summary>Verifies an expected stopped owner and clears only its active marker, before constructing an Aspire graph.</summary>
    /// <param name="execution">The same local socket, shared lease root and pinned runtime profile.</param>
    /// <param name="volumeName">The same explicit physical volume name recorded by the failed run.</param>
    /// <param name="expectedOwnerId">Exact owner id from the retained private marker.</param>
    /// <param name="cancellationToken">Cancels verification without granting a stop or clear.</param>
    /// <returns>A recovery-only result. The host must exit rather than continuing ordinary startup.</returns>
    /// <remarks>No administrator/client/seed value is resolved. No workload is started, stopped or killed. Metadata-only observation helpers are separately bounded.</remarks>
    public static Task<AppSurfaceKeycloakSupervisedRecoveryResult> VerifyStoppedAndClearAsync(
        AppSurfaceKeycloakSupervisedOptions execution, string volumeName, string expectedOwnerId, CancellationToken cancellationToken = default)
        => VerifyStoppedAndClearAsync(execution, volumeName, expectedOwnerId, null, cancellationToken);

    /// <summary>Performs the same recovery with optional observation at the final compare-and-clear window.</summary>
    /// <remarks>Coordinated proof can hold the window under the native lease. Tests may supply deterministic Docker inventory.</remarks>
    internal static async Task<AppSurfaceKeycloakSupervisedRecoveryResult> VerifyStoppedAndClearAsync(
        AppSurfaceKeycloakSupervisedOptions execution, string volumeName, string expectedOwnerId,
        Func<CancellationToken, Task>? beforeClear, CancellationToken cancellationToken = default, SupervisedDocker? docker = null)
    {
        ArgumentNullException.ThrowIfNull(execution);
        if (!Guid.TryParseExact(expectedOwnerId, "N", out _)) throw new ArgumentException("Invalid expected owner.", nameof(expectedOwnerId));
        var settings = execution.Snapshot(); docker ??= new SupervisedDocker(settings.DockerEndpoint);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); stop.CancelAfter(StopBound);
        var clock = Stopwatch.StartNew(); var daemon = await docker.DaemonAsync(stop.Token);
        using var lease = await SupervisedVolumeLease.Acquire(settings.LeaseRoot, daemon, volumeName);
        if (!lease.HasActive) throw new AppSurfaceKeycloakException(RecoveryRejected, "Recovery request is stale; no active owner. Workloads started: false.");
        var bytes = lease.ReadActive();
        ActiveRecord? active;
        try { active = JsonSerializer.Deserialize<ActiveRecord>(bytes); }
        catch (JsonException) { throw RecoveryUnverified(); }
        if (active is null || active.Schema != 3 || active.LaunchUncertain || active.OwnerId != expectedOwnerId
            || active.DaemonId != daemon || active.VolumeName != volumeName || active.Volume is null
            || !active.StagingComplete || active.OwnerProcess is not { Role: "apphost", Pid: > 1, StartTimeUtcTicks: > 0 }
            || active.Containers is null || active.Containers.Any(container => container is null)
            || !QualifiedProcessIdentity.Stopped(active.OwnerProcess)
            || active.Containers.Any(c => c.Role == AnchorRole) && !active.OwnerFenceEstablished
            || active.RunRoot != PathUtils.PathUnder(settings.RunRoot, expectedOwnerId))
            throw RecoveryUnverified();
        QualifiedLeaseDirectory.ValidatePrivateDirectory(active.RunRoot);
        var verifier = new SupervisedKeycloakOwnership(docker, settings.RuntimeImage, lease, active,
            () => ExecutionFailure(UnexpectedFailure));
        await verifier.VerifyStoppedAsync(active, stop.Token, bytes);
        if (beforeClear is not null) await beforeClear(stop.Token);
        ClearVerifiedMarker(lease, () => clock.Elapsed, stop.Token, bytes);
        System.Console.Error.WriteLine("AppSurface Keycloak recovery-only completed; workloads_started=false; remove request then rerun.");
        return new("RecoveryOnlyCompleted", false, true);
    }

    private static AppSurfaceKeycloakException RecoveryUnverified() =>
        new(RecoveryRejected, "Expected owner or stopped inventory cannot be verified; workloads started: false.");

    /// <summary>Registers the supervised graph and defers all ownership and private input resolution until start.</summary>
    /// <remarks>Production uses the scoped local Docker client and existing pinned HTTPS transport. Tests can supply deterministic inventory and HTTP responses.</remarks>
    internal static AppSurfaceKeycloakResource Register(IDistributedApplicationBuilder builder, string name,
        AppSurfaceKeycloakOptions options, AppSurfaceKeycloakCredentialSnapshot bindings, SupervisedDocker? docker = null,
        Func<HttpClient>? httpClient = null) =>
        Register(builder, name, options.CreateRegistrationSnapshot(), bindings, docker, httpClient);

    /// <summary>Registers the supervised graph from the single validated registration snapshot.</summary>
    internal static AppSurfaceKeycloakResource Register(IDistributedApplicationBuilder builder, string name,
        AppSurfaceKeycloakRegistrationSnapshot registration, AppSurfaceKeycloakCredentialSnapshot bindings, SupervisedDocker? docker = null,
        Func<HttpClient>? httpClient = null)
    {
        var options = registration.Options;
        if (string.IsNullOrWhiteSpace(name) || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')
            || options.SeededUsers.Count != 0 || !options.RequireIdentitySeed)
            throw AppSurfaceKeycloakCredentialBindings.Invalid();
        if (!IsSupportedIssuer(options))
            throw AppSurfaceKeycloakCredentialBindings.Invalid();
        var theme = registration.Theme;
        if (theme is not null && theme.SourceDirectory.Any(character => character == ',' || char.IsControl(character)))
            throw AppSurfaceKeycloakCredentialBindings.Invalid();
        var run = new AppSurfaceKeycloakSupervisedRun(builder, registration, bindings, docker, httpClient);
        var keycloak = builder.AddKeycloak(name, adminUsername: bindings.AdminUsername, adminPassword: bindings.AdminPassword)
            .WithExplicitStart();
        // The namespace backend supplies private bootstrap, TLS and fixed loopback ports. Never project official
        // bootstrap parameters into ordinary resource environment callbacks or reserve DCP proxy endpoints.
        foreach (var annotation in keycloak.Resource.Annotations.Where(a => a is EnvironmentCallbackAnnotation or CommandLineArgsCallbackAnnotation
                     or EndpointAnnotation or HealthCheckAnnotation).ToArray()) keycloak.Resource.Annotations.Remove(annotation);
        run._keycloak = keycloak;
        if (options.PersistentVolumeName is not null) keycloak.WithDataVolume(options.PersistentVolumeName);
        else if (options.UsePersistentDataVolume) keycloak.WithDataVolume();
        run.RealmReady = new(builder.AddExecutable(name + "-realm-ready", "/usr/bin/false", AppContext.BaseDirectory)
            .WithExplicitStart().WaitFor(keycloak));
        var wrapper = new AppSurfaceKeycloakResource(keycloak, registration.Projection,
            new AppSurfaceKeycloakReadinessProbe(registration), Desired.HostPath(run.RunDirectory), theme?.Registration)
        { SupervisedRun = run };
        builder.OnBeforeStart(async (BeforeStartEvent e, CancellationToken ct) =>
        {
            run._sealed = true;
            foreach (var endpoint in keycloak.Resource.Annotations.OfType<EndpointAnnotation>()) endpoint.TargetPort ??= 9000;
            run._notifications = e.Services.GetRequiredService<ResourceNotificationService>();
            try { await run.PrepareAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (AppSurfaceKeycloakException error) when (error.Code is OwnershipUnavailable or OwnershipConflict)
            {
                run._failure.TrySetResult(error.Code);
                throw;
            }
            catch
            {
                run._failure.TrySetResult(OwnershipUnavailable);
                throw ExecutionFailure(OwnershipUnavailable);
            }
        });
        builder.Eventing.Subscribe<AfterResourcesCreatedEvent>((e, ct) =>
        {
            run._workflow = run.WorkflowAsync();
            return Task.CompletedTask;
        });
        return wrapper;
    }

    /// <summary>Sets additional nonsecret consumer configuration before graph start.</summary>
    /// <param name="values">Fields that may not override package identity, paths or transport policy.</param>
    /// <returns>This run for fluent configuration.</returns>
    public AppSurfaceKeycloakSupervisedRun WithPublicInputs(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (_sealed || values.Count > 32) throw SafeException();
        var inputs = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length > 128 || value is null || value.Length > 4096
                || !inputs.TryAdd(key, value)) throw SafeException();
        }
        _publicInputs = inputs.ToImmutable();
        return this;
    }

    /// <summary>Registers one required finite container seed in the normal Aspire completion chain.</summary>
    /// <param name="workload">Copied portable payload and separate typed private-input bindings.</param>
    /// <returns>The normal Aspire resource on which subsequent local work can WaitForCompletion.</returns>
    /// <exception cref="AppSurfaceKeycloakException">The workload is invalid or its name is already in use or reserved for the package.</exception>
    public IResourceBuilder<ExecutableResource> WithSeed(AppSurfaceKeycloakSupervisedWorkload workload) => AddWorkload(workload, false);

    /// <summary>Registers the one web container, starting only after every required seed completed successfully.</summary>
    /// <param name="workload">Copied portable web payload. It receives no administrator or seed input mount.</param>
    /// <returns>The normal Aspire resource representing this owned web container.</returns>
    /// <exception cref="AppSurfaceKeycloakException">The workload is invalid or its name is already in use or reserved for the package.</exception>
    public IResourceBuilder<ExecutableResource> WithWeb(AppSurfaceKeycloakSupervisedWorkload workload) => AddWorkload(workload, true);

    private IResourceBuilder<ExecutableResource> AddWorkload(AppSurfaceKeycloakSupervisedWorkload workload, bool web)
    {
        ArgumentNullException.ThrowIfNull(workload);
        if (_sealed || _workloads.Count >= 8 || _workloads.Any(w => w.Web) || web && !_workloads.Any()
            || !Path.IsPathFullyQualified(workload.PayloadDirectory) || workload.PayloadDirectory.Any(c => char.IsControl(c) || c == ',')
            || string.IsNullOrEmpty(workload.Name) || workload.Name.Length is < 3 or > 63
            || workload.Name.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
            || IsReservedWorkloadName(workload.Name) || _workloads.Any(w => w.Workload.Name == workload.Name)
            || string.IsNullOrEmpty(workload.AssemblyName) || Path.GetFileName(workload.AssemblyName) != workload.AssemblyName || !workload.AssemblyName.EndsWith(".dll", StringComparison.Ordinal)
            || workload.Arguments.IsDefault || workload.Arguments.Length > 32 || workload.Arguments.Any(a => a is null || a.Length > 4096 || a.Any(char.IsControl))
            || workload.Environment is null || workload.Environment.Count > 32 || workload.Environment.Any(p => string.IsNullOrEmpty(p.Key) || p.Key.Length > 128
                || p.Key.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_') || p.Value is null || p.Value.Length > 4096 || p.Value.Any(char.IsControl))
            || workload.PrivateInputs is null || web && workload.PrivateInputs.Count != 0 || workload.PrivateInputs.Count > 32
            || workload.PrivateInputs.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Key.Length > 128 || p.Value is null
                || !p.Value.Resource.Secret && !(p.Key == "AdminUsername" && ReferenceEquals(p.Value.Resource, _bindings.AdminUsername.Resource))
                || !ReferenceEquals(p.Value.ApplicationBuilder, _builder))) throw SafeException();
        var copy = workload with
        {
            PayloadDirectory = Path.GetFullPath(workload.PayloadDirectory),
            Arguments = workload.Arguments.ToImmutableArray(),
            Environment = workload.Environment.ToImmutableDictionary(),
            PrivateInputs = workload.PrivateInputs.ToImmutableDictionary()
        };
        var handle = _builder.AddExecutable(workload.Name, "/usr/bin/false", AppContext.BaseDirectory).WithExplicitStart();
        handle.WaitForCompletion(_workloads.LastOrDefault().Handle ?? RealmReady.Resource);
        _workloads.Add((copy, web, handle));
        return handle;
    }

    /// <summary>Starts the normal Aspire app and returns only after owned shutdown has passed or failed its fixed 30-second bound.</summary>
    /// <param name="app">The app built from this registration's builder.</param>
    /// <param name="cancellationToken">Requests normal owned shutdown; required failures also latch a nonzero result.</param>
    /// <returns>A process-exit result. The AppHost entry point must return its ExitCode.</returns>
    public async Task<AppSurfaceKeycloakSupervisedResult> RunAsync(DistributedApplication app, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        string? fallbackFailure = null;
        try
        {
            await app.StartAsync(cancellationToken);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
                app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);
            var cancelled = Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
            if (await Task.WhenAny(_failure.Task, cancelled) == cancelled && !_ready)
                fallbackFailure = OperationTimedOut;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { fallbackFailure = OperationTimedOut; }
        catch { fallbackFailure = UnexpectedFailure; }
        var operationalFailure = FailureCode ?? fallbackFailure;
        _work.Cancel();
        _ownerConnection?.Dispose();
        var clock = Stopwatch.StartNew();
        using var stop = new CancellationTokenSource(StopBound);
        string? shutdownFailure = null;
        try
        {
            if (_ownership is { } ownership)
            {
                await ownership.FenceAsync(stop.Token);
                if (_workflow is not null) { try { await _workflow.WaitAsync(stop.Token); } catch { } }
                if (ownership.Active.LaunchUncertain) throw new IOException();
                if (_ownerConnection is not null) await _ownerConnection.ObserveStoppedAsync(stop.Token);
                await ownership.VerifyStoppedAsync(ownership.Active, stop.Token);
            }
            await app.StopAsync(stop.Token).WaitAsync(stop.Token);
            await app.DisposeAsync().AsTask().WaitAsync(stop.Token);
            if (_ownership is { } cleanupOwner)
            {
                await cleanupOwner.VerifyStoppedAsync(cleanupOwner.Active, stop.Token);
                if (InjectUnverifiedStop) throw new IOException();
                if (BeforeCleanObservation is not null) await BeforeCleanObservation(stop.Token);
                await cleanupOwner.VerifyStoppedAsync(cleanupOwner.Active, stop.Token);
                await cleanupOwner.RemoveStoppedAsync(cleanupOwner.Active, stop.Token);
                stop.Token.ThrowIfCancellationRequested();
                if (_stager.HasVerifiedRunDirectoryForCleanup())
                {
                    foreach (var directory in GetPrivateCleanupDirectories(RunDirectory))
                    {
                        stop.Token.ThrowIfCancellationRequested();
                        Directory.Delete(directory, recursive: true);
                    }
                }
                ClearVerifiedMarker(_lease!, () => clock.Elapsed, stop.Token);
            }
        }
        catch { shutdownFailure = OwnedShutdownUnverified; }
        finally { _lease?.Dispose(); }
        var failureCode = shutdownFailure ?? operationalFailure;
        var code = failureCode ?? "Completed";
        var activeRetained = shutdownFailure is not null;
        _terminalCode = code;
        if (failureCode is not null)
            System.Console.Error.WriteLine($"AppSurface Keycloak failed. Code: {code}. Owner: {OwnerId}. Active retained: {activeRetained}. Docs: docs/supervised-keycloak-execution.md.");
        return new(failureCode is null ? 0 : 1, code, OwnerId, activeRetained, clock.Elapsed.TotalSeconds);
    }

    private async Task PrepareAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        ValidatePublicWebOrigin(_options, _publicInputs);
        var allowed = new HashSet<IResource>(ReferenceEqualityComparer.Instance) { _keycloak.Resource, RealmReady.Resource.Resource };
        foreach (var item in _workloads) allowed.Add(item.Handle.Resource);
        if (_builder.Resources.Any(r => (r is ContainerResource or ExecutableResource or ProjectResource) && !allowed.Contains(r))) throw SafeException();
        if (_workloads.Count == 0 || _workloads.All(w => w.Web) || _workloads.Count(w => w.Web) != 1) throw SafeException();
        var daemon = await _docker.DaemonAsync(ct);
        var mounts = _keycloak.Resource.Annotations.OfType<ContainerMountAnnotation>().Where(m => m.Target == "/opt/keycloak/data").ToArray();
        if (mounts.Length > 1) throw SafeException();
        var volume = mounts.SingleOrDefault()?.Source;
        var persistent = volume is not null;
        // The legacy boolean can only reuse the exact volume resolved by Aspire. Reject a missing name
        // before writing an owner record, staging a run, or resolving a typed parameter.
        var legacyNamedVolume = _options.UsePersistentDataVolume && _options.PersistentVolumeName is null;
        if (legacyNamedVolume && (volume is null || await _docker.VolumeAsync(volume, ct) is null))
            throw LegacyVolumeUnavailable();
        _lease = await SupervisedVolumeLease.Acquire(_execution.LeaseRoot, daemon, volume ?? "disposable-" + OwnerId);
        if (_lease.HasActive) throw ExecutionFailure(OwnershipConflict);
        var existing = persistent ? await _docker.VolumeAsync(volume!, ct) : null;
        if (legacyNamedVolume && existing is null) throw LegacyVolumeUnavailable();
        using var self = Process.GetCurrentProcess();
        var active = new ActiveRecord(3, OwnerId, daemon, volume, null, RunDirectory,
            new("apphost", self.Id, self.StartTime.ToUniversalTime().Ticks), [], false, false, false);
        var ownership = new SupervisedKeycloakOwnership(_docker, _execution.RuntimeImage, _lease, active, SafeException, ObserveLifecycleAsync);
        _ownership = ownership;
        ownership.Save(active); // durable reservation precedes directory staging, volume creation and value resolution
        await ObserveLifecycleAsync("reserved", ct);
        _stager.StageWorker(_execution.RunRoot, _workerSource);
        if (persistent)
        {
            var created = false;
            if (existing is null)
            {
                await _docker.RunAsync(ct, "volume", "create", volume!);
                existing = await _docker.VolumeAsync(volume!, ct) ?? throw SafeException();
                created = true;
            }
            if ((await _docker.AttachmentsAsync(volume!, ct)).Length != 0) throw SafeException();
            var imageReference = _theme?.BaseImage.Value ?? _execution.KeycloakImage;
            var observePinnedDataOwnership = !created && imageReference is QualifiedKeycloakImage or TaggedQualifiedKeycloakImage;
            var beforeObservation = ownership.Active;
            var observed = await ownership.ObserveVolumeAsync(existing, ct, selectServerUser: observePinnedDataOwnership);
            ownership.Save(beforeObservation with { Volume = observed.Identity });
            // A newly mounted image can leave a UID 1000 root above an H2 database written by the host UID.
            // Keep the host server profile for that volume and preserve the qualified native profile for UID 1000 data.
            _useHistoricalServerUser = observed.Owner is { Uid: 1000, Gid: 0, Database: null or { Uid: 1000, Gid: 0 } };
            if (created) await ownership.InitializeNewVolumeAsync(volume!, ct);
        }
        _privateValues = await _stager.StageInputsAsync(
            _bindings, _workloads.Select(item => item.Workload), _publicInputs, _useHistoricalServerUser, ct);
        ownership.Save(ownership.Active with { StagingComplete = true });
        await ObserveLifecycleAsync("staged", ct);
    }

    private async Task WorkflowAsync()
    {
        try
        {
            var ct = _work.Token;
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            do { listener.Start(); _ownerPort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); }
            while (_ownerPort == _options.KeycloakPort || _ownerPort == _options.WebProofPort);
            var confidential = _options.ClientMode == KeycloakClientMode.Confidential;
            var ownerPort = _ownerPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var fence = confidential ? ClientReference : OwnerFence;
            string[] anchorCommand = confidential
                ? [KeycloakWorkerProtocol.FencedMemoryAnchor, AnchorInput.ContainerPath, fence.ContainerPath, ownerPort, OwnerId]
                : [KeycloakWorkerProtocol.OwnerFence, fence.ContainerPath, ownerPort, OwnerId];
            var anchorMounts = new List<OwnedMount>
                { Mount(Worker), Mount(State, writable: true), Mount(EmptyNuGet) };
            if (confidential) anchorMounts.Add(Mount(AnchorPrivate));
            await LaunchAsync(AnchorRole, _execution.RuntimeImage,
                [WorkerAssembly.ContainerPath, .. anchorCommand],
                anchorMounts, ct);
            var listeningFile = PathUtils.PathUnder(RunDirectory, fence.Mount.HostDirectory, KeycloakWorkerProtocol.ListeningMarkerPath(fence.Name));
            using (var listening = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                listening.CancelAfter(TimeSpan.FromSeconds(8));
                while (!File.Exists(listeningFile)) await Task.Delay(20, listening.Token);
            }
            _ownerConnection = await AppSurfaceKeycloakOwnerConnection.ConnectAsync(_ownerPort, OwnerId,
                () => _failure.TrySetResult(OwnedShutdownUnverified), ct);
            var ownership = _ownership!;
            ownership.Save(ownership.Active with { OwnerFenceEstablished = true });
            await ObserveLifecycleAsync("fence-ready", ct);
            if (confidential && !File.Exists(ClientReference.HostPath(RunDirectory))) throw SafeException();
            var serverMounts = new List<OwnedMount> { Mount(ServerPrivate), Mount(Public) };
            if (ownership.Active.VolumeName is { } volume) serverMounts.Add(new(volume, "/opt/keycloak/data", true, true));
            var transport = $"https://localhost:{_options.KeycloakPort}";
            var frontend = _options.Authority?.GetLeftPart(UriPartial.Authority) ?? transport;
            var serverCertificate = _useHistoricalServerUser ? HistoricalCertificate : PublicCertificate;
            var serverArgs = new List<string>
            {
                "--config-file=" + Bootstrap.ContainerPath, "start-dev", "--http-enabled=false",
                "--https-port="+_options.KeycloakPort, "--hostname="+frontend, "--hostname-strict=true",
                "--https-certificate-file=" + serverCertificate.ContainerPath,
                "--https-certificate-key-file=" + ServerKey.ContainerPath
            };
            if (frontend != transport) serverArgs.Add("--hostname-admin=" + transport);
            if (_theme is not null)
            {
                serverMounts.Add(new(_theme.SourceDirectory, "/opt/keycloak/themes/" + _theme.Registration.Name, false, false));
                serverArgs.AddRange(["--spi-theme--static-max-age=-1", "--spi-theme--cache-themes=false", "--spi-theme--cache-templates=false"]);
            }
            var keycloakImage = _theme?.BaseImage.Value
                ?? (_execution.KeycloakImage == QualifiedKeycloakImage ? TaggedQualifiedKeycloakImage : _execution.KeycloakImage);
            await LaunchAsync(ServerRole, keycloakImage, serverArgs, serverMounts, ct);
            using (var ready = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                ready.CancelAfter(_execution.TransportReadyTimeout);
                using var http = Http();
                while (true)
                {
                    try { using var reply = await http.GetAsync($"https://localhost:{_options.KeycloakPort}/realms/master/.well-known/openid-configuration", ready.Token); if (reply.IsSuccessStatusCode) break; }
                    catch (HttpRequestException) { }
                    await Task.Delay(200, ready.Token);
                }
            }
            await Publish(_keycloak.Resource, true, null);
            var worker = await LaunchAsync(WorkerRole, _execution.RuntimeImage,
                [WorkerAssembly.ContainerPath, KeycloakWorkerProtocol.Reconcile, Desired.ContainerPath,
                    WorkerBindings.ContainerPath, WorkerReceipt.ContainerPath],
                [Mount(Worker), Mount(Public), Mount(WorkerPrivate), Mount(State, writable: true), Mount(EmptyNuGet)], ct);
            await CompleteAsync(worker, RealmReady.Resource.Resource, TimeSpan.FromSeconds(125), ct);
            await ObserveLifecycleAsync("worker-completed", ct);
            foreach (var item in _workloads)
            {
                if (item.Web) await ObserveLifecycleAsync("seeds-completed", ct);
                var payload = WorkloadPayload(item.Workload.Name);
                var mounts = new List<OwnedMount> { Mount(payload), Mount(Public), Mount(State, writable: !item.Web), Mount(EmptyNuGet) };
                if (!item.Web) mounts.Add(Mount(WorkloadPrivate(item.Workload.Name)));
                var container = await LaunchAsync(item.Workload.Name, _execution.RuntimeImage,
                    [payload.ContainerFile(item.Workload.AssemblyName), .. item.Workload.Arguments], mounts, ct, item.Workload.Environment);
                if (item.Web) await Publish(item.Handle.Resource, true, null);
                else await CompleteAsync(container, item.Handle.Resource, _execution.LocalSeedTimeout, ct);
            }
            _ready = true;
            await ObserveLifecycleAsync("ready", ct);
            // Successful development runs remain live. Unexpected anchor/web termination latches a host failure.
            while (!ct.IsCancellationRequested)
            {
                foreach (var item in ownership.Active.Containers.Where(c => (c.Role is AnchorRole or ServerRole) || _workloads.Any(w => w.Web && w.Workload.Name == c.Role)))
                {
                    var observed = await _docker.ContainerAsync(item.Name, item.Id, OwnerId, ct);
                    if (observed is null || !observed.Running) throw new IOException();
                }
                await Task.Delay(200, ct);
            }
        }
        catch (OperationCanceledException) when (_work.IsCancellationRequested) { }
        catch { _failure.TrySetResult(ReconciliationFailed); }
    }

    private async Task CompleteAsync(OwnedContainer container, IResource handle, TimeSpan timeout, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(timeout);
        while (true)
        {
            var observed = await _docker.ContainerAsync(container.Name, container.Id, OwnerId, deadline.Token) ?? throw SafeException();
            if (!observed.Running)
            {
                var exit = observed.Snapshot.GetProperty("State").GetProperty("ExitCode").GetInt32();
                await Publish(handle, false, exit);
                if (exit != 0) throw SafeException();
                return;
            }
            await Task.Delay(80, deadline.Token);
        }
    }

    private Task Publish(IResource resource, bool running, int? exit) => _notifications!.PublishUpdateAsync(resource, s => s with
    { State = new ResourceStateSnapshot(running ? KnownResourceStates.Running : KnownResourceStates.Exited, null), ExitCode = exit });

    private async Task<OwnedContainer> LaunchAsync(
        string role, string image, IReadOnlyList<string> args, IReadOnlyList<OwnedMount> mounts,
        CancellationToken ct, IReadOnlyDictionary<string, string>? environment = null)
    {
        var name = "appsurface-owned-" + OwnerId + "-" + role;
        var anchor = role == AnchorRole;
        var ports = anchor ? new[] { _options.KeycloakPort, _options.WebProofPort, _ownerPort } : [];
        var container = await _ownership!.StartContainerAsync(role, name, image, mounts.ToArray(), ports,
            anchor ? null : _anchorId, _useHistoricalServerUser, args, ct, environment, _privateValues);
        if (anchor) _anchorId = container.Id;
        return container;
    }

    private OwnedMount Mount(MountDirectory directory, bool writable = false) =>
        new(directory.HostPath(RunDirectory), directory.ContainerDirectory, writable, false);
    private Task ObserveLifecycleAsync(string phase, CancellationToken ct) => LifecycleObservation?.Invoke(phase, ct) ?? Task.CompletedTask;

    private HttpClient Http()
    {
        if (_httpClient is not null) return _httpClient();
        var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        KeycloakPinnedCertificate.Configure(handler, PublicCertificate.HostPath(RunDirectory));
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
    }
    private static bool IsSupportedIssuer(AppSurfaceKeycloakOptions options) =>
        options.Authority is null || IsLocalIssuer(options, options.Authority) || IsSupportedPublicIssuer(options);

    private static bool IsLocalIssuer(AppSurfaceKeycloakOptions options, Uri issuer) =>
        issuer.IsAbsoluteUri && issuer.Scheme == Uri.UriSchemeHttps
        && (issuer.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || issuer.Host.Equals("127.0.0.1", StringComparison.Ordinal))
        && issuer.Port == options.KeycloakPort
        && issuer.AbsolutePath == "/realms/" + options.Realm
        && issuer.UserInfo.Length == 0 && issuer.Query.Length == 0 && issuer.Fragment.Length == 0;

    private static bool IsSupportedPublicIssuer(AppSurfaceKeycloakOptions options)
    {
        if (options.Authority is not { } issuer || !issuer.IsAbsoluteUri
            || issuer.Scheme != Uri.UriSchemeHttps || issuer.IsLoopback
            || !issuer.IsDefaultPort || issuer.UserInfo.Length != 0 || issuer.Query.Length != 0 || issuer.Fragment.Length != 0)
            return false;

        return issuer.AbsolutePath == "/realms/" + options.Realm
            && IsApprovedOrigin(options, issuer.GetLeftPart(UriPartial.Authority));
    }

    /// <summary>Validates the optional web origin against both callback declarations before ownership acquisition.</summary>
    internal static void ValidatePublicWebOrigin(AppSurfaceKeycloakOptions options, IReadOnlyDictionary<string, string> inputs)
    {
        var hasPublicIssuer = IsSupportedPublicIssuer(options);
        if (!inputs.TryGetValue("PublicWebOrigin", out var webOrigin))
        {
            if (hasPublicIssuer) throw AppSurfaceKeycloakCredentialBindings.Invalid();
            return;
        }

        if (!hasPublicIssuer || !IsApprovedOrigin(options, webOrigin)
            || !options.RedirectUris.Any(uri => uri.AbsoluteUri == webOrigin + options.CallbackPath)
            || !options.PostLogoutRedirectUris.Any(uri => uri.AbsoluteUri == webOrigin + options.SignedOutCallbackPath))
            throw AppSurfaceKeycloakCredentialBindings.Invalid();
    }

    private static bool IsApprovedOrigin(AppSurfaceKeycloakOptions options, string value) =>
        options.ApprovedHttpsOrigins.Any(origin => origin.IsAbsoluteUri && origin.Scheme == Uri.UriSchemeHttps
            && origin.IsDefaultPort && origin.AbsolutePath == "/" && origin.UserInfo.Length == 0
            && origin.Query.Length == 0 && origin.Fragment.Length == 0
            && origin.GetLeftPart(UriPartial.Authority) == value);

    private AppSurfaceKeycloakException SafeException() => ExecutionFailure(DiagnosticCode);
    private static AppSurfaceKeycloakException ExecutionFailure(string code) => new(code, "Local supervised Keycloak execution could not establish its required ownership/readiness proof. Inspect the safe code and expected-owner record; use guarded recovery only after stopped verification. Docs: docs/supervised-keycloak-execution.md.");
    private static AppSurfaceKeycloakException LegacyVolumeUnavailable() => new(OwnershipUnavailable,
        "The exact Aspire-generated Keycloak volume is unavailable. Set PersistentVolumeName to the existing volume, or explicitly name a new volume when creation is intended. Docs: docs/supervised-keycloak-execution.md#legacy-volume-migration.");
}
