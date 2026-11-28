using System.Text;
using System.Text.Json.Serialization;

namespace ForgeTrust.AppSurface.Auth.Keycloak;

/// <summary>Describes a run-scoped Keycloak client credential held by qualified private file or sealed-memory custody.</summary>
/// <remarks>
/// This object contains only a run directory and client identity metadata. It does not resolve the credential
/// during construction and never stores the credential. Call <see cref="Read"/> only where the existing private
/// OIDC token callback needs the value. Private-file capture access is qualified on macOS ARM64. That capture uses
/// no-follow opens, opened-file identity checks, private Unix modes, and the Darwin immutable-file flag; trusted
/// root ancestors remain required, and this does not provide full ACL, same-user, or root isolation or constitute
/// a production credential store.
/// Sealed-memory reads require the qualified Linux ARM64 supervised namespace and verify the holder's native
/// process incarnation, opened inode, identity-bound frame and irrevocable kernel seals. Neither transport
/// provides isolation from trusted host administrators or malicious code with the same OS identity.
/// </remarks>
public sealed class KeycloakClientCredentialReference
{
    private const int MaximumAuthorityBytes = 2048;
    private const int MaximumClientIdBytes = 256;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Creates a validated, serializable reference without performing filesystem I/O.</summary>
    /// <param name="runDirectory">Absolute directory containing the captured run custody data.</param>
    /// <param name="runId">Run identifier in 32-character GUID N format.</param>
    /// <param name="authority">Absolute HTTPS Keycloak realm URI without user information, query, or fragment.</param>
    /// <param name="clientId">Non-whitespace client identifier of at most 256 strict UTF-8 bytes.</param>
    /// <exception cref="ArgumentException">A declaration value is malformed or outside its documented bounds.</exception>
    /// <exception cref="ArgumentNullException">A required declaration value is null.</exception>
    public KeycloakClientCredentialReference(string runDirectory, string runId, Uri authority, string clientId)
        : this(runDirectory, runId, authority, clientId, memoryIdentity: null)
    {
    }

    /// <summary>Reconstructs a validated, metadata-only private-file or sealed-memory reference.</summary>
    /// <param name="runDirectory">Absolute private-file run directory, or the empty string for sealed memory.</param>
    /// <param name="runId">Run identifier in GUID N format.</param>
    /// <param name="authority">Exact HTTPS realm issuer bound to the capture.</param>
    /// <param name="clientId">Non-whitespace, control-free client identifier within 256 strict UTF-8 bytes.</param>
    /// <param name="memoryIdentity">Sealed-memory holder/file identity; null selects private-file custody.</param>
    /// <exception cref="ArgumentException">Metadata is malformed, ambiguous or outside the supported bounds.</exception>
    /// <exception cref="ArgumentNullException">A required metadata value is null.</exception>
    /// <remarks>No native or filesystem access occurs. A memory reference never falls back to file custody.</remarks>
    [JsonConstructor]
    public KeycloakClientCredentialReference(string runDirectory, string runId, Uri authority, string clientId,
        KeycloakClientCredentialMemoryIdentity? memoryIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(runDirectory);
        ArgumentNullException.ThrowIfNull(runId);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(clientId);

        if (memoryIdentity is null && (!Path.IsPathFullyQualified(runDirectory) || runDirectory.Any(char.IsControl)))
            throw new ArgumentException("The run directory must be an absolute path without control characters.", nameof(runDirectory));
        if (memoryIdentity is not null)
        {
            memoryIdentity.Validate();
            if (runDirectory.Length != 0)
                throw new ArgumentException("Sealed-memory references cannot also name a file directory.", nameof(runDirectory));
        }
        if (!Guid.TryParseExact(runId, "N", out _))
            throw new ArgumentException("The run identifier must use GUID N format.", nameof(runId));

        RunDirectory = runDirectory;
        RunId = runId;
        Authority = new Uri(NormalizeAuthority(authority), UriKind.Absolute);
        ValidateClientId(clientId);
        ClientId = clientId;
        MemoryIdentity = memoryIdentity;
    }

    /// <summary>Gets the absolute private-file run directory, or the empty string for sealed-memory custody.</summary>
    public string RunDirectory { get; }

    /// <summary>Gets the sealed-memory holder/file identity, or null for qualified private-file custody.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public KeycloakClientCredentialMemoryIdentity? MemoryIdentity { get; }

    /// <summary>Creates a metadata-only reference to a sealed capture held by a supervised Linux process.</summary>
    /// <param name="memoryIdentity">The exact native holder and anonymous file identity observed at capture.</param>
    /// <param name="runId">The capture's GUID N run identifier.</param>
    /// <param name="authority">The exact HTTPS realm issuer bound to the sealed frame.</param>
    /// <param name="clientId">The exact client identifier bound to the sealed frame.</param>
    /// <returns>An effect-free reference; use <see cref="Read"/> at the private OIDC configuration callback.</returns>
    /// <exception cref="ArgumentException">The metadata is invalid.</exception>
    /// <exception cref="ArgumentNullException">A required metadata value is null.</exception>
    /// <remarks>Readers must share the holder's qualified PID namespace and effective OS user. This grants no host mutation authority.</remarks>
    public static KeycloakClientCredentialReference ForSealedMemory(KeycloakClientCredentialMemoryIdentity memoryIdentity,
        string runId, Uri authority, string clientId)
    {
        ArgumentNullException.ThrowIfNull(memoryIdentity);
        return new(string.Empty, runId, authority, clientId, memoryIdentity);
    }

    /// <summary>Gets the run identifier in GUID N format.</summary>
    public string RunId { get; }

    /// <summary>Gets the validated HTTPS Keycloak realm URI bound to the capture.</summary>
    public Uri Authority { get; }

    /// <summary>Gets the client identifier bound to the capture.</summary>
    public string ClientId { get; }

    /// <summary>Reads the credential after native custody-lifetime and exact client-identity checks.</summary>
    /// <returns>The captured credential string for immediate use by a private OIDC callback.</returns>
    /// <exception cref="PlatformNotSupportedException">The selected native custody profile is not qualified on the current platform.</exception>
    /// <exception cref="InvalidDataException">The capture is expired, its holder or file changed, it is malformed, or it is bound to another identity.</exception>
    /// <exception cref="IOException">A capture file cannot be opened or verified.</exception>
    public string Read() => MemoryIdentity is null
        ? RealmClientCapture.Read(RunDirectory, RunId, Authority.AbsoluteUri, ClientId)
        : KeycloakClientCredentialMemoryCapture.Read(this);

    /// <summary>Reads the credential on a worker task after native custody and client-identity checks.</summary>
    /// <param name="cancellationToken">Cancels the queued read if it has not started.</param>
    /// <returns>A task producing the credential for immediate use by a private OIDC callback.</returns>
    /// <exception cref="PlatformNotSupportedException">The selected native custody profile is not qualified on the current platform.</exception>
    /// <exception cref="InvalidDataException">The capture is expired, changed, malformed, or bound to another identity.</exception>
    /// <exception cref="IOException">A capture file cannot be opened or verified.</exception>
    public Task<string> ReadAsync(CancellationToken cancellationToken = default) => Task.Run(Read, cancellationToken);

    internal static string NormalizeAuthority(string authority)
    {
        if (authority is null || !Uri.TryCreate(authority, UriKind.Absolute, out var parsed))
            throw new ArgumentException("The authority must be an absolute HTTPS Keycloak realm URI.", nameof(authority));
        return NormalizeAuthority(parsed);
    }

    internal static void ValidateClientId(string clientId)
    {
        if (clientId is null) throw new ArgumentNullException(nameof(clientId));
        int byteCount;
        try
        {
            byteCount = StrictUtf8.GetByteCount(clientId);
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException("The client identifier must be valid UTF-8.", nameof(clientId));
        }
        if (byteCount is < 1 or > MaximumClientIdBytes || string.IsNullOrWhiteSpace(clientId) || clientId.Any(char.IsControl))
            throw new ArgumentException("The client identifier must be non-whitespace, control-free, and at most 256 UTF-8 bytes.", nameof(clientId));
    }

    private static string NormalizeAuthority(Uri authority)
    {
        if (!authority.IsAbsoluteUri
            || !string.Equals(authority.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(authority.Host)
            || authority.UserInfo.Length != 0
            || authority.Query.Length != 0
            || authority.Fragment.Length != 0)
            throw new ArgumentException("The authority must be an absolute HTTPS Keycloak realm URI without credentials, query, or fragment.", nameof(authority));

        var segments = authority.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2
            || !string.Equals(segments[^2], "realms", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(Uri.UnescapeDataString(segments[^1]))
            || Uri.UnescapeDataString(segments[^1]).Contains('/')
            || Uri.UnescapeDataString(segments[^1]).Contains('\\'))
            throw new ArgumentException("The authority path must identify a Keycloak realm.", nameof(authority));

        string normalized;
        try
        {
            normalized = authority.AbsoluteUri;
            if (StrictUtf8.GetByteCount(normalized) is < 1 or > MaximumAuthorityBytes)
                throw new ArgumentException("The authority is outside its 2048-byte limit.", nameof(authority));
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException("The authority must be valid UTF-8.", nameof(authority));
        }
        return normalized;
    }
}

/// <summary>Identifies a sealed anonymous client-credential file held by one supervised Linux process incarnation.</summary>
/// <param name="ProcessId">Positive process identifier within the shared, task-owned PID namespace.</param>
/// <param name="ProcessStartCounter">Positive integral kernel start counter from field 22 of /proc/pid/stat; never a converted UTC timestamp.</param>
/// <param name="FileDescriptor">The holder's descriptor number, from 3 through 1,048,575.</param>
/// <param name="Inode">The nonzero inode observed for the sealed anonymous file.</param>
/// <remarks>This immutable value contains no credential. Reference construction validates it without accessing the holder or file.</remarks>
public sealed record KeycloakClientCredentialMemoryIdentity(int ProcessId, long ProcessStartCounter, int FileDescriptor, ulong Inode)
{
    /// <summary>Checks declaration bounds without native access.</summary>
    internal void Validate()
    {
        if (ProcessId <= 0 || ProcessStartCounter <= 0 || FileDescriptor is < 3 or > 1_048_575 || Inode == 0)
            throw new ArgumentException("The sealed-memory identity is invalid.", "memoryIdentity");
    }
}
