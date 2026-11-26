using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using ForgeTrust.AppSurface.Auth.Keycloak;
using ForgeTrust.AppSurface.Core;
using static ForgeTrust.AppSurface.Auth.Aspire.Keycloak.SupervisedKeycloakRunLayout;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

/// <summary>Writes the local payload, certificates, and protected inputs for an already reserved run.</summary>
/// <remarks>The coordinator owns the lease, checks volume ownership before credential resolution, and removes inputs
/// only after verifying stopped workloads. This component performs neither container operations nor cleanup.</remarks>
internal sealed class SupervisedKeycloakRunStager(
    string runDirectory, string ownerId, AppSurfaceKeycloakRegistrationSnapshot registration,
    Func<AppSurfaceKeycloakException> failure)
{
    private QualifiedLeaseDirectory.FileId? _runDirectoryId;
    private bool _runDirectoryCollision;

    /// <summary>Creates private staging and copies the packaged binary closure needed for the volume observers.</summary>
    [SupportedOSPlatform("macos")]
    internal void StageWorker(string runRoot, string workerSource)
    {
        Directory.CreateDirectory(runRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _runDirectoryId = QualifiedLeaseDirectory.TryCreatePrivateRunDirectory(runRoot, ownerId);
        if (_runDirectoryId is null)
        {
            _runDirectoryCollision = true;
            throw failure();
        }
        foreach (var directory in new[] { Worker, State, Public, EmptyNuGet, AnchorPrivate, ServerPrivate, WorkerPrivate })
        {
            Directory.CreateDirectory(directory.HostPath(runDirectory),
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        CopyPayload(workerSource, Worker.HostPath(runDirectory));
    }

    /// <summary>Verifies the exact run directory before cleanup.</summary>
    /// <returns>False when exclusive creation definitively found a pre-existing path and wrote no inputs.</returns>
    /// <exception cref="AppSurfaceKeycloakException">Acquisition is unresolved, or the acquired directory changed identity.</exception>
    internal bool HasVerifiedRunDirectoryForCleanup()
    {
        if (_runDirectoryCollision) return false;
        if (_runDirectoryId is not { } expected || QualifiedLeaseDirectory.ReadPrivateDirectoryId(runDirectory) != expected)
            throw failure();
        return true;
    }

    /// <summary>Resolves each typed parameter once, then stages worker, server, and workload inputs under ownership.</summary>
    /// <returns>Resolved private values for checking subsequent Docker arguments and environment.</returns>
    [SupportedOSPlatform("macos")]
    internal async Task<IReadOnlyList<string>> StageInputsAsync(
        AppSurfaceKeycloakCredentialSnapshot bindings, IEnumerable<AppSurfaceKeycloakSupervisedWorkload> workloadDefinitions,
        IReadOnlyDictionary<string, string> publicInputs, bool historicalServerUser, CancellationToken ct)
    {
        var options = registration.Options;
        var workloads = workloadDefinitions.ToArray();
        var values = new Dictionary<ParameterResource, string>(ReferenceEqualityComparer.Instance);
        async Task<string> Value(IResourceBuilder<ParameterResource> parameter)
        {
            if (values.TryGetValue(parameter.Resource, out var cached)) return cached;
            var value = await parameter.Resource.GetValueAsync(ct);
            if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(char.IsControl)) throw failure();
            values.Add(parameter.Resource, value);
            return value;
        }
        var user = await Value(bindings.AdminUsername);
        var password = await Value(bindings.AdminPassword);
        var client = bindings.ClientSecret is { } clientBinding ? await Value(clientBinding) : null;
        AppSurfaceKeycloakPortPreflight.ThrowIfOccupied(options.KeycloakPort, nameof(options.KeycloakPort));
        AppSurfaceKeycloakPortPreflight.ThrowIfOccupied(options.WebProofPort, nameof(options.WebProofPort));
        var desired = registration.Desired.ToReconcileDesired();
        WritePrivate(Desired, desired);
        CreateCertificate();
        if (client is not null)
            WritePrivate(AnchorInput, new { RunId = ownerId, Authority = desired.Authority, ClientId = desired.ClientId, ClientSecret = client });
        var serverConfig = $"bootstrap-admin-username={user}\nbootstrap-admin-password={password}\n";
        WritePrivateBytes(Bootstrap, Encoding.UTF8.GetBytes(serverConfig));
        if (historicalServerUser)
            PrepareHistoricalServerMount();
        var providers = ImmutableDictionary.CreateBuilder<string, string>();
        foreach (var (alias, binding) in bindings.IdentityProviderSecrets)
            providers.Add(alias, await Value(binding));

        // Check public inputs against every resolved secret, including seed-only parameters.
        foreach (var workload in workloads)
        {
            foreach (var parameter in workload.PrivateInputs.Values)
            {
                await Value(parameter);
            }
        }
        WritePrivate(WorkerBindings, new KeycloakWorkerBindings(
            $"https://localhost:{options.KeycloakPort}/", user, password,
            ClientSecret: null, CaptureRoot: null, CaptureRunId: null, BrokerSecrets: providers.ToImmutable())
        {
            ClientCredentialReferenceFile = client is null ? null : ClientReference.ContainerPath,
            ExpectedCaptureRunId = client is null ? null : ownerId
        });
        WritePublicInputs(desired, client is null ? null : ClientReference.ContainerPath, publicInputs,
            values.Where(binding => binding.Key.Secret).Select(binding => binding.Value));
        foreach (var workload in workloads)
        {
            var target = WorkloadPayload(workload.Name).HostPath(runDirectory);
            Directory.CreateDirectory(target);
            CopyPayload(workload.PayloadDirectory, target);
            if (!File.Exists(PayloadPathUnder(target, workload.AssemblyName))) throw failure();
            var pure = PathUtils.PathUnder(target, WorkerCoreAssembly.Name);
            if (File.Exists(pure) && !File.ReadAllBytes(pure).AsSpan().SequenceEqual(
                    File.ReadAllBytes(WorkerCoreAssembly.HostPath(runDirectory))))
                throw failure();

            var privateMount = WorkloadPrivate(workload.Name);
            Directory.CreateDirectory(privateMount.HostPath(runDirectory),
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var inputs = new Dictionary<string, string>();
            foreach (var (name, binding) in workload.PrivateInputs) inputs.Add(name, await Value(binding));
            WritePrivate(new MountedFile(privateMount, "input.json"), inputs);
        }

        return values.Values.ToArray();
    }

    private void WritePublicInputs(RealmReconcileDesired desired, string? referencePath,
        IReadOnlyDictionary<string, string> publicInputs, IEnumerable<string> secrets)
    {
        var options = registration.Options;
        var config = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Authority"] = desired.Authority,
            ["AdminAuthority"] = $"https://localhost:{options.KeycloakPort}/realms/{desired.Realm}",
            ["ClientId"] = desired.ClientId,
            ["Realm"] = desired.Realm,
            ["ExpectedRunId"] = ownerId,
            ["KeycloakHost"] = "localhost",
            ["KeycloakPort"] = options.KeycloakPort,
            ["CertificatePath"] = PublicCertificate.ContainerPath,
            ["ClientMode"] = options.ClientMode.ToString(),
            ["ReferencePath"] = referencePath,
            ["WebBaseAddress"] = $"http://localhost:{options.WebProofPort}",
            ["ListenUrl"] = $"http://0.0.0.0:{options.WebProofPort}",
            ["SeedReceiptPath"] = SeedReceipt.ContainerPath,
            ["WorkerReceiptPath"] = WorkerReceipt.ContainerPath,
            ["CallbackPath"] = options.CallbackPath,
            ["SignedOutCallbackPath"] = options.SignedOutCallbackPath,
        };
        EnsurePublicInputsSafe(publicInputs, secrets);
        foreach (var (name, value) in publicInputs)
        {
            if (!config.TryAdd(name, value)) throw failure();
        }
        WritePrivate(PublicConfig, config);
    }

    /// <summary>Stages a read-only, Keycloak-only input mount behind the private host run directory.</summary>
    /// <remarks>UID 1000 uses the mount directly, without acquiring access to the worker, seed, or host directories.
    /// The mounted files must be readable by that distinct UID. The host parent must remain owner-only.</remarks>
    private void PrepareHistoricalServerMount()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        QualifiedLeaseDirectory.ValidatePrivateDirectory(runDirectory);
        WritePrivateBytes(HistoricalCertificate, File.ReadAllBytes(PublicCertificate.HostPath(runDirectory)));
        foreach (var file in new[] { Bootstrap, ServerKey, HistoricalCertificate })
            File.SetUnixFileMode(file.HostPath(runDirectory), UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        File.SetUnixFileMode(ServerPrivate.HostPath(runDirectory), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }

    private void CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(System.Net.IPAddress.Loopback);
        if (registration.Options.Authority is { IsLoopback: false } issuer) san.AddDnsName(issuer.IdnHost);
        request.CertificateExtensions.Add(san.Build());
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        WritePrivateBytes(PublicCertificate, Encoding.UTF8.GetBytes(cert.ExportCertificatePem()));
        WritePrivateBytes(ServerKey, Encoding.UTF8.GetBytes(key.ExportPkcs8PrivateKeyPem()));
    }

    private void WritePrivate(MountedFile file, object value) => WritePrivateBytes(file, JsonSerializer.SerializeToUtf8Bytes(value));
    private void WritePrivateBytes(MountedFile file, byte[] bytes)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        var path = file.HostPath(runDirectory);
        using (var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        }))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.SetUnixFileMode(path, UnixFileMode.UserRead);
    }
    /// <summary>Rejects declared public consumer metadata containing a resolved authentication credential.</summary>
    /// <remarks>Call under ownership after administrator/client/provider resolution and before publishing public input. Consumer account semantics remain consumer-owned.</remarks>
    internal static void EnsurePublicInputsSafe(IReadOnlyDictionary<string, string> values, IEnumerable<string> authenticationSecrets)
    {
        foreach (var secret in authenticationSecrets)
        {
            if (string.IsNullOrEmpty(secret) || values.Any(p => p.Key.Contains(secret, StringComparison.Ordinal) || p.Value.Contains(secret, StringComparison.Ordinal)))
                throw AppSurfaceKeycloakCredentialBindings.Invalid();
        }
    }

    /// <summary>Copies a bounded payload into freshly staged storage, preserving native names and refusing links and existing targets.</summary>
    internal static void CopyPayload(string source, string destination)
    {
        if (new DirectoryInfo(source).LinkTarget is not null) throw new IOException();
        var pending = new Stack<string>();
        pending.Push(source);
        var files = new List<string>();
        var entries = 0;
        while (pending.TryPop(out var directory))
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++entries > 4096) throw new IOException();
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) throw new IOException();
                if (Directory.Exists(child)) pending.Push(child);
                else files.Add(child);
            }
        }
        if (files.Count == 0) throw new IOException();
        foreach (var file in files)
        {
            if (new FileInfo(file).LinkTarget is not null) throw new IOException();
            var target = PayloadPathUnder(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private static string PayloadPathUnder(string directory, string relativeName)
    {
        // Payload names come from the host filesystem or the validated assembly filename. PathUnder intentionally
        // rejects Windows-looking syntax even on Unix, where paths such as "a:b.dll" are ordinary filenames.
        var root = Path.GetFullPath(directory);
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(relativeName, root);
        if (path.Length <= prefix.Length || !path.StartsWith(prefix, StringComparison.Ordinal))
            throw new IOException();
        return path;
    }
}
