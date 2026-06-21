using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Config.LocalSecrets;

/// <summary>
/// File-backed LocalSecrets store for deterministic local workflows and tests.
/// </summary>
/// <remarks>
/// This store is useful when OS credential tooling is unavailable in CI or examples. It is not the default platform
/// store and should not be used as a production vault. The file contains secret values and must stay outside source
/// control.
/// </remarks>
public sealed class FileAppSurfaceLocalSecretStore : IAppSurfaceLocalSecretStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly IFileAppSurfaceLocalSecretStoreFileSystem _fileSystem;
    private readonly object _gate = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="FileAppSurfaceLocalSecretStore"/> class.
    /// </summary>
    /// <param name="path">The JSON file that stores local secrets.</param>
    public FileAppSurfaceLocalSecretStore(string path)
        : this(path, DefaultFileAppSurfaceLocalSecretStoreFileSystem.Instance)
    {
    }

    internal FileAppSurfaceLocalSecretStore(
        string path,
        IFileAppSurfaceLocalSecretStoreFileSystem fileSystem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(fileSystem);

        _path = Path.GetFullPath(path);
        _fileSystem = fileSystem;
    }

    /// <inheritdoc />
    public string Name => nameof(FileAppSurfaceLocalSecretStore);

    /// <summary>
    /// Gets the default per-user AppSurface local secret file path.
    /// </summary>
    /// <returns>A path under the user's local application data directory.</returns>
    [ExcludeFromCodeCoverage(
        Justification = "Special-folder fallback depends on host OS profile state; constructor and file-store behavior are covered with deterministic paths.")]
    public static string GetDefaultPath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".appsurface");
        }

        return Path.Join(root, "AppSurface", "local-secrets.json");
    }

    /// <inheritdoc />
    public AppSurfaceLocalSecretResult Get(AppSurfaceLocalSecretIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        lock (_gate)
        {
            if (!TryRead(out var data, out var failure))
            {
                return failure;
            }

            if (!data.TryGetValue(identity.StorageName, out var entry))
            {
                return AppSurfaceLocalSecretResult.Missing(Name);
            }

            var posture = _fileSystem.InspectExistingFilePosture(_path);
            return posture.Kind == FileSecretPostureKind.Unsupported
                ? PostureFailure(posture)
                : AppSurfaceLocalSecretResult.Found(entry.Value, Name);
        }
    }

    /// <inheritdoc />
    public AppSurfaceLocalSecretResult Set(AppSurfaceLocalSecretIdentity identity, string value)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(value);

        lock (_gate)
        {
            var preflight = _fileSystem.PrepareWrite(_path);
            if (preflight.Kind == FileSecretPostureKind.Unsupported)
            {
                return PostureFailure(preflight);
            }

            if (!TryRead(out var data, out var failure))
            {
                return failure;
            }

            data[identity.StorageName] = new FileSecretEntry(identity.ApplicationName, identity.Environment, identity.KeyPrefix, identity.Key, value);
            var writeFailure = TryWrite(data);
            if (writeFailure != null)
            {
                return writeFailure;
            }
        }

        return AppSurfaceLocalSecretResult.Found(string.Empty, Name);
    }

    /// <inheritdoc />
    public AppSurfaceLocalSecretResult Delete(AppSurfaceLocalSecretIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        lock (_gate)
        {
            var preflight = _fileSystem.PrepareWrite(_path);
            if (preflight.Kind == FileSecretPostureKind.Unsupported)
            {
                return PostureFailure(preflight);
            }

            if (!TryRead(out var data, out var failure))
            {
                return failure;
            }

            if (!data.Remove(identity.StorageName))
            {
                return AppSurfaceLocalSecretResult.Missing(Name);
            }

            var writeFailure = TryWrite(data);
            if (writeFailure != null)
            {
                return writeFailure;
            }
        }

        return AppSurfaceLocalSecretResult.Found(string.Empty, Name);
    }

    /// <inheritdoc />
    public AppSurfaceLocalSecretListResult List(string applicationName, string environment, string? keyPrefix)
    {
        lock (_gate)
        {
            if (!TryRead(out var data, out var failure))
            {
                return AppSurfaceLocalSecretListResult.Failed(failure.Status, failure.Diagnostic!, Name);
            }

            var posture = _fileSystem.InspectExistingFilePosture(_path);
            if (posture.Kind == FileSecretPostureKind.Unsupported)
            {
                var postureFailure = PostureFailure(posture);
                return AppSurfaceLocalSecretListResult.Failed(postureFailure.Status, postureFailure.Diagnostic!, Name);
            }

            var keys = data.Values
                .Where(entry => string.Equals(entry.ApplicationName, applicationName, StringComparison.Ordinal)
                                && string.Equals(entry.Environment, environment, StringComparison.Ordinal)
                                && string.Equals(entry.KeyPrefix, keyPrefix, StringComparison.Ordinal))
                .Select(entry => entry.Key);

            return AppSurfaceLocalSecretListResult.Found(keys, Name);
        }
    }

    /// <inheritdoc />
    public AppSurfaceLocalSecretResult Doctor(string applicationName, string environment, string? keyPrefix)
    {
        try
        {
            var posture = _fileSystem.Doctor(_path);
            return posture.Kind == FileSecretPostureKind.Unsupported
                ? PostureFailure(posture)
                : AppSurfaceLocalSecretResult.NotFound(
                    LocalSecretResultStatus.Missing,
                    posture.ToDiagnostic(),
                    Name);
        }
        catch (UnauthorizedAccessException)
        {
            return Failure(LocalSecretResultStatus.Locked, "local-secret-store-locked", "Local secret file store cannot be opened.", "The current user cannot read or write the configured local secret file.", "Fix file permissions or choose an OS-backed store.");
        }
        catch (IOException)
        {
            return Failure(LocalSecretResultStatus.Unavailable, "local-secret-store-unavailable", "Local secret file store is unavailable.", "The local secret file could not be opened.", "Close other processes using the file and retry.", retryable: true);
        }
    }

    private bool TryRead(
        out Dictionary<string, FileSecretEntry> data,
        out AppSurfaceLocalSecretResult failure)
    {
        try
        {
            var posture = _fileSystem.InspectExistingFilePosture(_path);
            if (posture.Kind == FileSecretPostureKind.Unsupported)
            {
                data = [];
                failure = PostureFailure(posture);
                return false;
            }

            data = Read();
            failure = null!;
            return true;
        }
        catch (JsonException)
        {
            data = [];
            failure = Failure(
                LocalSecretResultStatus.ProviderFailed,
                "local-secret-store-invalid",
                "Local secret file store is invalid.",
                "The configured local secret file could not be parsed.",
                "Delete and recreate the LocalSecrets namespace with `appsurface secrets init`.");
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            data = [];
            failure = Failure(
                LocalSecretResultStatus.Locked,
                "local-secret-store-locked",
                "Local secret file store cannot be read.",
                "The current user cannot read the configured local secret file.",
                "Fix file permissions or choose an OS-backed store.");
            return false;
        }
        catch (IOException)
        {
            data = [];
            failure = Failure(
                LocalSecretResultStatus.Unavailable,
                "local-secret-store-unavailable",
                "Local secret file store is unavailable.",
                "The configured local secret file could not be read.",
                "Close other processes using the file and retry.",
                retryable: true);
            return false;
        }
    }

    private Dictionary<string, FileSecretEntry> Read()
    {
        if (!_fileSystem.FileExists(_path))
        {
            return new Dictionary<string, FileSecretEntry>(StringComparer.Ordinal);
        }

        var text = _fileSystem.ReadAllText(_path);
        if (string.IsNullOrWhiteSpace(text))
        {
            return new Dictionary<string, FileSecretEntry>(StringComparer.Ordinal);
        }

        return JsonSerializer.Deserialize<Dictionary<string, FileSecretEntry>>(text, JsonOptions)
               ?? new Dictionary<string, FileSecretEntry>(StringComparer.Ordinal);
    }

    private AppSurfaceLocalSecretResult? TryWrite(Dictionary<string, FileSecretEntry> data)
    {
        try
        {
            var posture = Write(data);
            return posture.Kind == FileSecretPostureKind.Unsupported
                ? PostureFailure(posture)
                : null;
        }
        catch (UnauthorizedAccessException)
        {
            return Failure(
                LocalSecretResultStatus.Locked,
                "local-secret-store-locked",
                "Local secret file store cannot be written.",
                "The current user cannot write the configured local secret file.",
                "Fix file permissions or choose an OS-backed store.");
        }
        catch (IOException)
        {
            return Failure(
                LocalSecretResultStatus.Unavailable,
                "local-secret-store-unavailable",
                "Local secret file store is unavailable.",
                "The configured local secret file could not be written.",
                "Close other processes using the file and retry.",
                retryable: true);
        }
    }

    private FileSecretPostureResult Write(Dictionary<string, FileSecretEntry> data) =>
        _fileSystem.WriteAllTextWithPosture(_path, JsonSerializer.Serialize(data, JsonOptions));

    private AppSurfaceLocalSecretResult Failure(
        LocalSecretResultStatus status,
        string code,
        string problem,
        string cause,
        string fix,
        bool retryable = false) =>
        AppSurfaceLocalSecretResult.NotFound(
            status,
            new AppSurfaceLocalSecretDiagnostic(code, problem, cause, fix, "local-secrets-without-a-remote-vault", retryable),
            Name);

    private AppSurfaceLocalSecretResult PostureFailure(FileSecretPostureResult posture) =>
        AppSurfaceLocalSecretResult.NotFound(
            LocalSecretResultStatus.UnsupportedPlatform,
            posture.ToDiagnostic(),
            Name);

    private sealed record FileSecretEntry(
        string ApplicationName,
        string Environment,
        string? KeyPrefix,
        string Key,
        string Value);
}

/// <summary>
/// Provides filesystem operations for the explicit file-backed LocalSecrets store.
/// </summary>
/// <remarks>
/// The seam keeps posture checks ordered around raw secret IO: read inspection must finish before file contents are
/// opened, write preparation may create only missing directories, and doctor may repair only the fallback file itself.
/// </remarks>
internal interface IFileAppSurfaceLocalSecretStoreFileSystem
{
    /// <summary>
    /// Returns whether the configured fallback file exists.
    /// </summary>
    /// <param name="path">The normalized fallback file path.</param>
    /// <returns><see langword="true" /> when the fallback file exists.</returns>
    bool FileExists(string path);

    /// <summary>
    /// Reads the fallback JSON file after posture inspection has already succeeded.
    /// </summary>
    /// <param name="path">The normalized fallback file path.</param>
    /// <returns>The raw JSON contents.</returns>
    string ReadAllText(string path);

    /// <summary>
    /// Inspects only the path shape needed before read attempts that do not need file mode posture.
    /// </summary>
    /// <param name="path">The normalized fallback file path.</param>
    /// <returns>The path-shape posture result.</returns>
    FileSecretPostureResult InspectReadPath(string path);

    /// <summary>
    /// Inspects the existing fallback file and immediate containing directory before returning or reading secret values.
    /// </summary>
    /// <param name="path">The normalized fallback file path.</param>
    /// <returns>The existing-file posture result.</returns>
    FileSecretPostureResult InspectExistingFilePosture(string path);

    /// <summary>
    /// Prepares the fallback path for a write without reading or mutating existing parent directory permissions.
    /// </summary>
    /// <param name="path">The normalized fallback file path.</param>
    /// <returns>The write-preparation posture result.</returns>
    FileSecretPostureResult PrepareWrite(string path);

    /// <summary>
    /// Atomically writes the fallback JSON file and repairs the final file mode when the platform supports it.
    /// </summary>
    /// <param name="path">The normalized fallback file path.</param>
    /// <param name="contents">The serialized JSON contents.</param>
    /// <returns>The write posture result.</returns>
    FileSecretPostureResult WriteAllTextWithPosture(string path, string contents);

    /// <summary>
    /// Opens or creates the fallback file and reports ready, repaired, degraded, or unsupported posture.
    /// </summary>
    /// <param name="path">The normalized fallback file path.</param>
    /// <returns>The doctor posture result.</returns>
    FileSecretPostureResult Doctor(string path);
}

internal sealed class DefaultFileAppSurfaceLocalSecretStoreFileSystem : IFileAppSurfaceLocalSecretStoreFileSystem
{
    private const UnixFileMode SecretDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode SecretFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static DefaultFileAppSurfaceLocalSecretStoreFileSystem Instance { get; } = new();

    public bool FileExists(string path) => File.Exists(path);

    public string ReadAllText(string path) => File.ReadAllText(path);

    public FileSecretPostureResult InspectReadPath(string path) => ValidateExistingPathShape(path, finalMustBeFile: true);

    public FileSecretPostureResult InspectExistingFilePosture(string path)
    {
        if (!File.Exists(path))
        {
            return FileSecretPostureResult.Ready();
        }

        var shape = ValidateExistingPathShape(path, finalMustBeFile: true);
        if (shape.Kind == FileSecretPostureKind.Unsupported)
        {
            return shape;
        }

        if (!IsUnix())
        {
            return FileSecretPostureResult.Ready();
        }

        if (!IsFileModeReady(new FileInfo(path).UnixFileMode))
        {
            return FileSecretPostureResult.Unsupported(
                "local-secret-file-posture-degraded",
                "Local secret file posture is degraded.",
                "The fallback secret file does not use owner-only read/write mode bits.",
                "Run `appsurface secrets doctor` or set the secret again to repair Unix mode bits; prefer the OS-backed LocalSecrets store for normal development.");
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory) && !IsDirectoryModeReady(new DirectoryInfo(directory).UnixFileMode))
        {
            return FileSecretPostureResult.Unsupported(
                "local-secret-file-posture-degraded",
                "Local secret directory posture is degraded.",
                "The fallback secret directory does not use owner-only read/write/execute mode bits.",
                "Move the fallback file under a dedicated directory that AppSurface can create, or choose the OS-backed LocalSecrets store.");
        }

        return FileSecretPostureResult.Ready();
    }

    public FileSecretPostureResult PrepareWrite(string path)
    {
        var shape = ValidateExistingPathShape(path, finalMustBeFile: true);
        if (shape.Kind == FileSecretPostureKind.Unsupported)
        {
            return shape;
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            var directoryPosture = PrepareDirectory(directory);
            if (directoryPosture.Kind == FileSecretPostureKind.Unsupported)
            {
                return directoryPosture;
            }
        }

        if (!IsUnix())
        {
            return FileSecretPostureResult.Degraded();
        }

        return File.Exists(path) && RepairFileMode(path)
            ? FileSecretPostureResult.Repaired()
            : FileSecretPostureResult.Ready();
    }

    public FileSecretPostureResult WriteAllTextWithPosture(string path, string contents)
    {
        var preflight = PrepareWrite(path);
        if (preflight.Kind == FileSecretPostureKind.Unsupported)
        {
            return preflight;
        }

        var directory = Path.GetDirectoryName(path);
        var tempPath = Path.Join(string.IsNullOrWhiteSpace(directory) ? "." : directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (IsUnix())
            {
                new FileInfo(tempPath).UnixFileMode = SecretFileMode;
            }

            var shape = ValidateExistingPathShape(path, finalMustBeFile: true);
            if (shape.Kind == FileSecretPostureKind.Unsupported)
            {
                return shape;
            }

            File.Move(tempPath, path, overwrite: true);
            if (!IsUnix())
            {
                return FileSecretPostureResult.Degraded();
            }

            var repaired = RepairFileMode(path);
            return repaired ? FileSecretPostureResult.Repaired() : FileSecretPostureResult.Ready();
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    public FileSecretPostureResult Doctor(string path)
    {
        var preflight = PrepareWrite(path);
        if (preflight.Kind == FileSecretPostureKind.Unsupported)
        {
            return preflight;
        }

        var fileExisted = File.Exists(path);
        using (new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
        }

        if (!IsUnix())
        {
            return FileSecretPostureResult.Degraded();
        }

        var repaired = preflight.Kind == FileSecretPostureKind.Repaired;
        if (fileExisted)
        {
            repaired |= RepairFileMode(path);
            return repaired ? FileSecretPostureResult.Repaired() : FileSecretPostureResult.Ready();
        }

        new FileInfo(path).UnixFileMode = SecretFileMode;
        return FileSecretPostureResult.Ready();
    }

    private static FileSecretPostureResult PrepareDirectory(string directory)
    {
        if (IsUnix() && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory, SecretDirectoryMode);
            return FileSecretPostureResult.Ready();
        }

        Directory.CreateDirectory(directory);
        if (!IsUnix())
        {
            return FileSecretPostureResult.Degraded();
        }

        return IsDirectoryModeReady(new DirectoryInfo(directory).UnixFileMode)
            ? FileSecretPostureResult.Ready()
            : FileSecretPostureResult.Unsupported(
                "local-secret-file-posture-degraded",
                "Local secret directory posture is degraded.",
                "The fallback secret directory already exists without owner-only read/write/execute mode bits.",
                "Move the fallback file under a dedicated directory that AppSurface can create, or choose the OS-backed LocalSecrets store.");
    }

    private static FileSecretPostureResult ValidateExistingPathShape(string path, bool finalMustBeFile)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            var parent = ValidateDirectoryAncestors(directory);
            if (parent.Kind == FileSecretPostureKind.Unsupported)
            {
                return parent;
            }
        }

        var final = GetExistingFileSystemInfo(fullPath);
        if (final == null)
        {
            return FileSecretPostureResult.Ready();
        }

        if (IsSymbolicLink(final))
        {
            return FileSecretPostureResult.Unsupported(
                "local-secret-file-posture-unsupported",
                "Local secret file path is unsupported.",
                "The fallback secret file path uses a symbolic link.",
                "Use a normal per-user file path or the OS-backed LocalSecrets store.");
        }

        if (final is DirectoryInfo && finalMustBeFile)
        {
            return FileSecretPostureResult.Unsupported(
                "local-secret-file-posture-unsupported",
                "Local secret file path is unsupported.",
                "The configured fallback secret path is a directory.",
                "Choose a JSON file path outside source control or use the OS-backed LocalSecrets store.");
        }

        return FileSecretPostureResult.Ready();
    }

    private static FileSecretPostureResult ValidateDirectoryAncestors(string directory)
    {
        var fullDirectory = Path.GetFullPath(directory);
        var root = Path.GetPathRoot(fullDirectory);
        if (string.IsNullOrEmpty(root))
        {
            return FileSecretPostureResult.Ready();
        }

        var trimmedRoot = Path.TrimEndingDirectorySeparator(root);
        var current = string.IsNullOrEmpty(trimmedRoot) ? root : trimmedRoot;

        var relative = Path.GetRelativePath(root, fullDirectory);
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (string.IsNullOrWhiteSpace(segment) || segment == ".")
            {
                continue;
            }

            current = Path.Join(current, segment);
            var info = GetExistingFileSystemInfo(current);
            if (info == null)
            {
                break;
            }

            if (IsSymbolicLink(info) && !IsAllowedSystemDirectoryAlias(current, info))
            {
                return FileSecretPostureResult.Unsupported(
                    "local-secret-file-posture-unsupported",
                    "Local secret directory path is unsupported.",
                    "A fallback secret directory component uses a symbolic link.",
                    "Use a normal per-user directory path or the OS-backed LocalSecrets store.");
            }

            if (info is not DirectoryInfo)
            {
                return FileSecretPostureResult.Unsupported(
                    "local-secret-file-posture-unsupported",
                    "Local secret directory path is unsupported.",
                    "A fallback secret path component is not a directory.",
                    "Choose a JSON file path under normal per-user directories or use the OS-backed LocalSecrets store.");
            }
        }

        return FileSecretPostureResult.Ready();
    }

    private static bool IsAllowedSystemDirectoryAlias(string path, FileSystemInfo info)
    {
        if (!OperatingSystem.IsMacOS() || info.LinkTarget is not { } target)
        {
            return false;
        }

        var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var normalizedTarget = Path.TrimEndingDirectorySeparator(target.StartsWith(Path.DirectorySeparatorChar)
            ? target
            : Path.Join(Path.GetPathRoot(normalizedPath), target));
        return normalizedPath == "/var" && normalizedTarget == "/private/var"
            || normalizedPath == "/tmp" && normalizedTarget == "/private/tmp";
    }

    private static FileSystemInfo? GetExistingFileSystemInfo(string path)
    {
        var directory = new DirectoryInfo(path);
        if (directory.Exists || directory.LinkTarget != null)
        {
            return directory;
        }

        var file = new FileInfo(path);
        if (file.Exists || file.LinkTarget != null)
        {
            return file;
        }

        return null;
    }

    private static bool IsSymbolicLink(FileSystemInfo info) =>
        info.LinkTarget != null || info.Attributes.HasFlag(FileAttributes.ReparsePoint);

    [UnsupportedOSPlatform("windows")]
    private static bool RepairFileMode(string path)
    {
        var info = new FileInfo(path);
        if (IsFileModeReady(info.UnixFileMode))
        {
            return false;
        }

        info.UnixFileMode = SecretFileMode;
        return true;
    }

    private static bool IsDirectoryModeReady(UnixFileMode mode) => mode == SecretDirectoryMode;

    private static bool IsFileModeReady(UnixFileMode mode) => mode == SecretFileMode;

    [UnsupportedOSPlatformGuard("windows")]
    private static bool IsUnix() => !OperatingSystem.IsWindows();
}

/// <summary>
/// Describes the posture outcome for the explicit file fallback.
/// </summary>
internal enum FileSecretPostureKind
{
    /// <summary>
    /// The fallback path is ready and did not require changes.
    /// </summary>
    Ready,

    /// <summary>
    /// The fallback file was repaired to the required owner-only mode.
    /// </summary>
    Repaired,

    /// <summary>
    /// The fallback can be opened, but this platform cannot prove owner-only posture in v1.
    /// </summary>
    Degraded,

    /// <summary>
    /// The fallback path is unsafe or permanently outside the supported posture contract.
    /// </summary>
    Unsupported,
}

/// <summary>
/// Carries a posture outcome and the value-safe diagnostic text shown by LocalSecrets commands.
/// </summary>
/// <param name="Kind">The posture outcome kind.</param>
/// <param name="Code">The stable diagnostic code.</param>
/// <param name="Problem">The value-safe problem summary.</param>
/// <param name="Cause">The value-safe cause.</param>
/// <param name="Fix">The recommended fix.</param>
/// <param name="Retryable">Whether retrying without changes may succeed.</param>
internal sealed record FileSecretPostureResult(
    FileSecretPostureKind Kind,
    string Code,
    string Problem,
    string Cause,
    string Fix,
    bool Retryable = false)
{
    /// <summary>
    /// Creates a ready posture result.
    /// </summary>
    /// <returns>A ready posture result.</returns>
    public static FileSecretPostureResult Ready() =>
        new(
            FileSecretPostureKind.Ready,
            "local-secret-store-ready",
            "Local secret file store is ready.",
            "The configured file can be opened and its fallback posture is ready.",
            "Use this store only for deterministic local examples or tests; prefer the OS-backed store for normal local development.");

    /// <summary>
    /// Creates a repaired posture result for fallback files tightened by write or doctor.
    /// </summary>
    /// <returns>A repaired posture result.</returns>
    public static FileSecretPostureResult Repaired() =>
        new(
            FileSecretPostureKind.Repaired,
            "local-secret-file-posture-repaired",
            "Local secret file posture was repaired.",
            "The fallback secret file was opened and tightened to owner-only Unix mode bits.",
            "No action is required. Prefer the OS-backed LocalSecrets store for normal local development.");

    /// <summary>
    /// Creates a degraded posture result for platforms where owner-only posture cannot be proven in v1.
    /// </summary>
    /// <returns>A degraded posture result.</returns>
    public static FileSecretPostureResult Degraded() =>
        new(
            FileSecretPostureKind.Degraded,
            "local-secret-file-posture-degraded",
            "Local secret file posture is degraded.",
            "The file fallback can be opened, but this platform path does not prove owner-only filesystem posture in v1.",
            "Use the OS-backed LocalSecrets store for normal local development, or treat this file fallback as a deterministic local/test escape hatch.");

    /// <summary>
    /// Creates an unsupported posture result for unsafe path shapes or permanent posture violations.
    /// </summary>
    /// <param name="code">The stable diagnostic code.</param>
    /// <param name="problem">The value-safe problem summary.</param>
    /// <param name="cause">The value-safe cause.</param>
    /// <param name="fix">The recommended fix.</param>
    /// <returns>An unsupported posture result.</returns>
    public static FileSecretPostureResult Unsupported(
        string code,
        string problem,
        string cause,
        string fix) =>
        new(FileSecretPostureKind.Unsupported, code, problem, cause, fix);

    /// <summary>
    /// Converts the posture result to a LocalSecrets diagnostic.
    /// </summary>
    /// <returns>The value-safe LocalSecrets diagnostic.</returns>
    public AppSurfaceLocalSecretDiagnostic ToDiagnostic() =>
        new(Code, Problem, Cause, Fix, "local-secrets-without-a-remote-vault", Retryable);
}
