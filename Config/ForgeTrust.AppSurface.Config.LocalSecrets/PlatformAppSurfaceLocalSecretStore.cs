using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config.LocalSecrets;

/// <summary>
/// LocalSecrets store that delegates to the current operating system's user secret facility when available.
/// </summary>
/// <remarks>
/// macOS uses Security.framework Keychain generic passwords, Windows uses current-user Credential Manager generic credentials,
/// Linux uses Secret Service through <c>secret-tool</c>, and unsupported sessions return display-safe diagnostics.
/// </remarks>
[ExcludeFromCodeCoverage(
    Justification = "Real OS credential stores depend on desktop session state, prompts, and native services; deterministic tests cover fake stores and status mapping.")]
public sealed partial class PlatformAppSurfaceLocalSecretStore : IAppSurfaceLocalSecretStore
{
    private static readonly string[] LinuxSecretToolTrustedCandidates = ["/usr/bin/secret-tool", "/bin/secret-tool"];

    private readonly IAppSurfaceLocalSecretStore _inner;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlatformAppSurfaceLocalSecretStore"/> class.
    /// </summary>
    public PlatformAppSurfaceLocalSecretStore()
        : this(new AppSurfaceLocalSecretsOptions())
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PlatformAppSurfaceLocalSecretStore"/> class.
    /// </summary>
    /// <param name="options">LocalSecrets options that may configure Linux platform-store resolution.</param>
    public PlatformAppSurfaceLocalSecretStore(IOptions<AppSurfaceLocalSecretsOptions> options)
        : this(options?.Value ?? throw new ArgumentNullException(nameof(options)))
    {
    }

    private PlatformAppSurfaceLocalSecretStore(AppSurfaceLocalSecretsOptions options)
    {
        _inner = CreateInnerStore(options, LinuxSecretToolResolver.Default);
    }

    /// <inheritdoc />
    public string Name => _inner.Name;

    /// <inheritdoc />
    public AppSurfaceLocalSecretResult Get(AppSurfaceLocalSecretIdentity identity) => _inner.Get(identity);

    /// <inheritdoc />
    public AppSurfaceLocalSecretResult Set(AppSurfaceLocalSecretIdentity identity, string value) => _inner.Set(identity, value);

    /// <inheritdoc />
    public AppSurfaceLocalSecretResult Delete(AppSurfaceLocalSecretIdentity identity) => _inner.Delete(identity);

    /// <inheritdoc />
    public AppSurfaceLocalSecretListResult List(string applicationName, string environment, string? keyPrefix) =>
        _inner.List(applicationName, environment, keyPrefix);

    /// <inheritdoc />
    public AppSurfaceLocalSecretResult Doctor(string applicationName, string environment, string? keyPrefix) =>
        _inner.Doctor(applicationName, environment, keyPrefix);

    private static IAppSurfaceLocalSecretStore CreateInnerStore(
        AppSurfaceLocalSecretsOptions options,
        LinuxSecretToolResolver linuxSecretToolResolver,
        LocalSecretsPlatform? platformOverride = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(linuxSecretToolResolver);

        if (platformOverride == LocalSecretsPlatform.Linux)
        {
            return CreateLinuxStore(options, linuxSecretToolResolver);
        }

        if (platformOverride == LocalSecretsPlatform.Unsupported)
        {
            return CreateUnsupportedStore();
        }

        if (options.LinuxSecretToolPath != null && !RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return DiagnosticLocalSecretStore.FromResolution(LinuxSecretToolResolver.UnsupportedPlatformOverride(options.LinuxSecretToolPath));
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return new MacOsKeychainLocalSecretStore();
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return CreateLinuxStore(options, linuxSecretToolResolver);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new WindowsCredentialManagerLocalSecretStore();
        }

        return CreateUnsupportedStore();
    }

    private static IAppSurfaceLocalSecretStore CreateLinuxStore(
        AppSurfaceLocalSecretsOptions options,
        LinuxSecretToolResolver linuxSecretToolResolver)
    {
        var secretTool = linuxSecretToolResolver.Resolve(options.LinuxSecretToolPath);
        return secretTool.Succeeded
            ? new LinuxSecretServiceLocalSecretStore(secretTool.Path!)
            : DiagnosticLocalSecretStore.FromResolution(secretTool);
    }

    private static IAppSurfaceLocalSecretStore CreateUnsupportedStore() =>
        DiagnosticLocalSecretStore.Unsupported(
            new AppSurfaceLocalSecretDiagnostic(
                "local-secret-store-unsupported",
                "Local secret store is unsupported in this session.",
                "This operating system does not have an AppSurface LocalSecrets adapter.",
                "Use environment variables or key-per-file in this session, or run on a supported desktop user session.",
                "local-secrets-platform-compatibility"));

    /// <summary>
    /// Creates the platform-specific inner store with deterministic platform selection for tests.
    /// </summary>
    /// <param name="options">LocalSecrets options used by platform resolution.</param>
    /// <param name="linuxSecretToolResolver">Linux resolver seam used when the selected platform is Linux.</param>
    /// <param name="platformOverride">Optional platform override; when unset, the current runtime platform is used.</param>
    /// <returns>The store selected for the requested platform and options.</returns>
    internal static IAppSurfaceLocalSecretStore CreateInnerStoreForTests(
        AppSurfaceLocalSecretsOptions options,
        LinuxSecretToolResolver linuxSecretToolResolver,
        LocalSecretsPlatform? platformOverride = null) =>
        CreateInnerStore(options, linuxSecretToolResolver, platformOverride);

    /// <summary>
    /// Represents platform selections used by deterministic platform-store tests.
    /// </summary>
    internal enum LocalSecretsPlatform
    {
        /// <summary>
        /// Linux Secret Service-backed local secret storage through <c>secret-tool</c>.
        /// </summary>
        Linux,

        /// <summary>
        /// A platform without a LocalSecrets platform adapter.
        /// </summary>
        Unsupported
    }

    /// <summary>
    /// Resolves the Linux <c>secret-tool</c> executable from AppSurface-trusted defaults or an explicit absolute override.
    /// </summary>
    /// <remarks>
    /// The resolver is the trust boundary for Linux Secret Service command execution. It intentionally ignores arbitrary
    /// <c>PATH</c> matches for command selection and reports them only as diagnostic context so package consumers do not
    /// accidentally execute a spoofed <c>secret-tool</c> binary.
    /// </remarks>
    internal sealed class LinuxSecretToolResolver
    {
        private const string FileName = "secret-tool";
        private static readonly char[] PathSeparators = [Path.PathSeparator];

        private readonly IReadOnlyList<string> _trustedCandidates;
        private readonly Func<string, LinuxSecretToolPathState> _inspectPath;
        private readonly Func<string?> _getPath;

        /// <summary>
        /// Initializes a resolver with deterministic candidate paths, file-system inspection, and environment lookup seams.
        /// </summary>
        /// <param name="trustedCandidates">
        /// Absolute paths that may be selected without an override. Candidates are evaluated in order; the first executable
        /// file wins.
        /// </param>
        /// <param name="inspectPath">
        /// Function that classifies a candidate path without executing it. Tests supply this seam to verify every resolution
        /// branch without depending on the host file system.
        /// </param>
        /// <param name="getPath">
        /// Function that reads the current <c>PATH</c> value for ignored-candidate diagnostics. The returned value never
        /// influences the selected executable.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="trustedCandidates"/>, <paramref name="inspectPath"/>, or <paramref name="getPath"/>
        /// is <see langword="null"/>.
        /// </exception>
        public LinuxSecretToolResolver(
            IEnumerable<string> trustedCandidates,
            Func<string, LinuxSecretToolPathState> inspectPath,
            Func<string?> getPath)
        {
            ArgumentNullException.ThrowIfNull(trustedCandidates);
            ArgumentNullException.ThrowIfNull(inspectPath);
            ArgumentNullException.ThrowIfNull(getPath);

            _trustedCandidates = trustedCandidates.ToArray();
            _inspectPath = inspectPath;
            _getPath = getPath;
        }

        /// <summary>
        /// Gets the production resolver that trusts <c>/usr/bin/secret-tool</c>, then <c>/bin/secret-tool</c>.
        /// </summary>
        public static LinuxSecretToolResolver Default { get; } =
            new(LinuxSecretToolTrustedCandidates, InspectFileSystemPath, () => Environment.GetEnvironmentVariable("PATH"));

        /// <summary>
        /// Resolves the executable path that the Linux platform store may launch.
        /// </summary>
        /// <param name="overridePath">
        /// Optional explicit override path from <see cref="AppSurfaceLocalSecretsOptions.LinuxSecretToolPath"/> or the
        /// CLI <c>--secret-tool-path</c> option. Overrides must be absolute executable files.
        /// </param>
        /// <returns>
        /// A successful resolution with a trusted executable path, or a failed resolution containing a display-safe
        /// diagnostic that explains why command execution is blocked.
        /// </returns>
        /// <remarks>
        /// Passing <see langword="null"/> uses the trusted default candidates only. Passing an empty, relative, missing,
        /// directory, or non-executable override fails before any process is launched.
        /// </remarks>
        public LinuxSecretToolResolution Resolve(string? overridePath)
        {
            if (overridePath != null)
            {
                return ResolveOverride(overridePath);
            }

            var trustedCandidate = _trustedCandidates
                .Where(candidate => _inspectPath(candidate) == LinuxSecretToolPathState.ExecutableFile)
                .FirstOrDefault();

            if (trustedCandidate != null)
            {
                return LinuxSecretToolResolution.Found(trustedCandidate);
            }

            return LinuxSecretToolResolution.Failed(
                LocalSecretResultStatus.UnsupportedPlatform,
                new AppSurfaceLocalSecretDiagnostic(
                    "local-secret-store-command-untrusted",
                    "Linux Secret Service command is not trusted.",
                    BuildNoTrustedCandidateCause(),
                    "Install `secret-tool` in `/usr/bin` or `/bin`, or verify a trusted nonstandard binary with `test -x /absolute/path/to/secret-tool` and pass `--secret-tool-path /absolute/path/to/secret-tool`. For app runtime, set `AppSurfaceLocalSecretsOptions.LinuxSecretToolPath`.",
                    "local-secrets-platform-compatibility"));
        }

        /// <summary>
        /// Creates the diagnostic result used when a Linux-only override is configured on a non-Linux platform.
        /// </summary>
        /// <param name="overridePath">The configured override path that cannot be used on the current platform.</param>
        /// <returns>A failed resolution instructing callers to remove the Linux-only override.</returns>
        public static LinuxSecretToolResolution UnsupportedPlatformOverride(string overridePath) =>
            LinuxSecretToolResolution.Failed(
                LocalSecretResultStatus.UnsupportedPlatform,
                new AppSurfaceLocalSecretDiagnostic(
                    "local-secret-store-command-unsupported",
                    "Linux Secret Service command override is unsupported on this platform.",
                    $"`secret-tool` overrides are Linux-only, but `{overridePath}` was configured for a non-Linux platform.",
                    "Remove `--secret-tool-path` or `AppSurfaceLocalSecretsOptions.LinuxSecretToolPath` on macOS/Windows and use the native platform store.",
                    "local-secrets-platform-compatibility"));

        private LinuxSecretToolResolution ResolveOverride(string overridePath)
        {
            if (string.IsNullOrWhiteSpace(overridePath))
            {
                return InvalidOverride(overridePath, "The configured `secret-tool` override path is empty.");
            }

            if (!Path.IsPathFullyQualified(overridePath))
            {
                return InvalidOverride(overridePath, "The configured `secret-tool` override path is not absolute.");
            }

            var state = _inspectPath(overridePath);
            return state switch
            {
                LinuxSecretToolPathState.ExecutableFile => LinuxSecretToolResolution.Found(overridePath),
                LinuxSecretToolPathState.Directory => InvalidOverride(overridePath, "The configured `secret-tool` override path is a directory."),
                LinuxSecretToolPathState.NotExecutableFile => InvalidOverride(overridePath, "The configured `secret-tool` override path exists but is not executable."),
                _ => InvalidOverride(overridePath, "The configured `secret-tool` override path does not exist.")
            };
        }

        private static LinuxSecretToolResolution InvalidOverride(string overridePath, string cause) =>
            LinuxSecretToolResolution.Failed(
                LocalSecretResultStatus.Unavailable,
                new AppSurfaceLocalSecretDiagnostic(
                    "local-secret-store-command-invalid",
                    "Linux Secret Service command override is invalid.",
                    cause,
                    $"Verify the binary with `test -x /absolute/path/to/secret-tool`, then pass `--secret-tool-path /absolute/path/to/secret-tool`. Current value: `{overridePath}`.",
                    "local-secrets-platform-compatibility"));

        private string BuildNoTrustedCandidateCause()
        {
            var checkedPaths = string.Join(", ", _trustedCandidates);
            var ignoredPathCandidate = FindIgnoredPathCandidate();
            if (ignoredPathCandidate == null)
            {
                return $"Checked trusted candidates: {checkedPaths}. AppSurface does not search PATH for `secret-tool`.";
            }

            return $"Checked trusted candidates: {checkedPaths}. Found `{ignoredPathCandidate}` on PATH, but AppSurface ignores PATH for `secret-tool` to avoid executing an untrusted command.";
        }

        private string? FindIgnoredPathCandidate()
        {
            var path = _getPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var trusted = _trustedCandidates.ToHashSet(StringComparer.Ordinal);
            return path
                .Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(directory => Path.Join(directory, FileName))
                .FirstOrDefault(candidate => !trusted.Contains(candidate) && _inspectPath(candidate) != LinuxSecretToolPathState.Missing);
        }

        private static LinuxSecretToolPathState InspectFileSystemPath(string path)
        {
            if (Directory.Exists(path))
            {
                return LinuxSecretToolPathState.Directory;
            }

            if (!File.Exists(path))
            {
                return LinuxSecretToolPathState.Missing;
            }

            if (OperatingSystem.IsWindows())
            {
                return LinuxSecretToolPathState.ExecutableFile;
            }

            try
            {
                const UnixFileMode executeBits = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
                return (File.GetUnixFileMode(path) & executeBits) == 0
                    ? LinuxSecretToolPathState.NotExecutableFile
                    : LinuxSecretToolPathState.ExecutableFile;
            }
            catch (IOException)
            {
                return LinuxSecretToolPathState.NotExecutableFile;
            }
            catch (UnauthorizedAccessException)
            {
                return LinuxSecretToolPathState.NotExecutableFile;
            }
        }
    }

    /// <summary>
    /// Describes the file-system state of a candidate Linux <c>secret-tool</c> path.
    /// </summary>
    /// <remarks>
    /// The states deliberately separate existence from executability so diagnostics can distinguish missing binaries,
    /// directory mistakes, and files that exist but cannot be launched.
    /// </remarks>
    internal enum LinuxSecretToolPathState
    {
        /// <summary>
        /// The candidate path does not exist.
        /// </summary>
        Missing,

        /// <summary>
        /// The candidate path exists but is a directory, not an executable file.
        /// </summary>
        Directory,

        /// <summary>
        /// The candidate path exists as a file but does not have executable permissions.
        /// </summary>
        NotExecutableFile,

        /// <summary>
        /// The candidate path exists as a file and is executable by at least one Unix permission class.
        /// </summary>
        ExecutableFile
    }

    /// <summary>
    /// Immutable outcome from resolving the Linux <c>secret-tool</c> command path.
    /// </summary>
    /// <param name="Succeeded">
    /// <see langword="true"/> when <paramref name="Path"/> contains a command path that may be launched.
    /// </param>
    /// <param name="Path">
    /// The trusted executable path for successful resolutions, or <see langword="null"/> for failed resolutions.
    /// </param>
    /// <param name="Status">
    /// The LocalSecrets result status callers should surface when resolution fails.
    /// </param>
    /// <param name="Diagnostic">
    /// Display-safe diagnostic details for failed resolutions, or <see langword="null"/> when resolution succeeds.
    /// </param>
    /// <remarks>
    /// Callers should use <see cref="Found(string)"/> and <see cref="Failed(LocalSecretResultStatus, AppSurfaceLocalSecretDiagnostic)"/>
    /// so success results never carry diagnostics and failed results always carry the user-facing cause/fix guidance.
    /// </remarks>
    internal sealed record LinuxSecretToolResolution(
        bool Succeeded,
        string? Path,
        LocalSecretResultStatus Status,
        AppSurfaceLocalSecretDiagnostic? Diagnostic)
    {
        /// <summary>
        /// Creates a successful resolution for a trusted executable path.
        /// </summary>
        /// <param name="path">The absolute command path that the Linux platform store may launch.</param>
        /// <returns>A resolution with <see cref="Succeeded"/> set and no diagnostic.</returns>
        public static LinuxSecretToolResolution Found(string path) =>
            new(true, path, LocalSecretResultStatus.Found, null);

        /// <summary>
        /// Creates a failed resolution with the status and diagnostic callers should surface.
        /// </summary>
        /// <param name="status">The LocalSecrets status that best describes the failure category.</param>
        /// <param name="diagnostic">Display-safe details explaining why command resolution failed and how to fix it.</param>
        /// <returns>A resolution with no executable path and <see cref="Succeeded"/> unset.</returns>
        public static LinuxSecretToolResolution Failed(LocalSecretResultStatus status, AppSurfaceLocalSecretDiagnostic diagnostic) =>
            new(false, null, status, diagnostic);
    }

    private sealed class DiagnosticLocalSecretStore(
        LocalSecretResultStatus status,
        AppSurfaceLocalSecretDiagnostic diagnostic) : IAppSurfaceLocalSecretStore
    {
        public string Name => nameof(DiagnosticLocalSecretStore);

        public AppSurfaceLocalSecretResult Get(AppSurfaceLocalSecretIdentity identity) => Unsupported();

        public AppSurfaceLocalSecretResult Set(AppSurfaceLocalSecretIdentity identity, string value) => Unsupported();

        public AppSurfaceLocalSecretResult Delete(AppSurfaceLocalSecretIdentity identity) => Unsupported();

        public AppSurfaceLocalSecretListResult List(string applicationName, string environment, string? keyPrefix) =>
            AppSurfaceLocalSecretListResult.Failed(status, diagnostic, Name);

        public AppSurfaceLocalSecretResult Doctor(string applicationName, string environment, string? keyPrefix) => Unsupported();

        private AppSurfaceLocalSecretResult Unsupported() =>
            AppSurfaceLocalSecretResult.NotFound(status, diagnostic, Name);

        public static DiagnosticLocalSecretStore FromResolution(LinuxSecretToolResolution resolution) =>
            new(resolution.Status, resolution.Diagnostic ?? throw new ArgumentException("Failed resolution requires a diagnostic.", nameof(resolution)));

        public static DiagnosticLocalSecretStore Unsupported(AppSurfaceLocalSecretDiagnostic diagnostic) =>
            new(LocalSecretResultStatus.UnsupportedPlatform, diagnostic);
    }

    internal sealed partial class MacOsKeychainLocalSecretStore : IndexedLocalSecretStore
    {
        private const int ErrSecSuccess = 0;
        private const int ErrSecDuplicateItem = -25299;
        private const int ErrSecItemNotFound = -25300;
        private const int ErrSecInteractionNotAllowed = -25308;
        private const int ErrSecAuthFailed = -25293;
        private const int ErrSecUserCanceled = -128;

        public override string Name => "macOS Keychain";

        protected override AppSurfaceLocalSecretResult ReadValue(AppSurfaceLocalSecretIdentity identity)
        {
            var names = BuildKeychainName(identity);
            var status = SecKeychainFindGenericPassword(
                IntPtr.Zero,
                (uint)names.Service.Length,
                names.Service,
                (uint)names.Account.Length,
                names.Account,
                out var passwordLength,
                out var passwordData,
                out var itemRef);
            try
            {
                if (status == ErrSecSuccess)
                {
                    var bytes = new byte[passwordLength];
                    Marshal.Copy(passwordData, bytes, 0, bytes.Length);
                    return AppSurfaceLocalSecretResult.Found(Encoding.UTF8.GetString(bytes), Name);
                }

                return MapMacOsStatus(status, "read");
            }
            finally
            {
                if (passwordData != IntPtr.Zero)
                {
                    SecKeychainItemFreeContent(IntPtr.Zero, passwordData);
                }

                ReleaseIfNeeded(itemRef);
            }
        }

        protected override AppSurfaceLocalSecretResult WriteValue(AppSurfaceLocalSecretIdentity identity, string value)
        {
            var names = BuildKeychainName(identity);
            var password = Encoding.UTF8.GetBytes(value);
            var status = SecKeychainAddGenericPassword(
                IntPtr.Zero,
                (uint)names.Service.Length,
                names.Service,
                (uint)names.Account.Length,
                names.Account,
                (uint)password.Length,
                password,
                out var addedItemRef);
            ReleaseIfNeeded(addedItemRef);

            if (status == ErrSecDuplicateItem)
            {
                status = UpdateExisting(identity, password);
            }

            if (status != ErrSecSuccess)
            {
                return MapMacOsStatus(status, "write");
            }

            if (string.Equals(identity.Key, IndexKey, StringComparison.Ordinal))
            {
                return AppSurfaceLocalSecretResult.Found(string.Empty, Name);
            }

            return UpdateIndex(identity, add: true);
        }

        protected override AppSurfaceLocalSecretResult DeleteValue(AppSurfaceLocalSecretIdentity identity)
        {
            var status = FindItem(identity, out var itemRef);
            try
            {
                if (status == ErrSecItemNotFound)
                {
                    return AppSurfaceLocalSecretResult.Missing(Name);
                }

                if (status != ErrSecSuccess)
                {
                    return MapMacOsStatus(status, "delete");
                }

                status = SecKeychainItemDelete(itemRef);
                if (status != ErrSecSuccess)
                {
                    return MapMacOsStatus(status, "delete");
                }
            }
            finally
            {
                ReleaseIfNeeded(itemRef);
            }

            if (string.Equals(identity.Key, IndexKey, StringComparison.Ordinal))
            {
                return AppSurfaceLocalSecretResult.Found(string.Empty, Name);
            }

            return UpdateIndex(identity, add: false);
        }

        protected override AppSurfaceLocalSecretResult DoctorStore(string applicationName, string environment, string? keyPrefix) =>
            AppSurfaceLocalSecretResult.NotFound(
                LocalSecretResultStatus.Missing,
                new AppSurfaceLocalSecretDiagnostic(
                    "local-secret-store-ready",
                    "macOS Keychain is available.",
                    "Security.framework can access the current user's Keychain session.",
                    "Set a local secret with `appsurface secrets set`.",
                    "local-secrets-platform-compatibility"),
                Name);

        private static string Service(AppSurfaceLocalSecretIdentity identity) =>
            $"AppSurface.LocalSecrets.{identity.ApplicationName}.{identity.Environment}";

        internal static string Account(AppSurfaceLocalSecretIdentity identity) =>
            string.IsNullOrWhiteSpace(identity.KeyPrefix) ? identity.Key : $"{identity.KeyPrefix}:{identity.Key}";

        internal static KeychainNameBytes BuildKeychainName(AppSurfaceLocalSecretIdentity identity) =>
            new(Encode(Service(identity)), Encode(Account(identity)));

        private static byte[] Encode(string value) => Encoding.UTF8.GetBytes(value);

        internal AppSurfaceLocalSecretResult MapMacOsStatus(int status, string operation)
        {
            if (status == ErrSecItemNotFound)
            {
                return AppSurfaceLocalSecretResult.Missing(Name);
            }

            var resultStatus = status is ErrSecInteractionNotAllowed or ErrSecAuthFailed or ErrSecUserCanceled
                ? LocalSecretResultStatus.Locked
                : LocalSecretResultStatus.Unavailable;
            return AppSurfaceLocalSecretResult.NotFound(
                resultStatus,
                new AppSurfaceLocalSecretDiagnostic(
                    resultStatus == LocalSecretResultStatus.Locked ? "local-secret-store-locked" : "local-secret-store-unavailable",
                    "macOS Keychain could not complete the request.",
                    $"Keychain returned OSStatus {status} during `{operation}`.",
                    "Unlock the login keychain, allow access for the current user session, or use environment variables/key-per-file for this environment.",
                    "local-secrets-platform-compatibility",
                    retryable: true),
                Name);
        }

        private static int UpdateExisting(AppSurfaceLocalSecretIdentity identity, byte[] password)
        {
            var status = FindItem(identity, out var itemRef);
            try
            {
                return status == ErrSecSuccess
                    ? SecKeychainItemModifyAttributesAndData(itemRef, IntPtr.Zero, (uint)password.Length, password)
                    : status;
            }
            finally
            {
                ReleaseIfNeeded(itemRef);
            }
        }

        private static int FindItem(AppSurfaceLocalSecretIdentity identity, out IntPtr itemRef)
        {
            var names = BuildKeychainName(identity);
            var status = SecKeychainFindGenericPassword(
                IntPtr.Zero,
                (uint)names.Service.Length,
                names.Service,
                (uint)names.Account.Length,
                names.Account,
                out _,
                out var passwordData,
                out itemRef);
            if (passwordData != IntPtr.Zero)
            {
                SecKeychainItemFreeContent(IntPtr.Zero, passwordData);
            }

            return status;
        }

        internal readonly record struct KeychainNameBytes(byte[] Service, byte[] Account);

        private static void ReleaseIfNeeded(IntPtr itemRef)
        {
            if (itemRef != IntPtr.Zero)
            {
                CFRelease(itemRef);
            }
        }

        [LibraryImport("/System/Library/Frameworks/Security.framework/Security")]
        private static partial int SecKeychainAddGenericPassword(
            IntPtr keychain,
            uint serviceNameLength,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)]
            byte[] serviceName,
            uint accountNameLength,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)]
            byte[] accountName,
            uint passwordLength,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 5)]
            byte[] passwordData,
            out IntPtr itemRef);

        [LibraryImport("/System/Library/Frameworks/Security.framework/Security")]
        private static partial int SecKeychainFindGenericPassword(
            IntPtr keychainOrArray,
            uint serviceNameLength,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)]
            byte[] serviceName,
            uint accountNameLength,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)]
            byte[] accountName,
            out uint passwordLength,
            out IntPtr passwordData,
            out IntPtr itemRef);

        [LibraryImport("/System/Library/Frameworks/Security.framework/Security")]
        private static partial int SecKeychainItemModifyAttributesAndData(
            IntPtr itemRef,
            IntPtr attrList,
            uint length,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)]
            byte[] data);

        [LibraryImport("/System/Library/Frameworks/Security.framework/Security")]
        private static partial int SecKeychainItemDelete(IntPtr itemRef);

        [LibraryImport("/System/Library/Frameworks/Security.framework/Security")]
        private static partial int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

        [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
        private static partial void CFRelease(IntPtr cf);
    }

    internal sealed class LinuxSecretServiceLocalSecretStore : CommandBackedLocalSecretStore
    {
        private readonly string _secretToolPath;

        public LinuxSecretServiceLocalSecretStore(string secretToolPath)
            : this(secretToolPath, DefaultPlatformSecretCommandRunner.Instance)
        {
        }

        internal LinuxSecretServiceLocalSecretStore(string secretToolPath, IPlatformSecretCommandRunner commandRunner)
            : base(commandRunner)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(secretToolPath);

            _secretToolPath = secretToolPath;
        }

        public override string Name => "Linux Secret Service";

        protected override AppSurfaceLocalSecretResult ReadValue(AppSurfaceLocalSecretIdentity identity)
        {
            var result = Run(
                _secretToolPath,
                BuildArguments("lookup", identity),
                null);
            if (result.ExitCode == 0)
            {
                return AppSurfaceLocalSecretResult.Found(result.Output, Name);
            }

            return IsLinuxMissing(result)
                ? AppSurfaceLocalSecretResult.Missing(Name)
                : MapCommandFailure(result, "read");
        }

        protected override AppSurfaceLocalSecretResult WriteValue(AppSurfaceLocalSecretIdentity identity, string value)
        {
            var label = $"AppSurface {identity.ApplicationName} {identity.Environment} {identity.Key}";
            var result = Run(
                _secretToolPath,
                ["store", "--label", label, .. BuildArguments(identity)],
                value);
            if (result.ExitCode != 0)
            {
                return MapCommandFailure(result, "write");
            }

            if (string.Equals(identity.Key, IndexKey, StringComparison.Ordinal))
            {
                return AppSurfaceLocalSecretResult.Found(string.Empty, Name);
            }

            return UpdateIndex(identity, add: true);
        }

        protected override AppSurfaceLocalSecretResult DeleteValue(AppSurfaceLocalSecretIdentity identity)
        {
            var result = Run(
                _secretToolPath,
                BuildArguments("clear", identity),
                null);
            if (result.ExitCode != 0)
            {
                return IsLinuxMissing(result)
                    ? AppSurfaceLocalSecretResult.Missing(Name)
                    : MapCommandFailure(result, "delete");
            }

            if (string.Equals(identity.Key, IndexKey, StringComparison.Ordinal))
            {
                return AppSurfaceLocalSecretResult.Found(string.Empty, Name);
            }

            return UpdateIndex(identity, add: false);
        }

        protected override AppSurfaceLocalSecretResult DoctorStore(string applicationName, string environment, string? keyPrefix)
        {
            var result = Run(_secretToolPath, ["search", "appsurface", "local-secrets"], null);
            return result.ExitCode == 0
                ? AppSurfaceLocalSecretResult.NotFound(
                    LocalSecretResultStatus.Missing,
                    new AppSurfaceLocalSecretDiagnostic(
                        "local-secret-store-ready",
                        "Linux Secret Service is reachable.",
                        "`secret-tool` can talk to the current user secret service session.",
                        "Set a local secret with `appsurface secrets set`.",
                        "local-secrets-platform-compatibility"),
                    Name)
                : MapCommandFailure(result, "doctor");
        }

        private static bool IsLinuxMissing(PlatformSecretCommandResult result) =>
            result.ExitCode != 0
            && string.IsNullOrWhiteSpace(result.Output)
            && string.IsNullOrWhiteSpace(result.Error);

        internal static string[] BuildArguments(string command, AppSurfaceLocalSecretIdentity identity) =>
            [command, .. BuildArguments(identity)];

        private static string[] BuildArguments(AppSurfaceLocalSecretIdentity identity) =>
            [
                "appsurface",
                "local-secrets",
                "application",
                identity.ApplicationName,
                "environment",
                identity.Environment,
                "prefix",
                identity.KeyPrefix ?? string.Empty,
                "key",
                identity.Key
            ];
    }

    [SupportedOSPlatform("windows")]
    private sealed partial class WindowsCredentialManagerLocalSecretStore : IndexedLocalSecretStore
    {
        private const int ErrorNotFound = 1168;
        private const int CredentialTypeGeneric = 1;
        private const int CredentialPersistLocalMachine = 2;

        public override string Name => "Windows Credential Manager";

        protected override AppSurfaceLocalSecretResult ReadValue(AppSurfaceLocalSecretIdentity identity)
        {
            if (!CredReadW(TargetName(identity), CredentialTypeGeneric, 0, out var credentialPointer))
            {
                return Marshal.GetLastPInvokeError() == ErrorNotFound
                    ? AppSurfaceLocalSecretResult.Missing(Name)
                    : WindowsFailure("read", LocalSecretResultStatus.Unavailable);
            }

            try
            {
                var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
                if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
                {
                    return AppSurfaceLocalSecretResult.Found(string.Empty, Name);
                }

                var bytes = new byte[credential.CredentialBlobSize];
                Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                return AppSurfaceLocalSecretResult.Found(Encoding.Unicode.GetString(bytes), Name);
            }
            finally
            {
                CredFree(credentialPointer);
            }
        }

        protected override AppSurfaceLocalSecretResult WriteValue(AppSurfaceLocalSecretIdentity identity, string value)
        {
            var targetName = TargetName(identity);
            var blob = Encoding.Unicode.GetBytes(value);
            var blobHandle = GCHandle.Alloc(blob, GCHandleType.Pinned);
            try
            {
                var credential = new NativeCredential
                {
                    Type = CredentialTypeGeneric,
                    TargetName = targetName,
                    CredentialBlobSize = blob.Length,
                    CredentialBlob = blobHandle.AddrOfPinnedObject(),
                    Persist = CredentialPersistLocalMachine,
                    UserName = Environment.UserName
                };

                var credentialPointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeCredential>());
                try
                {
                    Marshal.StructureToPtr(credential, credentialPointer, fDeleteOld: false);
                    if (!CredWriteW(credentialPointer, 0))
                    {
                        return WindowsFailure("write", LocalSecretResultStatus.Unavailable);
                    }
                }
                finally
                {
                    Marshal.DestroyStructure<NativeCredential>(credentialPointer);
                    Marshal.FreeHGlobal(credentialPointer);
                }
            }
            finally
            {
                blobHandle.Free();
            }

            if (string.Equals(identity.Key, IndexKey, StringComparison.Ordinal))
            {
                return AppSurfaceLocalSecretResult.Found(string.Empty, Name);
            }

            return UpdateIndex(identity, add: true);
        }

        protected override AppSurfaceLocalSecretResult DeleteValue(AppSurfaceLocalSecretIdentity identity)
        {
            if (!CredDeleteW(TargetName(identity), CredentialTypeGeneric, 0))
            {
                return Marshal.GetLastPInvokeError() == ErrorNotFound
                    ? AppSurfaceLocalSecretResult.Missing(Name)
                    : WindowsFailure("delete", LocalSecretResultStatus.Unavailable);
            }

            if (string.Equals(identity.Key, IndexKey, StringComparison.Ordinal))
            {
                return AppSurfaceLocalSecretResult.Found(string.Empty, Name);
            }

            return UpdateIndex(identity, add: false);
        }

        protected override AppSurfaceLocalSecretResult DoctorStore(string applicationName, string environment, string? keyPrefix)
        {
            var probeIdentity = new AppSurfaceLocalSecretIdentity(
                applicationName,
                environment,
                keyPrefix,
                "__appsurface_doctor__",
                $"appsurface:{applicationName}:{environment}:{keyPrefix}:__appsurface_doctor__");
            var write = WriteValue(probeIdentity, "ready");
            if (write.Status != LocalSecretResultStatus.Found)
            {
                return write;
            }

            var read = ReadValue(probeIdentity);
            _ = DeleteValue(probeIdentity);
            return read.Status == LocalSecretResultStatus.Found
                ? AppSurfaceLocalSecretResult.NotFound(
                    LocalSecretResultStatus.Missing,
                    new AppSurfaceLocalSecretDiagnostic(
                        "local-secret-store-ready",
                        "Windows Credential Manager is reachable.",
                        "The current user can write, read, and delete a generic credential.",
                        "Set a local secret with `appsurface secrets set`.",
                        "local-secrets-platform-compatibility"),
                    Name)
                : read;
        }

        private AppSurfaceLocalSecretResult WindowsFailure(string operation, LocalSecretResultStatus status) =>
            AppSurfaceLocalSecretResult.NotFound(
                status,
                new AppSurfaceLocalSecretDiagnostic(
                    "local-secret-store-unavailable",
                    "Windows Credential Manager could not complete the request.",
                    $"The current-user credential store failed during `{operation}`.",
                    "Run in an interactive Windows user session, unlock the user profile, or use environment variables/key-per-file for this environment.",
                    "local-secrets-platform-compatibility",
                    retryable: true),
                Name);

        private static string TargetName(AppSurfaceLocalSecretIdentity identity) => $"AppSurface.LocalSecrets.{identity.StorageName}";

        [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CredReadW(string target, int type, int reservedFlag, out IntPtr credential);

        [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CredWriteW(IntPtr userCredential, int flags);

        [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CredDeleteW(string target, int type, int flags);

        [LibraryImport("advapi32.dll")]
        private static partial void CredFree(IntPtr buffer);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NativeCredential
        {
            public int Flags;
            public int Type;
            public string TargetName;
            public string? Comment;
            public long LastWritten;
            public int CredentialBlobSize;
            public IntPtr CredentialBlob;
            public int Persist;
            public int AttributeCount;
            public IntPtr Attributes;
            public string? TargetAlias;
            public string? UserName;
        }
    }

    internal abstract class IndexedLocalSecretStore : IAppSurfaceLocalSecretStore
    {
        protected const string IndexKey = "__appsurface_index__";

        public abstract string Name { get; }

        public AppSurfaceLocalSecretResult Get(AppSurfaceLocalSecretIdentity identity) => ReadValue(identity);

        public AppSurfaceLocalSecretResult Set(AppSurfaceLocalSecretIdentity identity, string value) => WriteValue(identity, value);

        public AppSurfaceLocalSecretResult Delete(AppSurfaceLocalSecretIdentity identity) => DeleteValue(identity);

        public AppSurfaceLocalSecretListResult List(string applicationName, string environment, string? keyPrefix)
        {
            var indexIdentity = new AppSurfaceLocalSecretIdentity(applicationName, environment, keyPrefix, IndexKey, $"appsurface:{applicationName}:{environment}:{keyPrefix}:{IndexKey}");
            var result = ReadValue(indexIdentity);
            if (result.Status == LocalSecretResultStatus.Missing)
            {
                return AppSurfaceLocalSecretListResult.Found([], Name);
            }

            if (result.Status != LocalSecretResultStatus.Found || result.Value == null)
            {
                return AppSurfaceLocalSecretListResult.Failed(result.Status, result.Diagnostic!, Name);
            }

            try
            {
                var keys = JsonSerializer.Deserialize<string[]>(result.Value) ?? [];
                return AppSurfaceLocalSecretListResult.Found(keys.Where(key => !string.Equals(key, IndexKey, StringComparison.Ordinal)), Name);
            }
            catch (JsonException)
            {
                return AppSurfaceLocalSecretListResult.Failed(
                    LocalSecretResultStatus.ProviderFailed,
                    new AppSurfaceLocalSecretDiagnostic(
                        "local-secret-index-invalid",
                        "Local secret index is invalid.",
                        "The platform store index entry could not be parsed.",
                        "Delete and recreate the LocalSecrets namespace with `appsurface secrets init`.",
                        "local-secrets-troubleshooting"),
                    Name);
            }
        }

        public AppSurfaceLocalSecretResult Doctor(string applicationName, string environment, string? keyPrefix) =>
            DoctorStore(applicationName, environment, keyPrefix);

        protected abstract AppSurfaceLocalSecretResult ReadValue(AppSurfaceLocalSecretIdentity identity);

        protected abstract AppSurfaceLocalSecretResult WriteValue(AppSurfaceLocalSecretIdentity identity, string value);

        protected abstract AppSurfaceLocalSecretResult DeleteValue(AppSurfaceLocalSecretIdentity identity);

        protected abstract AppSurfaceLocalSecretResult DoctorStore(string applicationName, string environment, string? keyPrefix);

        protected AppSurfaceLocalSecretResult UpdateIndex(AppSurfaceLocalSecretIdentity identity, bool add)
        {
            var current = List(identity.ApplicationName, identity.Environment, identity.KeyPrefix);
            if (current.Status != LocalSecretResultStatus.Found)
            {
                return AppSurfaceLocalSecretResult.NotFound(current.Status, current.Diagnostic!, Name);
            }

            var keys = current.Keys.ToHashSet(StringComparer.Ordinal);
            if (add)
            {
                keys.Add(identity.Key);
            }
            else
            {
                keys.Remove(identity.Key);
            }

            var indexIdentity = identity with
            {
                Key = IndexKey,
                StorageName = $"appsurface:{identity.ApplicationName}:{identity.Environment}:{identity.KeyPrefix}:{IndexKey}"
            };
            var index = JsonSerializer.Serialize(keys.Order(StringComparer.OrdinalIgnoreCase).ToArray());
            var write = WriteValue(indexIdentity, index);
            return write.Status == LocalSecretResultStatus.Found
                ? AppSurfaceLocalSecretResult.Found(string.Empty, Name)
                : write;
        }

    }

    internal abstract class CommandBackedLocalSecretStore : IndexedLocalSecretStore
    {
        private readonly IPlatformSecretCommandRunner _commandRunner;

        protected CommandBackedLocalSecretStore(IPlatformSecretCommandRunner commandRunner)
        {
            ArgumentNullException.ThrowIfNull(commandRunner);

            _commandRunner = commandRunner;
        }

        protected AppSurfaceLocalSecretResult MapCommandFailure(PlatformSecretCommandResult result, string operation)
        {
            var status = result.Error.Contains("User interaction is not allowed", StringComparison.OrdinalIgnoreCase)
                         || result.Error.Contains("locked", StringComparison.OrdinalIgnoreCase)
                         || result.Error.Contains("denied", StringComparison.OrdinalIgnoreCase)
                ? LocalSecretResultStatus.Locked
                : LocalSecretResultStatus.Unavailable;
            return AppSurfaceLocalSecretResult.NotFound(
                status,
                new AppSurfaceLocalSecretDiagnostic(
                    status == LocalSecretResultStatus.Locked ? "local-secret-store-locked" : "local-secret-store-unavailable",
                    "Local secret store could not complete the request.",
                    $"The platform store failed during `{operation}` with exit code {result.ExitCode}.",
                    "Unlock the user secret store, run in an interactive desktop session, or use environment variables/key-per-file for this environment.",
                    "local-secrets-platform-compatibility",
                    retryable: true),
                Name);
        }

        protected PlatformSecretCommandResult Run(string fileName, IReadOnlyList<string> arguments, string? standardInput) =>
            _commandRunner.Run(fileName, arguments, standardInput);
    }

    internal interface IPlatformSecretCommandRunner
    {
        PlatformSecretCommandResult Run(string fileName, IReadOnlyList<string> arguments, string? standardInput);
    }

    internal sealed class DefaultPlatformSecretCommandRunner : IPlatformSecretCommandRunner
    {
        private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(30);
        private readonly TimeSpan _commandTimeout;

        public static DefaultPlatformSecretCommandRunner Instance { get; } = new(DefaultCommandTimeout);

        internal DefaultPlatformSecretCommandRunner(TimeSpan commandTimeout)
        {
            if (commandTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(commandTimeout), commandTimeout, "Command timeout must be positive.");
            }

            _commandTimeout = commandTimeout;
        }

        public PlatformSecretCommandResult Run(string fileName, IReadOnlyList<string> arguments, string? standardInput)
        {
            using var process = new Process();
            process.StartInfo.FileName = fileName;
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.RedirectStandardInput = standardInput != null;
            process.StartInfo.UseShellExecute = false;
            try
            {
                process.Start();
            }
            catch (InvalidOperationException ex)
            {
                return PlatformSecretCommandResult.StartFailed(ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                return PlatformSecretCommandResult.StartFailed(ex);
            }
            catch (Win32Exception ex)
            {
                return PlatformSecretCommandResult.StartFailed(ex);
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            if (standardInput != null)
            {
                process.StandardInput.Write(standardInput);
                process.StandardInput.Close();
            }

            if (!process.WaitForExit(_commandTimeout))
            {
                KillTimedOutProcess(process);
                return PlatformSecretCommandResult.TimedOut;
            }

            Task.WhenAll(outputTask, errorTask).GetAwaiter().GetResult();
            var output = outputTask.GetAwaiter().GetResult().TrimEnd('\r', '\n');
            var error = errorTask.GetAwaiter().GetResult();
            return new PlatformSecretCommandResult(process.ExitCode, output, error);
        }

        private static void KillTimedOutProcess(Process process)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException ex)
            {
                TraceTimedOutProcessCleanupFailure(ex);
            }
            catch (NotSupportedException ex)
            {
                TraceTimedOutProcessCleanupFailure(ex);
            }
            catch (Win32Exception ex)
            {
                TraceTimedOutProcessCleanupFailure(ex);
            }
        }

        private static void TraceTimedOutProcessCleanupFailure(Exception exception) =>
            Trace.TraceWarning(
                "AppSurface LocalSecrets could not terminate a timed-out platform secret command. Exception={0}; HResult={1}.",
                exception.GetType().FullName,
                exception.HResult);
    }

    internal sealed record PlatformSecretCommandResult(int ExitCode, string Output, string Error)
    {
        internal const int TimedOutExitCode = -1;
        internal const int StartFailedExitCode = -2;

        public static PlatformSecretCommandResult TimedOut { get; } =
            new(TimedOutExitCode, string.Empty, "Timed out waiting for the platform secret command to finish.");

        public static PlatformSecretCommandResult StartFailed(Exception exception) =>
            new(
                StartFailedExitCode,
                string.Empty,
                $"Could not start the platform secret command. Exception={exception.GetType().Name}; Message={exception.Message}");
    }
}
