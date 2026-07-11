using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using System.Text;
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
public sealed class FileAppSurfaceLocalSecretStore : IAppSurfaceLocalSecretStore, IAppSurfaceLocalSecretMetadataStore
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

            if (!TryInspectExistingFilePosture(out var posture, out failure))
            {
                return failure;
            }

            if (posture.Kind == FileSecretPostureKind.Unsupported)
            {
                return PostureFailure(posture);
            }

            return data.TryGetValue(identity.StorageName, out var entry)
                ? AppSurfaceLocalSecretResult.Found(entry.Value, Name)
                : AppSurfaceLocalSecretResult.Missing(Name);
        }
    }

    /// <inheritdoc />
    public AppSurfaceLocalSecretResult Set(AppSurfaceLocalSecretIdentity identity, string value)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(value);

        lock (_gate)
        {
            if (!TryPrepareWrite(out var preflight, out var failure))
            {
                return failure;
            }

            if (preflight.Kind == FileSecretPostureKind.Unsupported)
            {
                return PostureFailure(preflight);
            }

            if (!TryRead(out var data, out failure))
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
            if (!TryPrepareWrite(out var preflight, out var failure))
            {
                return failure;
            }

            if (preflight.Kind == FileSecretPostureKind.Unsupported)
            {
                return PostureFailure(preflight);
            }

            if (!TryRead(out var data, out failure))
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
    public AppSurfaceLocalSecretResult Probe(AppSurfaceLocalSecretIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        lock (_gate)
        {
            if (!_fileSystem.FileExists(_path))
            {
                return AppSurfaceLocalSecretResult.Missing(Name);
            }

            if (!TryInspectExistingFilePosture(out var posture, out var failure))
            {
                return failure;
            }

            if (posture.Kind == FileSecretPostureKind.Unsupported)
            {
                return PostureFailure(posture);
            }

            try
            {
                return ContainsStorageName(identity.StorageName)
                    ? AppSurfaceLocalSecretResult.Found(string.Empty, Name)
                    : AppSurfaceLocalSecretResult.Missing(Name);
            }
            catch (JsonException)
            {
                return InvalidStoreFailure();
            }
            catch (UnauthorizedAccessException)
            {
                return ReadLockedFailure();
            }
            catch (IOException)
            {
                return ReadUnavailableFailure();
            }
        }
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

            if (!TryInspectExistingFilePosture(out var posture, out failure))
            {
                return AppSurfaceLocalSecretListResult.Failed(failure.Status, failure.Diagnostic!, Name);
            }

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
            if (!TryInspectExistingFilePosture(out var posture, out failure))
            {
                data = [];
                return false;
            }

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
            failure = InvalidStoreFailure();
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            data = [];
            failure = ReadLockedFailure();
            return false;
        }
        catch (IOException)
        {
            data = [];
            failure = ReadUnavailableFailure();
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

    private bool ContainsStorageName(string storageName)
    {
        using var stream = _fileSystem.OpenRead(_path);
        var serializedStorageName = JsonSerializer.SerializeToUtf8Bytes(storageName);
        return ContainsTopLevelProperty(stream, serializedStorageName.AsSpan(1, serializedStorageName.Length - 2));
    }

    private static bool ContainsTopLevelProperty(Stream stream, ReadOnlySpan<byte> propertyName)
    {
        var reader = new JsonByteReader(stream);
        var next = reader.ReadNextNonWhitespace();
        if (next < 0)
        {
            return false;
        }

        if (next != '{')
        {
            throw new JsonException("Local secret file store root must be a JSON object.");
        }

        while (true)
        {
            next = reader.ReadNextNonWhitespace();
            if (next == '}')
            {
                EnsureEndOfJson(reader);
                return false;
            }

            if (next != '"')
            {
                throw new JsonException("Local secret file store root contains an invalid property name.");
            }

            var matched = ReadJsonStringEquals(reader, propertyName);
            next = reader.ReadNextNonWhitespace();
            if (next != ':')
            {
                throw new JsonException("Local secret file store root contains an invalid property separator.");
            }

            var valueStart = reader.ReadNextNonWhitespace();
            if (valueStart != '{')
            {
                throw new JsonException("Local secret file store entry must be a JSON object.");
            }

            reader.Unread(valueStart);
            if (matched)
            {
                return true;
            }

            SkipJsonValue(reader);
            var delimiter = reader.ReadNextNonWhitespace();
            if (delimiter == ',')
            {
                continue;
            }

            if (delimiter == '}')
            {
                EnsureEndOfJson(reader);
                return false;
            }

            throw new JsonException("Local secret file store root contains an invalid value delimiter.");
        }
    }

    private static bool ReadJsonStringEquals(JsonByteReader reader, ReadOnlySpan<byte> expected)
    {
        var matched = true;
        var index = 0;
        while (true)
        {
            var next = reader.Read();
            if (next < 0)
            {
                throw new JsonException("Local secret file store string is unterminated.");
            }

            if (next == '"')
            {
                return matched && index == expected.Length;
            }

            if (next == '\\')
            {
                MatchJsonStringByte(next, expected, ref matched, ref index);
                ReadJsonEscapeAndMatch(reader, expected, ref matched, ref index);
                continue;
            }

            if (next < 0x20)
            {
                throw new JsonException("Local secret file store string contains an invalid control character.");
            }

            MatchJsonStringByte(next, expected, ref matched, ref index);
        }
    }

    private static void MatchJsonStringByte(int value, ReadOnlySpan<byte> expected, ref bool matched, ref int index)
    {
        if (matched && (index >= expected.Length || expected[index] != (byte)value))
        {
            matched = false;
        }

        index++;
    }

    private static void ReadJsonEscapeAndMatch(JsonByteReader reader, ReadOnlySpan<byte> expected, ref bool matched, ref int index)
    {
        var escaped = reader.Read();
        if (escaped < 0)
        {
            throw new JsonException("Local secret file store escape sequence is unterminated.");
        }

        MatchJsonStringByte(escaped, expected, ref matched, ref index);
        if (escaped is not ('"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't' or 'u'))
        {
            throw new JsonException("Local secret file store escape sequence is invalid.");
        }

        if (escaped == 'u')
        {
            for (var position = 0; position < 4; position++)
            {
                var hex = reader.Read();
                if (hex < 0)
                {
                    throw new JsonException("Local secret file store unicode escape sequence is unterminated.");
                }

                MatchJsonStringByte(hex, expected, ref matched, ref index);
                if (!IsJsonHexDigit(hex))
                {
                    throw new JsonException("Local secret file store unicode escape sequence is invalid.");
                }
            }
        }
    }

    private static void SkipJsonValue(JsonByteReader reader)
    {
        var next = reader.ReadNextNonWhitespace();
        if (!IsJsonValueStart(next))
        {
            throw new JsonException("Local secret file store value is missing.");
        }

        switch (next)
        {
            case '"':
                SkipJsonString(reader);
                return;
            case '{':
                SkipJsonObject(reader);
                return;
            case '[':
                SkipJsonArray(reader);
                return;
            case 't':
                ReadJsonLiteral(reader, "rue");
                return;
            case 'f':
                ReadJsonLiteral(reader, "alse");
                return;
            case 'n':
                ReadJsonLiteral(reader, "ull");
                return;
            case '-' or >= '0' and <= '9':
                SkipJsonNumber(reader, next);
                return;
            default:
                throw new JsonException("Local secret file store contains an invalid value.");
        }
    }

    private static void SkipJsonObject(JsonByteReader reader)
    {
        var next = reader.ReadNextNonWhitespace();
        if (next == '}')
        {
            return;
        }

        reader.Unread(next);
        while (true)
        {
            if (reader.ReadNextNonWhitespace() != '"')
            {
                throw new JsonException("Local secret file store object contains an invalid property name.");
            }

            SkipJsonString(reader);
            if (reader.ReadNextNonWhitespace() != ':')
            {
                throw new JsonException("Local secret file store object contains an invalid property separator.");
            }

            SkipJsonValue(reader);
            next = reader.ReadNextNonWhitespace();
            if (next == ',')
            {
                continue;
            }

            if (next == '}')
            {
                return;
            }

            throw new JsonException("Local secret file store object contains an invalid value delimiter.");
        }
    }

    private static void SkipJsonArray(JsonByteReader reader)
    {
        var next = reader.ReadNextNonWhitespace();
        if (next == ']')
        {
            return;
        }

        reader.Unread(next);
        while (true)
        {
            SkipJsonValue(reader);
            next = reader.ReadNextNonWhitespace();
            if (next == ',')
            {
                continue;
            }

            if (next == ']')
            {
                return;
            }

            throw new JsonException("Local secret file store array contains an invalid value delimiter.");
        }
    }

    private static void SkipJsonString(JsonByteReader reader)
    {
        while (true)
        {
            var next = reader.Read();
            if (next < 0)
            {
                throw new JsonException("Local secret file store string is unterminated.");
            }

            if (next == '"')
            {
                return;
            }

            if (next == '\\')
            {
                SkipJsonEscape(reader);
                continue;
            }

            if (next < 0x20)
            {
                throw new JsonException("Local secret file store string contains an invalid control character.");
            }
        }
    }

    private static void SkipJsonEscape(JsonByteReader reader)
    {
        var escaped = reader.Read();
        if (escaped < 0)
        {
            throw new JsonException("Local secret file store escape sequence is unterminated.");
        }

        if (escaped is not ('"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't' or 'u'))
        {
            throw new JsonException("Local secret file store escape sequence is invalid.");
        }

        if (escaped == 'u')
        {
            for (var index = 0; index < 4; index++)
            {
                var hex = reader.Read();
                if (hex < 0)
                {
                    throw new JsonException("Local secret file store unicode escape sequence is unterminated.");
                }

                if (!IsJsonHexDigit(hex))
                {
                    throw new JsonException("Local secret file store unicode escape sequence is invalid.");
                }
            }
        }
    }

    private static void ReadJsonLiteral(JsonByteReader reader, string literalTail)
    {
        foreach (var expected in literalTail)
        {
            if (reader.Read() != expected)
            {
                throw new JsonException("Local secret file store contains an invalid literal.");
            }
        }
    }

    private static void EnsureEndOfJson(JsonByteReader reader)
    {
        if (reader.ReadNextNonWhitespace() >= 0)
        {
            throw new JsonException("Local secret file store contains trailing JSON content.");
        }
    }

    private static void SkipJsonNumber(JsonByteReader reader, int first)
    {
        var next = first;
        if (next == '-')
        {
            next = reader.Read();
            if (next < 0)
            {
                throw new JsonException("Local secret file store number is incomplete.");
            }
        }

        if (next == '0')
        {
            next = reader.Read();
            if (next is >= '0' and <= '9')
            {
                throw new JsonException("Local secret file store number has an invalid leading zero.");
            }
        }
        else if (next is >= '1' and <= '9')
        {
            do
            {
                next = reader.Read();
            }
            while (next is >= '0' and <= '9');
        }
        else
        {
            throw new JsonException("Local secret file store number is invalid.");
        }

        if (next == '.')
        {
            next = reader.Read();
            if (next is not (>= '0' and <= '9'))
            {
                throw new JsonException("Local secret file store number fraction is invalid.");
            }

            do
            {
                next = reader.Read();
            }
            while (next is >= '0' and <= '9');
        }

        if (next is 'e' or 'E')
        {
            next = reader.Read();
            if (next is '+' or '-')
            {
                next = reader.Read();
            }

            if (next is not (>= '0' and <= '9'))
            {
                throw new JsonException("Local secret file store number exponent is invalid.");
            }

            do
            {
                next = reader.Read();
            }
            while (next is >= '0' and <= '9');
        }

        reader.Unread(next);
    }

    private static bool IsJsonWhitespace(byte value) =>
        value is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t';

    private static bool IsJsonValueStart(int value) =>
        value is '"' or '{' or '[' or 't' or 'f' or 'n' or '-' or >= '0' and <= '9';

    private static bool IsJsonHexDigit(int value) =>
        value is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f';

    private sealed class JsonByteReader(Stream stream)
    {
        private int _pushedBack = -1;

        public int Read()
        {
            if (_pushedBack < 0)
            {
                return stream.ReadByte();
            }

            var value = _pushedBack;
            _pushedBack = -1;
            return value;
        }

        public void Unread(int value)
        {
            if (value < 0)
            {
                return;
            }

            if (_pushedBack >= 0)
            {
                throw new InvalidOperationException("Only one JSON byte can be pushed back.");
            }

            _pushedBack = value;
        }

        public int ReadNextNonWhitespace()
        {
            int next;
            while ((next = Read()) >= 0)
            {
                if (!IsJsonWhitespace((byte)next))
                {
                    return next;
                }
            }

            return -1;
        }
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
            return WriteLockedFailure();
        }
        catch (IOException)
        {
            return WriteUnavailableFailure();
        }
    }

    private FileSecretPostureResult Write(Dictionary<string, FileSecretEntry> data) =>
        _fileSystem.WriteAllTextWithPosture(_path, JsonSerializer.Serialize(data, JsonOptions));

    private bool TryInspectExistingFilePosture(
        out FileSecretPostureResult posture,
        out AppSurfaceLocalSecretResult failure)
    {
        try
        {
            posture = _fileSystem.InspectExistingFilePosture(_path);
            failure = null!;
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            posture = FileSecretPostureResult.Ready();
            failure = ReadLockedFailure();
            return false;
        }
        catch (IOException)
        {
            posture = FileSecretPostureResult.Ready();
            failure = ReadUnavailableFailure();
            return false;
        }
    }

    private bool TryPrepareWrite(
        out FileSecretPostureResult posture,
        out AppSurfaceLocalSecretResult failure)
    {
        try
        {
            posture = _fileSystem.PrepareWrite(_path);
            failure = null!;
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            posture = FileSecretPostureResult.Ready();
            failure = WriteLockedFailure();
            return false;
        }
        catch (IOException)
        {
            posture = FileSecretPostureResult.Ready();
            failure = WriteUnavailableFailure();
            return false;
        }
    }

    private AppSurfaceLocalSecretResult ReadLockedFailure() =>
        Failure(
            LocalSecretResultStatus.Locked,
            "local-secret-store-locked",
            "Local secret file store cannot be read.",
            "The current user cannot read the configured local secret file.",
            "Fix file permissions or choose an OS-backed store.");

    private AppSurfaceLocalSecretResult ReadUnavailableFailure() =>
        Failure(
            LocalSecretResultStatus.Unavailable,
            "local-secret-store-unavailable",
            "Local secret file store is unavailable.",
            "The configured local secret file could not be read.",
            "Close other processes using the file and retry.",
            retryable: true);

    private AppSurfaceLocalSecretResult InvalidStoreFailure() =>
        Failure(
            LocalSecretResultStatus.ProviderFailed,
            "local-secret-store-invalid",
            "Local secret file store is invalid.",
            "The configured local secret file could not be parsed.",
            "Delete and recreate the LocalSecrets namespace with `appsurface secrets init`.");

    private AppSurfaceLocalSecretResult WriteLockedFailure() =>
        Failure(
            LocalSecretResultStatus.Locked,
            "local-secret-store-locked",
            "Local secret file store cannot be written.",
            "The current user cannot write the configured local secret file.",
            "Fix file permissions or choose an OS-backed store.");

    private AppSurfaceLocalSecretResult WriteUnavailableFailure() =>
        Failure(
            LocalSecretResultStatus.Unavailable,
            "local-secret-store-unavailable",
            "Local secret file store is unavailable.",
            "The configured local secret file could not be written.",
            "Close other processes using the file and retry.",
            retryable: true);

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
    /// Opens the fallback JSON file for metadata-only streaming scans after posture inspection has already succeeded.
    /// </summary>
    /// <param name="path">The normalized fallback file path.</param>
    /// <returns>A readable stream for the raw JSON contents.</returns>
    Stream OpenRead(string path);

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

/// <summary>
/// Default filesystem adapter for the explicit file-backed LocalSecrets store.
/// </summary>
/// <remarks>
/// The adapter validates path shape, Unix mode posture, and write ordering without broad filesystem mutation. Missing
/// fallback directories may be created, but existing loose directories are reported for the caller to fix deliberately.
/// </remarks>
internal sealed class DefaultFileAppSurfaceLocalSecretStoreFileSystem : IFileAppSurfaceLocalSecretStoreFileSystem
{
    private const UnixFileMode SecretDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode SecretFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>
    /// Gets the singleton production adapter.
    /// </summary>
    public static DefaultFileAppSurfaceLocalSecretStoreFileSystem Instance { get; } = new();

    private readonly Func<bool> _isUnix;
    private readonly Func<bool> _isMacOs;
    private readonly Action<string>? _beforeMove;
    private readonly Action<string>? _afterDirectoryCreate;

    private DefaultFileAppSurfaceLocalSecretStoreFileSystem()
        : this(() => !OperatingSystem.IsWindows(), OperatingSystem.IsMacOS)
    {
    }

    /// <summary>
    /// Initializes a new instance with platform probes for deterministic posture tests.
    /// </summary>
    /// <param name="isUnix">Returns whether Unix mode checks are available.</param>
    /// <param name="isMacOs">Returns whether macOS directory-alias exceptions should apply.</param>
    /// <param name="beforeMove">Optional deterministic test hook invoked after the temporary file is ready.</param>
    /// <param name="afterDirectoryCreate">Optional deterministic test hook invoked after fallback directory creation.</param>
    internal DefaultFileAppSurfaceLocalSecretStoreFileSystem(
        Func<bool> isUnix,
        Func<bool> isMacOs,
        Action<string>? beforeMove = null,
        Action<string>? afterDirectoryCreate = null)
    {
        ArgumentNullException.ThrowIfNull(isUnix);
        ArgumentNullException.ThrowIfNull(isMacOs);

        _isUnix = isUnix;
        _isMacOs = isMacOs;
        _beforeMove = beforeMove;
        _afterDirectoryCreate = afterDirectoryCreate;
    }

    /// <inheritdoc />
    public bool FileExists(string path) => File.Exists(path);

    /// <inheritdoc />
    public string ReadAllText(string path) => File.ReadAllText(path);

    /// <inheritdoc />
    public Stream OpenRead(string path) => File.OpenRead(path);

    /// <inheritdoc />
    public FileSecretPostureResult InspectReadPath(string path) => ValidateExistingPathShape(path, finalMustBeFile: true);

    /// <inheritdoc />
    public FileSecretPostureResult InspectExistingFilePosture(string path)
    {
        var shape = ValidateExistingPathShape(path, finalMustBeFile: true);
        if (shape.Kind == FileSecretPostureKind.Unsupported)
        {
            return shape;
        }

        if (!File.Exists(path))
        {
            return FileSecretPostureResult.Ready();
        }

        if (!IsUnix())
        {
            return FileSecretPostureResult.Ready();
        }

        if (!IsFileModeReady(new FileInfo(path).UnixFileMode))
        {
            return FileSecretPostureResult.Unsupported(
                "local-secret-file-posture-unsupported",
                "Local secret file posture is unsupported.",
                "The fallback secret file does not use owner-only read/write mode bits.",
                "Run `appsurface secrets doctor` or set the secret again to repair Unix mode bits; prefer the OS-backed LocalSecrets store for normal development.");
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory) && !IsDirectoryModeReady(new DirectoryInfo(directory).UnixFileMode))
        {
            return FileSecretPostureResult.Unsupported(
                "local-secret-file-posture-unsupported",
                "Local secret directory posture is unsupported.",
                "The fallback secret directory does not use owner-only read/write/execute mode bits.",
                "Move the fallback file under a dedicated directory that AppSurface can create, or choose the OS-backed LocalSecrets store.");
        }

        return FileSecretPostureResult.Ready();
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
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
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None
            };
            SetUnixCreateModeIfSupported(options);

            using (var stream = File.Open(tempPath, options))
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

            _beforeMove?.Invoke(tempPath);

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

            RepairFileMode(path);
            return FileSecretPostureResult.Ready();
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    /// <inheritdoc />
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

    private FileSecretPostureResult PrepareDirectory(string directory)
    {
        if (IsUnix() && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory, SecretDirectoryMode);
            _afterDirectoryCreate?.Invoke(directory);

            var ancestorPosture = ValidateDirectoryAncestors(directory);
            if (ancestorPosture.Kind == FileSecretPostureKind.Unsupported)
            {
                return ancestorPosture;
            }

            return IsDirectoryModeReady(new DirectoryInfo(directory).UnixFileMode)
                ? FileSecretPostureResult.Ready()
                : FileSecretPostureResult.Unsupported(
                    "local-secret-file-posture-unsupported",
                    "Local secret directory posture is unsupported.",
                    "The fallback secret directory could not be created with owner-only read/write/execute mode bits.",
                    "Move the fallback file under a dedicated owner-only directory or choose the OS-backed LocalSecrets store.");
        }

        var directoryInfo = Directory.CreateDirectory(directory);
        if (!IsUnix())
        {
            return FileSecretPostureResult.Degraded();
        }

        return IsDirectoryModeReady(directoryInfo.UnixFileMode)
            ? FileSecretPostureResult.Ready()
            : FileSecretPostureResult.Unsupported(
                "local-secret-file-posture-unsupported",
                "Local secret directory posture is unsupported.",
                "The fallback secret directory already exists without owner-only read/write/execute mode bits.",
                "Move the fallback file under a dedicated directory that AppSurface can create, or choose the OS-backed LocalSecrets store.");
    }

    private FileSecretPostureResult ValidateExistingPathShape(string path, bool finalMustBeFile)
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

    private FileSecretPostureResult ValidateDirectoryAncestors(string directory)
    {
        var fullDirectory = Path.GetFullPath(directory);
        var root = Path.GetPathRoot(fullDirectory)!;
        var current = root;

        var relative = Path.GetRelativePath(root, fullDirectory);
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Join(current, segment);
            var info = GetExistingFileSystemInfo(current);
            if (info == null)
            {
                break;
            }

            var allowedSystemAlias = IsSymbolicLink(info) && IsAllowedSystemDirectoryAlias(current, info);
            if (IsSymbolicLink(info) && !allowedSystemAlias)
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

            if (IsUnix()
                && !allowedSystemAlias
                && info is DirectoryInfo directoryInfo
                && IsDirectoryWritableByGroupOrOther(directoryInfo.UnixFileMode)
                && !IsStickyDirectory(directoryInfo.UnixFileMode))
            {
                return FileSecretPostureResult.Unsupported(
                    "local-secret-file-posture-unsupported",
                    "Local secret directory posture is unsupported.",
                    "A fallback secret directory ancestor is writable by group or other users.",
                    "Move the fallback file under a dedicated owner-only directory or choose the OS-backed LocalSecrets store.");
            }
        }

        return FileSecretPostureResult.Ready();
    }

    [ExcludeFromCodeCoverage(Justification = "The allowed /var and /tmp alias combinations depend on macOS system symlink layout; deterministic non-system symlink rejection is covered.")]
    private bool IsAllowedSystemDirectoryAlias(string path, FileSystemInfo info)
    {
        if (!_isMacOs() || info.LinkTarget is not { } target)
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

    [ExcludeFromCodeCoverage(Justification = "This helper only guards a Unix-only file creation option from Windows; Unix write behavior is covered through WriteAllTextWithPosture.")]
    private void SetUnixCreateModeIfSupported(FileStreamOptions options)
    {
        if (IsUnix())
        {
            options.UnixCreateMode = SecretFileMode;
        }
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

    private static bool IsDirectoryWritableByGroupOrOther(UnixFileMode mode) =>
        mode.HasFlag(UnixFileMode.GroupWrite) || mode.HasFlag(UnixFileMode.OtherWrite);

    private static bool IsStickyDirectory(UnixFileMode mode) => mode.HasFlag(UnixFileMode.StickyBit);

    [UnsupportedOSPlatformGuard("windows")]
    private bool IsUnix() => _isUnix();
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
