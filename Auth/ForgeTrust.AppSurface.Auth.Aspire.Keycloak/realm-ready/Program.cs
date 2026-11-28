using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using ForgeTrust.AppSurface.Auth.Keycloak;
using RealmReadyEnvironment = ForgeTrust.AppSurface.Auth.Keycloak.KeycloakWorkerProtocol.RealmReadyEnvironment;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

internal static class AppSurfaceKeycloakRealmReadyEntryPoint
{
    private static readonly TimeSpan LegacyReadinessTimeout = TimeSpan.FromSeconds(120);

    private static Task<int> Main(string[] args)
        => RunAsync(args, Environment.GetEnvironmentVariable, Console.Out, Console.Error, CancellationToken.None);

    /// <summary>Runs one worker, namespace fence, or volume metadata command and returns its process exit status.</summary>
    /// <param name="args">The exact mode and mode-specific file or network arguments.</param>
    /// <param name="getEnvironmentVariable">The process environment reader for baseline and pinned TLS inputs.</param>
    /// <param name="standardOutput">The writer used for JSON volume metadata.</param>
    /// <param name="standardError">The writer used for safe failure and completion messages.</param>
    /// <param name="cancellationToken">An additional cancellation source for non-process callers.</param>
    /// <param name="checkBaseline">Optional deterministic baseline verifier for dispatcher tests.</param>
    /// <param name="reconcile">Optional deterministic reconciliation runner for dispatcher tests.</param>
    /// <param name="volumeRoot">The fixed mount in the worker process or a private Linux test directory.
    /// Command arguments and the process environment never select this path.</param>
    /// <returns>A mode-specific exit code. Zero indicates a completed operation.</returns>
    internal static async Task<int> RunAsync(
        string[] args,
        Func<string, string?> getEnvironmentVariable,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken,
        Func<HttpClient, RealmBaselineDesired, Func<string, string>, CancellationToken, Task>? checkBaseline = null,
        Func<HttpClient, RealmReconcileDesired, RealmReconcileCredentials, CancellationToken, Task<RealmReconcileResult>>? reconcile = null,
        string volumeRoot = KeycloakVolumeIncarnation.VolumeRoot)
    {
        var volumeMetadataResult = RunVolumeMetadataCommand(
            args, volumeRoot, standardOutput, standardError,
            KeycloakVolumeIncarnation.Read,
            KeycloakVolumeIncarnation.ReadOwnership,
            KeycloakVolumeIncarnation.ReadPinnedDatabaseOwnership);
        if (volumeMetadataResult is { } volumeExitCode) return volumeExitCode;

        if (args is [KeycloakWorkerProtocol.InitializeVolume, var uidText])
            return RunInitializeVolumeCommand(volumeRoot, uidText, standardError, InitializeNativeVolume);
        var boundaryStage = "configuration";
        (Func<CancellationToken, Task<int>> Execute, TimeSpan? Timeout, string? ResultPath)? command = args switch
        {
            [KeycloakWorkerProtocol.Reconcile, var desired, var bindings, var result]
                => (token => RunRealmAsync(token, (desired, bindings, result)), KeycloakWorkerProtocol.ReconciliationTimeout, result),
            [KeycloakWorkerProtocol.FencedMemoryAnchor, var input, var ready, var port, var owner]
                => (token => HoldAnchorAsync(input, ready, token, ParsePort(port), owner, standardError), null, null),
            [KeycloakWorkerProtocol.OwnerFence, var ready, var port, var owner]
                => (token => HoldAnchorAsync(null, ready, token, ParsePort(port), owner, standardError), null, null),
            ["--appsurface-keycloak-memory-anchor", var input, var ready]
                => (token => HoldAnchorAsync(input, ready, token, null, null, standardError), null, null),
            [KeycloakWorkerProtocol.RealmReady] => (token => RunRealmAsync(token), LegacyReadinessTimeout, null),
            _ => null
        };
        if (command is not { } selected) return 1;

        var execution = Stopwatch.StartNew();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (selected.Timeout is { } timeout) stop.CancelAfter(timeout);
        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; stop.Cancel(); });
        ConsoleCancelEventHandler interrupt = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += interrupt;
        try
        {
            return await selected.Execute(stop.Token);
        }
        catch (OperationCanceledException)
        {
            standardError.WriteLine("AppSurface Keycloak realm-ready gate was cancelled.");
            return 124;
        }
        catch (RealmBaselineException error)
        {
            standardError.WriteLine("AppSurface Keycloak realm-ready gate failed. Code: " + error.Code + ".");
            return 1;
        }
        catch (Exception error)
        {
            if (selected.ResultPath is { } resultPath)
            {
                var failureReceipt = JsonSerializer.Serialize(new
                {
                    code = "WorkerBoundaryFailure",
                    stage = boundaryStage,
                    type = error.GetType().Name,
                    elapsedSeconds = execution.Elapsed.TotalSeconds
                });
                try
                {
                    File.WriteAllText(resultPath, failureReceipt);
                }
                catch (Exception publicationError) when (publicationError is IOException or UnauthorizedAccessException
                    or ArgumentException or NotSupportedException or System.Security.SecurityException)
                {
                    // Keep the worker's safe failure status when its receipt path is unavailable.
                }
            }
            var code = selected.ResultPath is not null ? "WorkerBoundaryFailure" : KeycloakBaselineDiagnosticCodes.InvalidOptions;
            standardError.WriteLine($"AppSurface Keycloak realm-ready gate failed. Code: {code}.");
            return selected.ResultPath is not null ? 70 : 1;
        }
        finally { Console.CancelKeyPress -= interrupt; }

        static int ParsePort(string value) => int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

        async Task<int> RunRealmAsync(CancellationToken token,
            (string DesiredPath, string BindingsPath, string ResultPath)? request = null)
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false };
            // Historical internal variable name: this qualified seam pins the exact peer certificate, not a CA chain.
            var publicCa = getEnvironmentVariable(KeycloakWorkerProtocol.CertificateFileEnvironmentVariable);
            if (!string.IsNullOrEmpty(publicCa))
            {
                KeycloakPinnedCertificate.Configure(handler, publicCa);
            }
            using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            if (request is not { } inputs)
            {
                var baseline = Baseline(getEnvironmentVariable);
                await (checkBaseline ?? RealmBaselineVerifier.CheckOnceAsync)(http, baseline, File.ReadAllText, token);
                standardError.WriteLine("AppSurface Keycloak realm-ready gate completed.");
                return 0;
            }
            var (desiredPath, bindingsPath, resultPath) = inputs;
            VerifyReceiptDirectoryWritable(resultPath);
            if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            var privateFile = new FileInfo(bindingsPath);
            if (!privateFile.Exists || privateFile.LinkTarget is not null || privateFile.Length > 16 * 1024
                || (File.GetUnixFileMode(bindingsPath) & (UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite
                    | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                throw new InvalidDataException();
            if (new FileInfo(desiredPath).Length > 1024 * 1024) throw new InvalidDataException();
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var desired = JsonSerializer.Deserialize<RealmReconcileDesired>(File.ReadAllBytes(desiredPath), options) ?? throw new InvalidDataException();
            var bindings = JsonSerializer.Deserialize<KeycloakWorkerBindings>(File.ReadAllBytes(bindingsPath), options) ?? throw new InvalidDataException();
            http.BaseAddress = new Uri(bindings.BaseAddress);
            if ((bindings.CaptureRoot is null) != (bindings.CaptureRunId is null)) throw new InvalidDataException();
            boundaryStage = "client-capture";
            var clientSecret = ResolveClientCredential(desired, bindings);
            boundaryStage = "reconciliation";
            var clock = Stopwatch.StartNew();
            var result = await (reconcile ?? RealmReconciler.ReconcileAsync)(http, desired,
                new RealmReconcileCredentials(bindings.AdminUsername, bindings.AdminPassword, clientSecret) { BrokerSecrets = bindings.BrokerSecrets }, token);
            var envelope = new
            {
                result,
                elapsedSeconds = clock.Elapsed.TotalSeconds,
                assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic)
                    .Select(a => new { name = a.GetName().Name, location = a.Location }).ToArray(),
                workerReferences = typeof(AppSurfaceKeycloakRealmReadyEntryPoint).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToArray(),
                pureReferences = typeof(KeycloakSecurity).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToArray()
            };
            File.WriteAllText(resultPath, JsonSerializer.Serialize(envelope));
            standardError.WriteLine("AppSurface Keycloak reconciliation: " + result.Code + "/" + result.Stage + ".");
            return result.Ready ? 0 : 20;
        }
    }

    /// <summary>Resolves exactly one credential for a confidential client, or none for a public client.</summary>
    private static string? ResolveClientCredential(RealmReconcileDesired desired, KeycloakWorkerBindings bindings)
    {
        return (desired.Confidential, bindings.ClientSecret, bindings.CaptureRoot, bindings.CaptureRunId,
            bindings.ClientCredentialReference, bindings.ClientCredentialReferenceFile) switch
        {
            (false, null, null, null, null, null) => null,
            (true, { } secret, null, null, null, null) => secret,
            (true, null, { } root, { } runId, null, null) => RealmClientCapture.Read(root, runId),
            (true, null, null, null, { } reference, null) => ReadVerifiedReference(reference),
            (true, null, null, null, null, { } referenceFile) => ReadVerifiedReference(LoadReference(referenceFile)),
            _ => throw new InvalidDataException()
        };

        string ReadVerifiedReference(KeycloakClientCredentialReference reference)
        {
            if (reference.MemoryIdentity is null || reference.Authority.AbsoluteUri != new Uri(desired.Authority).AbsoluteUri
                || reference.ClientId != desired.ClientId || reference.RunId != bindings.ExpectedCaptureRunId)
                throw new InvalidDataException();
            return reference.Read();
        }

        static KeycloakClientCredentialReference LoadReference(string path)
        {
            var metadata = new FileInfo(path);
            if (!metadata.Exists || metadata.LinkTarget is not null || metadata.Length is < 1 or > 16 * 1024)
                throw new InvalidDataException();
            return JsonSerializer.Deserialize<KeycloakClientCredentialReference>(File.ReadAllBytes(path))
                ?? throw new InvalidDataException();
        }
    }

    /// <summary>Runs a volume metadata command while keeping native reads behind injectable, guarded readers.</summary>
    /// <param name="args">The exact worker command arguments.</param>
    /// <param name="volumeRoot">The fixed mounted volume root.</param>
    /// <param name="standardOutput">The JSON result writer.</param>
    /// <param name="standardError">The safe failure writer.</param>
    /// <param name="readIncarnation">The root incarnation reader, Linux ARM64 guarded in production.</param>
    /// <param name="readOwnership">The root incarnation and UID/GID reader, Linux ARM64 guarded in production.</param>
    /// <param name="readPinnedDatabaseOwnership">The no-follow pinned H2 owner reader, Linux ARM64 guarded in production.</param>
    /// <returns>The command exit code, or null when the arguments select another worker mode.</returns>
    internal static int? RunVolumeMetadataCommand(
        string[] args,
        string volumeRoot,
        TextWriter standardOutput,
        TextWriter standardError,
        Func<string, KeycloakVolumeIncarnation> readIncarnation,
        Func<string, (KeycloakVolumeIncarnation Incarnation, uint Uid, uint Gid)> readOwnership,
        Func<string, (uint Uid, uint Gid)?> readPinnedDatabaseOwnership)
    {
        if (args is [KeycloakWorkerProtocol.VolumeIncarnation])
        {
            try { standardOutput.WriteLine(JsonSerializer.Serialize(readIncarnation(volumeRoot))); return 0; }
            catch { standardError.WriteLine("AppSurface Keycloak volume identity unavailable."); return 1; }
        }
        if (args is [KeycloakWorkerProtocol.VolumeOwnership])
        {
            try
            {
                var (incarnation, uid, gid) = readOwnership(volumeRoot);
                var database = readPinnedDatabaseOwnership(volumeRoot);
                standardOutput.WriteLine(JsonSerializer.Serialize(new KeycloakWorkerVolumeOwnership(
                    incarnation, uid, gid, database is { } owner
                        ? new KeycloakWorkerDatabaseOwnership(owner.Uid, owner.Gid)
                        : null)));
                return 0;
            }
            catch { standardError.WriteLine("AppSurface Keycloak volume ownership unavailable."); return 1; }
        }
        return null;
    }

    /// <summary>Validates the requested UID and reports initialization failures without exposing native details.</summary>
    /// <param name="volumeRoot">The fixed mounted volume root.</param>
    /// <param name="uidText">The requested nonzero numeric UID.</param>
    /// <param name="standardError">The safe failure writer.</param>
    /// <param name="initializeVolume">The guarded volume initializer.</param>
    /// <returns>The initializer's exit status, or one when the UID or operation is invalid.</returns>
    internal static int RunInitializeVolumeCommand(string volumeRoot, string uidText, TextWriter standardError,
        Func<string, uint, int> initializeVolume)
    {
        if (!uint.TryParse(uidText, out var uid) || uid == 0) return 1;
        try { return initializeVolume(volumeRoot, uid); }
        catch { standardError.WriteLine("AppSurface Keycloak new-volume initialization failed."); return 1; }
    }

    private static int InitializeNativeVolume(string volumeRoot, uint uid)
    {
        if (!OperatingSystem.IsLinux()) return 1;
        return InitializeVolumeCore(volumeRoot, uid,
            root => Directory.EnumerateFileSystemEntries(root).Any(),
            KeycloakVolumeIncarnation.Read,
            chown,
            File.SetUnixFileMode);
    }

    /// <summary>Initializes an empty volume in place and verifies that its root identity survived the update.</summary>
    /// <remarks>
    /// Production callers must apply the Linux and nonzero UID guards first. The identity reader is called before
    /// the ordered chown and mode update, then again afterward; production readers enforce Linux ARM64 before chown.
    /// </remarks>
    /// <param name="volumeRoot">The fixed mounted volume root.</param>
    /// <param name="uid">The validated nonzero UID assigned to the new volume.</param>
    /// <param name="hasEntries">Checks whether any entry already exists in the root.</param>
    /// <param name="readIncarnation">Reads the root incarnation, including the production Linux ARM64 guard.</param>
    /// <param name="changeOwner">Changes the root UID and root GID and returns the native result.</param>
    /// <param name="setUnixFileMode">Sets the required owner/group access after ownership succeeds.</param>
    /// <returns>Zero only when the root remains the same volume incarnation.</returns>
    internal static int InitializeVolumeCore(
        string volumeRoot,
        uint uid,
        Func<string, bool> hasEntries,
        Func<string, KeycloakVolumeIncarnation> readIncarnation,
        Func<string, uint, uint, int> changeOwner,
        Action<string, UnixFileMode> setUnixFileMode)
    {
        if (hasEntries(volumeRoot)) return 1;
        var before = readIncarnation(volumeRoot);
        if (changeOwner(volumeRoot, uid, 0) != 0) return 1;
        setUnixFileMode(volumeRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute);
        return before == readIncarnation(volumeRoot) ? 0 : 1;
    }

    /// <summary>Holds the owner fence and, for a confidential client, the same-run sealed capture.</summary>
    /// <remarks>A null input path selects a credential-free fence. In that mode only the listening marker is written.</remarks>
    private static Task<int> HoldAnchorAsync(string? inputPath, string readyPath, CancellationToken token, int? ownerPort,
        string? expectedOwner, TextWriter standardError)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
            throw new PlatformNotSupportedException();

        return HoldAnchorCoreAsync(inputPath, readyPath, token, ownerPort, expectedOwner, CreateNativeAnchorCapture, standardError);
    }

    /// <summary>Coordinates anchor capture custody and metadata publication without invoking native capture operations.</summary>
    /// <param name="inputPath">The private same-run declaration path, or null for a fence without credentials.</param>
    /// <param name="readyPath">The path for the metadata-only reference and listening marker.</param>
    /// <param name="token">Cancels publication or the unfenced idle lifetime.</param>
    /// <param name="ownerPort">The local heartbeat port, or null for an unfenced worker.</param>
    /// <param name="expectedOwner">The required owner identifier for a fenced worker.</param>
    /// <param name="createCapture">Creates disposable custody and returns its metadata-only reference.</param>
    /// <param name="standardError">The safe publication and owner-loss diagnostic writer.</param>
    /// <param name="createInitialFenceDeadline">Optional deadline factory forwarded to the owner fence.</param>
    /// <param name="exitProcess">Optional process exit callback forwarded to the owner fence.</param>
    /// <returns>The worker's owner-fence or cancellation exit code.</returns>
    internal static async Task<int> HoldAnchorCoreAsync(
        string? inputPath,
        string readyPath,
        CancellationToken token,
        int? ownerPort,
        string? expectedOwner,
        Func<KeycloakMemoryAnchorInput, (IDisposable Custody, KeycloakClientCredentialReference Reference)> createCapture,
        TextWriter standardError,
        Func<Action, IDisposable>? createInitialFenceDeadline = null,
        Action<int>? exitProcess = null)
    {
        (IDisposable Custody, KeycloakClientCredentialReference Reference)? custody = null;
        try
        {
            if (ownerPort is null)
            {
                await PrepareCaptureAsync(token);

                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                return 0;
            }

            return await RunOwnerFenceAsync(readyPath, ownerPort.Value, expectedOwner!, token, standardError, PrepareCaptureAsync,
                createInitialFenceDeadline, exitProcess);
        }
        finally { custody?.Custody.Dispose(); }

        async Task PrepareCaptureAsync(CancellationToken captureToken)
        {
            if (inputPath is null) return;
            var declaration = ReadAnchorDeclaration(inputPath, expectedOwner);
            custody = createCapture(declaration);
            await WriteAnchorReferenceAsync(readyPath, custody.Value.Reference, captureToken, standardError);
        }
    }

    /// <summary>Coordinates one task-scoped owner connection without invoking native capture operations.</summary>
    /// <remarks>
    /// Production worker modes reach this method through <see cref="HoldAnchorAsync"/>, which enforces Linux ARM64.
    /// The initial owner fence retains its fixed twelve-second deadline. A test can supply deterministic deadline
    /// and process-exit callbacks while exercising the real localhost heartbeat protocol.
    /// </remarks>
    /// <param name="readyPath">The worker's ready-reference path, used to publish the listening marker.</param>
    /// <param name="ownerPort">The local TCP port reserved for the owner heartbeat.</param>
    /// <param name="expectedOwner">The GUID-N owner identifier required by every heartbeat frame.</param>
    /// <param name="cancellationToken">Cancels acceptance, preparation, acknowledgement, or monitoring.</param>
    /// <param name="standardError">The safe owner-loss diagnostic writer.</param>
    /// <param name="prepareBeforeAcknowledgement">Optional same-run capture and reference publication performed after owner acceptance.</param>
    /// <param name="createInitialFenceDeadline">Optional deadline factory for deterministic tests. Production uses the fixed twelve-second timer.</param>
    /// <param name="exitProcess">Optional process exit action. Production exits with status 71 if the initial fence expires.</param>
    /// <returns>71 when the accepted owner connection is lost or the initial fence expires.</returns>
    internal static async Task<int> RunOwnerFenceAsync(
        string readyPath,
        int ownerPort,
        string expectedOwner,
        CancellationToken cancellationToken,
        TextWriter standardError,
        Func<CancellationToken, Task>? prepareBeforeAcknowledgement = null,
        Func<Action, IDisposable>? createInitialFenceDeadline = null,
        Action<int>? exitProcess = null)
    {
        using var ownerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var initialFenceState = 0;
        void ExpireInitialFence()
        {
            if (Interlocked.CompareExchange(ref initialFenceState, 1, 0) != 0) return;
            if (exitProcess is null)
            {
                Environment.Exit(71);
                return;
            }

            exitProcess(71);
            ownerCancellation.Cancel();
        }

        using var initialFence = (createInitialFenceDeadline ?? StartInitialFenceDeadline)(ExpireInitialFence);
        try
        {
            await using var heartbeat = await KeycloakOwnerHeartbeat.AcceptAsync(ownerPort, expectedOwner, ownerCancellation.Token,
                () => File.WriteAllText(KeycloakWorkerProtocol.ListeningMarkerPath(readyPath), expectedOwner));
            if (prepareBeforeAcknowledgement is not null)
                await prepareBeforeAcknowledgement(ownerCancellation.Token);
            if (Interlocked.CompareExchange(ref initialFenceState, 2, 0) != 0) throw new TimeoutException();
            await heartbeat.AcknowledgeAsync(ownerCancellation.Token);
            await heartbeat.WaitForLossAsync(ownerCancellation.Token);
            standardError.WriteLine("AppSurface Keycloak owner connection lost; namespace fencing.");
            return 71;
        }
        catch (OperationCanceledException) when (Volatile.Read(ref initialFenceState) == 1)
        {
            return 71;
        }
    }

    private static IDisposable StartInitialFenceDeadline(Action onExpired)
    {
        // A blocked initial capture/publication cannot keep an unacknowledged namespace alive indefinitely.
        // Exit, rather than FailFast, avoids requesting a diagnostic dump of private memory.
        return new Timer(_ => onExpired(), null, TimeSpan.FromSeconds(12), Timeout.InfiniteTimeSpan);
    }

    /// <summary>Reads and validates the bounded Unix anchor declaration without creating native credential custody.</summary>
    /// <param name="inputPath">The declaration path, which must be nonlinked and owner-only.</param>
    /// <param name="expectedOwner">The required run identifier, or null when the declaration is not owner-fenced.</param>
    /// <returns>The in-process declaration used immediately by the Linux native capture path.</returns>
    /// <exception cref="InvalidDataException">The file is linked, has unsafe permissions or bounds, or names another owner.</exception>
    /// <exception cref="JsonException">The declaration JSON is malformed.</exception>
    /// <exception cref="PlatformNotSupportedException">The current platform does not provide Unix file permissions.</exception>
    internal static KeycloakMemoryAnchorInput ReadAnchorDeclaration(string inputPath, string? expectedOwner)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();

        var input = new FileInfo(inputPath);
        if (!input.Exists || input.LinkTarget is not null || input.Length is < 1 or > 16 * 1024
            || (File.GetUnixFileMode(inputPath) & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite)) != 0)
            throw new InvalidDataException();
        var declaration = JsonSerializer.Deserialize<KeycloakMemoryAnchorInput>(File.ReadAllBytes(inputPath)) ?? throw new InvalidDataException();
        if (expectedOwner is not null && declaration.RunId != expectedOwner) throw new InvalidDataException();
        return declaration;
    }

    [SupportedOSPlatform("linux")]
    private static (IDisposable, KeycloakClientCredentialReference) CreateNativeAnchorCapture(KeycloakMemoryAnchorInput declaration)
    {
        var capture = KeycloakClientCredentialMemoryCapture.CreateForOwnerLifetime(
            declaration.RunId, new Uri(declaration.Authority), declaration.ClientId, declaration.ClientSecret);
        return (capture, capture.Reference);
    }

    /// <summary>Publishes only sealed-capture metadata through a create-new, owner-readable Unix file.</summary>
    /// <param name="referencePath">The path reserved for the immutable ready reference.</param>
    /// <param name="reference">The metadata-only reference; it contains no client credential.</param>
    /// <param name="token">Cancels metadata serialization before publication completes.</param>
    /// <param name="standardError">The safe completion diagnostic writer.</param>
    /// <exception cref="IOException">The destination already exists or cannot be written.</exception>
    /// <exception cref="PlatformNotSupportedException">The current platform does not provide Unix file permissions.</exception>
    internal static async Task WriteAnchorReferenceAsync(
        string referencePath,
        KeycloakClientCredentialReference reference,
        CancellationToken token,
        TextWriter standardError)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();

        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        };
        using (var output = new FileStream(referencePath, options))
        {
            await JsonSerializer.SerializeAsync(output, reference, cancellationToken: token);
            output.Flush(flushToDisk: true);
        }
        File.SetUnixFileMode(referencePath, UnixFileMode.UserRead);
        standardError.WriteLine("AppSurface Keycloak sealed-memory anchor ready.");
    }

    /// <summary>Checks the receipt directory before reconciliation can update Keycloak.</summary>
    private static void VerifyReceiptDirectoryWritable(string resultPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(resultPath)) ?? throw new InvalidDataException();
        var probePath = Path.Combine(directory, ".receipt-probe-" + Guid.NewGuid().ToString("N"));
        using var probe = File.OpenHandle(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, FileOptions.DeleteOnClose);
    }

    /// <summary>Reads the legacy nonsecret environment contract before contacting Keycloak.</summary>
    private static RealmBaselineDesired Baseline(Func<string, string?> getEnvironmentVariable)
    {
        string Required(string key)
        {
            var value = getEnvironmentVariable(key);
            return !string.IsNullOrWhiteSpace(value) ? value : throw new InvalidDataException();
        }

        var authority = new Uri(Required(RealmReadyEnvironment.Authority));
        var parts = authority.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (authority.Scheme != "https" || authority.Host is not ("localhost" or "127.0.0.1") || authority.UserInfo.Length != 0
            || authority.Query.Length != 0 || authority.Fragment.Length != 0 || parts.Length != 2 || parts[0] != "realms"
            || !parts[1].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')) throw new InvalidDataException();
        ImmutableArray<string> Uris(string key, string path)
        {
            var uris = JsonSerializer.Deserialize<string[]>(Required(key)) ?? throw new InvalidDataException();
            if (uris.Length is < 1 or > 32 || uris.Any(value => !Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https") || uri.Host is not ("localhost" or "127.0.0.1") || uri.AbsolutePath != path
                || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)) throw new InvalidDataException();
            return uris.ToImmutableArray();
        }
        var users = Required(RealmReadyEnvironment.SeededUserNames)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (users.Length == 0 || users.Distinct(StringComparer.Ordinal).Count() != users.Length) throw new InvalidDataException();
        var theme = getEnvironmentVariable(RealmReadyEnvironment.LoginThemeName);
        return new(authority.AbsoluteUri, Required(RealmReadyEnvironment.ClientId), parts[1],
            Uris(RealmReadyEnvironment.RedirectUris, Required(RealmReadyEnvironment.CallbackPath)),
            Uris(RealmReadyEnvironment.PostLogoutRedirectUris, Required(RealmReadyEnvironment.SignedOutCallbackPath)),
            users.ToImmutableArray(), Required(RealmReadyEnvironment.RealmImportDirectory),
            string.IsNullOrWhiteSpace(theme) ? null : theme);
    }

    [DllImport("libc", SetLastError = true)] private static extern int chown(string path, uint owner, uint group);
}
