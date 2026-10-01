using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>One reviewed dispatcher/runtime role identity and its dispatcher capability profile.</summary>
/// <remarks>
/// Names are preserved exactly as supplied. PostgreSQL role names are compared ordinally; values are not trimmed,
/// normalized, case-folded, interpolated into SQL, or shortened. See the
/// <a href="../../Durable/heartbeat-retention-operations.md#deploy-schema-11">schema-11 operations guide</a> for
/// manifest custody and the deployment workflow.
/// </remarks>
internal sealed record DurableRolePair(string Dispatcher, string Runtime, string DispatcherProfile);

/// <summary>A bounded immutable copy of the reviewed version-1 role manifest and its exact-byte SHA-256 digest.</summary>
/// <remarks>
/// <see cref="ReadAsync(string, CancellationToken)"/> reads at most 64 KiB once from the selected file and hashes
/// those exact bytes. <see cref="Parse(ReadOnlySpan{byte})"/> is the equivalent parser seam for internal callers
/// and tests; it does not retain or expose the input buffer. Pairs are ordered as reviewed and are immutable.
/// The format is deliberately complete even for one-pair stores: version 1, one to 32 pairs, and exactly the three
/// pair fields. See the <a href="../../Durable/heartbeat-retention-operations.md#deploy-schema-11">canonical
/// operations guide</a> for when this manifest is used and its release/activation limits.
/// </remarks>
internal sealed class DurableRoleManifest
{
    internal const int MaximumBytes = 65_536;
    internal const int MaximumPairs = 32;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private DurableRoleManifest(ImmutableArray<DurableRolePair> pairs, string sha256)
    {
        Pairs = pairs;
        Sha256 = sha256;
    }

    /// <summary>Gets the ordered, immutable role pairs exactly as declared in the reviewed input.</summary>
    public ImmutableArray<DurableRolePair> Pairs { get; }

    /// <summary>Gets the lowercase hexadecimal SHA-256 of the original manifest bytes, including whitespace.</summary>
    public string Sha256 { get; }

    /// <summary>Reads one bounded manifest file and validates its complete version-1 input before a connection opens.</summary>
    /// <param name="path">Path to the reviewed UTF-8 JSON manifest.</param>
    /// <param name="cancellationToken">Cancellation requested while opening or reading the file.</param>
    /// <returns>An immutable manifest with a digest over the exact bytes read.</returns>
    /// <exception cref="ArgumentException">The path cannot be read or the file exceeds the supported input size.</exception>
    /// <exception cref="ArgumentException">The bytes are not a valid manifest; messages are fixed and contain no path or input text.</exception>
    /// <exception cref="OperationCanceledException">The read was cancelled.</exception>
    public static async ValueTask<DurableRoleManifest> ReadAsync(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(path))
        {
            throw InputError("Manifest file could not be read.");
        }

        try
        {
            await using var stream = OpenManifestStream(path);
            return await ReadStreamAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            throw InputError("Manifest file could not be read.");
        }
    }

    /// <summary>Reads and validates a bounded manifest from a caller-owned stream.</summary>
    /// <remarks>
    /// This internal file-boundary seam reads forward once and never disposes, seeks or retains the stream.
    /// It permits deterministic partial-read cancellation and I/O failure verification without changing
    /// the public CLI input contract. Parsing occurs only after a complete bounded read; failures contain
    /// neither stream contents nor provider exception text. The caller owns stream cleanup.
    /// </remarks>
    /// <param name="stream">Readable stream positioned at the start of the complete UTF-8 manifest.</param>
    /// <param name="cancellationToken">Cancels before or during a read; partial bytes are never accepted.</param>
    /// <returns>The same immutable manifest and exact-byte digest as the path-based reader.</returns>
    /// <exception cref="ArgumentNullException">The stream is null.</exception>
    /// <exception cref="ArgumentException">The read fails, exceeds 64 KiB, or contains an invalid manifest.</exception>
    /// <exception cref="OperationCanceledException">The read is cancelled.</exception>
    internal static async ValueTask<DurableRoleManifest> ReadStreamAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] bytes;
        try
        {
            var buffer = new byte[MaximumBytes + 1];
            var length = 0;
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (read == 0)
                {
                    break;
                }

                length += read;
            }

            bytes = buffer[..length];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            throw InputError("Manifest file could not be read.");
        }

        if (bytes.Length > MaximumBytes)
        {
            throw InputError("Manifest exceeds the 65536-byte limit.");
        }

        return Parse(bytes);
    }

    private static FileStream OpenManifestStream(string path)
    {
        try
        {
            return new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            throw InputError("Manifest file could not be read.");
        }
    }

    /// <summary>Validates strict UTF-8 JSON and constructs an immutable manifest without retaining the input memory.</summary>
    /// <param name="utf8Json">The complete JSON document bytes.</param>
    /// <returns>Validated ordered role pairs and the SHA-256 of exactly <paramref name="utf8Json"/>.</returns>
    /// <exception cref="ArgumentException">The document violates the fixed version-1 input contract.</exception>
    public static DurableRoleManifest Parse(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length > MaximumBytes)
        {
            throw InputError("Manifest exceeds the 65536-byte limit.");
        }

        // Freeze the caller buffer before validation and hashing so concurrent caller mutation cannot
        // bind parsed roles to different bytes. This private copy is never exposed.
        utf8Json = utf8Json.ToArray();

        if (utf8Json.Length >= 3
            && utf8Json[0] == 0xEF
            && utf8Json[1] == 0xBB
            && utf8Json[2] == 0xBF)
        {
            throw InputError("Manifest must be strict UTF-8 JSON without a byte-order mark.");
        }

        try
        {
            _ = StrictUtf8.GetCharCount(utf8Json);
        }
        catch (DecoderFallbackException)
        {
            throw InputError("Manifest must be strict UTF-8 JSON without a byte-order mark.");
        }

        try
        {
            ValidateJsonTokens(utf8Json);
            using var document = JsonDocument.Parse(utf8Json.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw InputError("Manifest shape is invalid.");
            }

            var rootFields = ReadFields(document.RootElement, ["version", "pairs"], "Manifest shape is invalid.");
            if (rootFields["version"].ValueKind != JsonValueKind.Number
                || !rootFields["version"].TryGetInt32(out var version)
                || version != 1
                || rootFields["pairs"].ValueKind != JsonValueKind.Array)
            {
                throw InputError("Manifest shape is invalid.");
            }

            var pairsElement = rootFields["pairs"];
            if (pairsElement.GetArrayLength() is < 1 or > MaximumPairs)
            {
                throw InputError("Manifest must contain between 1 and 32 role pairs.");
            }

            var pairs = ImmutableArray.CreateBuilder<DurableRolePair>(pairsElement.GetArrayLength());
            var roles = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pairElement in pairsElement.EnumerateArray())
            {
                if (pairElement.ValueKind != JsonValueKind.Object)
                {
                    throw InputError("Manifest shape is invalid.");
                }

                var fields = ReadFields(pairElement, ["dispatcher", "runtime", "dispatcher_profile"], "Manifest shape is invalid.");
                var dispatcher = ReadRole(fields["dispatcher"]);
                var runtime = ReadRole(fields["runtime"]);
                var profile = fields["dispatcher_profile"];
                if (profile.ValueKind != JsonValueKind.String)
                {
                    throw InputError("Manifest shape is invalid.");
                }

                var profileName = profile.GetString();
                if (profileName is not ("full" or "work_only"))
                {
                    throw InputError("Manifest shape is invalid.");
                }

                if (!roles.Add(dispatcher) || !roles.Add(runtime))
                {
                    throw InputError("Manifest role names must be globally unique.");
                }

                pairs.Add(new DurableRolePair(dispatcher, runtime, profileName));
            }

            var digest = Convert.ToHexString(SHA256.HashData(utf8Json)).ToLowerInvariant();
            return new DurableRoleManifest(pairs.MoveToImmutable(), digest);
        }
        catch (JsonException)
        {
            throw InputError("Manifest must be valid strict UTF-8 JSON (depth at most 8, no comments or trailing commas).");
        }
        catch (DecoderFallbackException)
        {
            throw InputError("Manifest contains an invalid Unicode string.");
        }
    }

    internal static string ValidateRoleName(string? value, string errorMessage)
    {
        if (string.IsNullOrEmpty(value) || value.Any(char.IsControl))
        {
            throw InputError(errorMessage);
        }

        try
        {
            var byteCount = StrictUtf8.GetByteCount(value);
            if (byteCount is < 1 or > 63)
            {
                throw InputError(errorMessage);
            }
        }
        catch (EncoderFallbackException)
        {
            throw InputError(errorMessage);
        }

        return value;
    }

    private static Dictionary<string, JsonElement> ReadFields(JsonElement element, string[] expected, string errorMessage)
    {
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            var name = property.Name;
            if (!expected.Contains(name, StringComparer.Ordinal))
            {
                throw InputError(errorMessage);
            }

            fields.Add(name, property.Value);
        }

        if (fields.Count != expected.Length)
        {
            throw InputError(errorMessage);
        }

        return fields;
    }

    private static void ValidateJsonTokens(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 8,
        });
        var containers = new Stack<HashSet<string>?>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    containers.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;
                case JsonTokenType.StartArray:
                    containers.Push(null);
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    containers.Pop();
                    break;
                case JsonTokenType.PropertyName:
                case JsonTokenType.String:
                    if (reader.ValueIsEscaped)
                    {
                        ValidateUtf16Escapes(reader.ValueSpan);
                    }

                    if (reader.TokenType == JsonTokenType.PropertyName)
                    {
                        var name = reader.GetString()!;
                        var names = containers.Peek();
                        if (names is null || !names.Add(name))
                        {
                            throw InputError("Manifest contains duplicate object properties.");
                        }
                    }

                    break;
            }
        }
    }

    private static void ValidateUtf16Escapes(ReadOnlySpan<byte> rawString)
    {
        for (var index = 0; index < rawString.Length; index++)
        {
            if (rawString[index] != (byte)'\\')
            {
                continue;
            }

            if (index + 1 >= rawString.Length || rawString[index + 1] != (byte)'u')
            {
                index++;
                continue;
            }

            var codeUnit = ReadHexCodeUnit(rawString, index + 2);
            if (char.IsLowSurrogate((char)codeUnit))
            {
                throw InputError("Manifest contains an invalid Unicode string.");
            }

            if (!char.IsHighSurrogate((char)codeUnit))
            {
                index += 5;
                continue;
            }

            var nextEscape = index + 6;
            if (nextEscape + 6 > rawString.Length
                || rawString[nextEscape] != (byte)'\\'
                || rawString[nextEscape + 1] != (byte)'u'
                || !char.IsLowSurrogate((char)ReadHexCodeUnit(rawString, nextEscape + 2)))
            {
                throw InputError("Manifest contains an invalid Unicode string.");
            }

            index = nextEscape + 5;
        }
    }

    private static int ReadHexCodeUnit(ReadOnlySpan<byte> bytes, int start)
    {
        var value = 0;
        for (var offset = 0; offset < 4; offset++)
        {
            var digit = bytes[start + offset] switch
            {
                >= (byte)'0' and <= (byte)'9' => bytes[start + offset] - (byte)'0',
                >= (byte)'a' and <= (byte)'f' => bytes[start + offset] - (byte)'a' + 10,
                >= (byte)'A' and <= (byte)'F' => bytes[start + offset] - (byte)'A' + 10,
                _ => throw InputError("Manifest must be valid strict UTF-8 JSON (depth at most 8, no comments or trailing commas)."),
            };
            value = (value << 4) | digit;
        }

        return value;
    }

    private static string ReadRole(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            throw InputError("Manifest role name is invalid.");
        }

        return ValidateRoleName(element.GetString(), "Manifest role name is invalid.");
    }

    private static ArgumentException InputError(string message) => new(message);
}

/// <summary>An immutable manifest plus the independently reviewed migration-owner role used for one preflight.</summary>
/// <remarks>
/// The owner is deliberately supplied separately from the manifest, validated by the same exact UTF-8 byte and
/// control-character rules, and must not equal any dispatcher or runtime name. Neither value is inferred from the
/// catalog. The caller retains ownership of database connections and the continuous maintenance guard; this request
/// owns only its immutable manifest reference and validated owner string. See the
/// <a href="../../Durable/heartbeat-retention-operations.md#deploy-schema-11">schema-11 operations guide</a> for
/// ordering, custody and deployment evidence requirements.
/// </remarks>
internal sealed class DurablePreflightRequest
{
    /// <summary>Creates a request after validating the independently supplied owner and pairwise disjointness.</summary>
    /// <param name="manifest">The complete immutable reviewed manifest.</param>
    /// <param name="migrationOwnerRole">The exact reviewed migration-owner role name.</param>
    /// <exception cref="ArgumentNullException">The manifest is null.</exception>
    /// <exception cref="ArgumentException">The owner name is invalid or overlaps a manifest role.</exception>
    public DurablePreflightRequest(DurableRoleManifest manifest, string migrationOwnerRole)
    {
        Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        MigrationOwnerRole = DurableRoleManifest.ValidateRoleName(
            migrationOwnerRole,
            "Migration-owner role name is invalid.");
        if (manifest.Pairs.Any(pair => string.Equals(pair.Dispatcher, MigrationOwnerRole, StringComparison.Ordinal)
            || string.Equals(pair.Runtime, MigrationOwnerRole, StringComparison.Ordinal)))
        {
            throw new ArgumentException("Migration-owner role must be distinct from every manifest role.");
        }
    }

    /// <summary>Gets the complete immutable reviewed manifest.</summary>
    public DurableRoleManifest Manifest { get; }

    /// <summary>Gets the exact validated migration-owner role name.</summary>
    public string MigrationOwnerRole { get; }
}
