using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace ForgeTrust.AppSurface.Auth.Keycloak;

/// <summary>Internal macOS ARM64 profile for an owner-managed immutable captured client file.</summary>
/// <remarks>
/// Root ancestors remain trusted. No-follow opens anchor the private case and custody directories;
/// fstat checks the opened file identity. UF_IMMUTABLE prevents ordinary mutation but its owner can
/// remove the flag: this is not isolation from malicious code running as the same user or root.
/// </remarks>
internal static class RealmClientCapture
{
    private const int NoFollowAny = 0x20000000;
    private const int NoFollow = 0x100;
    private const int CloseOnExec = 0x1000000;
    private const int DirectoryFlag = 0x100000;
    private const uint Immutable = 2;
    private const ushort Regular = 0x8000;
    private const ushort DirectoryType = 0x4000;
    private const int MaximumCredentialBytes = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Captures one opaque UTF-8 client credential under an already held run owner.</summary>
    internal static void Capture(string root, string runId, string value) => Capture(root, runId, value, null, null);

    /// <summary>Captures one opaque UTF-8 client credential and its optional authority/client identity.</summary>
    internal static void Capture(
        string root,
        string runId,
        string value,
        string? authority = null,
        string? clientId = null)
    {
        var bytes = EncodeCredential(value);
        if (!Guid.TryParseExact(runId, "N", out _)) throw new InvalidDataException("Invalid capture run.");
        if ((authority is null) != (clientId is null)) throw new InvalidDataException("Capture identity is incomplete.");
        var normalizedAuthority = authority is null ? null : KeycloakClientCredentialReference.NormalizeAuthority(authority);
        if (clientId is not null) KeycloakClientCredentialReference.ValidateClientId(clientId);
        if (!IsSupportedPlatform()) throw UnsupportedPlatform();
        using var rootHandle = Open(root, NoFollowAny | DirectoryFlag);
        ValidateDirectory(Inspect(rootHandle));
        var directory = Path.Combine(root, "client-custody");
        if (Path.Exists(directory) || new DirectoryInfo(directory).LinkTarget is not null) throw new IOException("Capture directory exists.");
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var directoryHandle = OpenRelative(rootHandle, "client-custody", DirectoryFlag);
        ValidateDirectory(Inspect(directoryHandle));
        var path = Path.Combine(directory, "client.secret");
        using (var file = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        }))
        {
            file.Write(bytes);
            file.Flush(true);
        }
        File.SetUnixFileMode(path, UnixFileMode.UserRead);
        using var handle = Open(path, NoFollowAny);
        if (fchflags(handle, Immutable) != 0) throw new IOException("Capture sealing failed.");
        var stat = Inspect(handle);
        ValidateFile(stat);
        var identity = new Identity(
            runId,
            stat.Device,
            stat.Inode,
            stat.BirthSeconds,
            stat.BirthNanoseconds,
            DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds(),
            bytes.Length,
            normalizedAuthority,
            clientId);
        File.WriteAllText(Path.Combine(directory, "identity.json"), JsonSerializer.Serialize(identity));
        File.SetUnixFileMode(Path.Combine(directory, "identity.json"), UnixFileMode.UserRead);
    }

    /// <summary>Reads one unexpired same-run capture after verifying its opened identity and private modes.</summary>
    internal static string Read(string root, string runId) => ReadCore(root, runId, null, null, verifyIdentity: false);

    /// <summary>Reads one capture only when its stored authority and client identity match exactly.</summary>
    internal static string Read(string root, string runId, string expectedAuthority, string expectedClientId)
    {
        var normalizedAuthority = KeycloakClientCredentialReference.NormalizeAuthority(expectedAuthority);
        KeycloakClientCredentialReference.ValidateClientId(expectedClientId);
        return ReadCore(root, runId, normalizedAuthority, expectedClientId, verifyIdentity: true);
    }

    private static string ReadCore(string root, string runId, string? expectedAuthority, string? expectedClientId, bool verifyIdentity)
    {
        if (!IsSupportedPlatform()) throw UnsupportedPlatform();
        if (!Guid.TryParseExact(runId, "N", out _)) throw new InvalidDataException("Invalid capture run.");
        using var caseHandle = Open(root, NoFollowAny | DirectoryFlag);
        ValidateDirectory(Inspect(caseHandle));
        using var directory = OpenRelative(caseHandle, "client-custody", DirectoryFlag);
        ValidateDirectory(Inspect(directory));
        using var identityHandle = OpenRelative(directory, "identity.json", 0);
        var identityStat = Inspect(identityHandle);
        if ((identityStat.Mode & 0xf000) != Regular || (identityStat.Mode & 0xfff) != 0x100
            || identityStat.Owner != getuid() || identityStat.Links != 1 || identityStat.Size > 16_384)
            throw new InvalidDataException("Capture descriptor rejected.");
        using var metadata = new FileStream(identityHandle, FileAccess.Read);
        var expected = JsonSerializer.Deserialize<Identity>(metadata) ?? throw new InvalidDataException();
        if (expected.RunId != runId || DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= expected.ExpiresUnixSeconds)
            throw new InvalidDataException("Capture expired or wrong run.");
        if (expected.SecretByteLength is < 1 or > MaximumCredentialBytes)
            throw new InvalidDataException("Capture descriptor rejected.");
        if (verifyIdentity && (!string.Equals(expected.Authority, expectedAuthority, StringComparison.Ordinal)
            || !string.Equals(expected.ClientId, expectedClientId, StringComparison.Ordinal)))
            throw new InvalidDataException("Capture belongs to a different client identity.");
        using var fileHandle = OpenRelative(directory, "client.secret", 0);
        var before = Inspect(fileHandle);
        ValidateFile(before);
        if (expected.Device != before.Device || expected.Inode != before.Inode
            || expected.BirthSeconds != before.BirthSeconds || expected.BirthNanoseconds != before.BirthNanoseconds
            || expected.SecretByteLength != before.Size)
            throw new InvalidDataException("Capture incarnation changed.");
        using var file = new FileStream(fileHandle, FileAccess.Read);
        var bytes = new byte[checked((int)before.Size)];
        file.ReadExactly(bytes);
        if (file.ReadByte() != -1 || !before.Equals(Inspect(fileHandle))) throw new InvalidDataException();
        string value;
        try
        {
            value = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException("Capture credential is not valid UTF-8.");
        }
        if (!IsValidCredentialText(value)) throw new InvalidDataException("Capture credential is invalid.");
        // Never export the bytes, their hash, or a credential-derived acknowledgment.
        return value;
    }

    /// <summary>Removes the immutable flag only after the caller proves all owned readers have stopped.</summary>
    internal static void UnsealForOwnedCleanup(string root)
    {
        if (!IsSupportedPlatform()) throw UnsupportedPlatform();
        var path = Path.Combine(root, "client-custody", "client.secret");
        if (new FileInfo(path).LinkTarget is not null) return; // Delete the owned link, never unseal its target.
        if (!File.Exists(path)) return;
        using var file = Open(path, NoFollowAny);
        var stat = Inspect(file);
        if (stat.Owner != getuid() || (stat.Mode & 0xf000) != Regular) throw new InvalidDataException();
        if (fchflags(file, stat.Flags & ~Immutable) != 0) throw new IOException("Owned unseal failed.");
    }

    private static void ValidateFile(Stat stat)
    {
        if ((stat.Mode & 0xf000) != Regular || (stat.Mode & 0xfff) != 0x100 || stat.Owner != getuid()
            || stat.Links != 1 || stat.Size is < 1 or > MaximumCredentialBytes || (stat.Flags & Immutable) == 0)
            throw new InvalidDataException("Capture file rejected.");
    }

    private static byte[] EncodeCredential(string value)
    {
        if (value is null) throw new InvalidDataException("Capture credential is invalid.");
        byte[] bytes;
        try
        {
            bytes = StrictUtf8.GetBytes(value);
        }
        catch (EncoderFallbackException)
        {
            throw new InvalidDataException("Capture credential is not valid UTF-8.");
        }
        if (bytes.Length is < 1 or > MaximumCredentialBytes || !IsValidCredentialText(value))
            throw new InvalidDataException("Capture credential is invalid.");
        return bytes;
    }

    private static bool IsValidCredentialText(string value) =>
        !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);

    private static void ValidateDirectory(Stat stat)
    {
        if ((stat.Mode & 0xf000) != DirectoryType || (stat.Mode & 0xfff) != 0x1c0 || stat.Owner != getuid())
            throw new InvalidDataException("Private directory rejected.");
    }

    private static SafeFileHandle Open(string path, int flags)
    {
        var fd = open(path, flags | CloseOnExec | 4); // O_NONBLOCK: reject FIFO by fstat without blocking.
        if (fd < 0) throw new IOException("Capture open rejected.");
        return new SafeFileHandle((IntPtr)fd, true);
    }

    private static SafeFileHandle OpenRelative(SafeFileHandle parent, string name, int flags)
    {
        var fd = openat(parent, name, flags | NoFollow | CloseOnExec | 4);
        if (fd < 0) throw new IOException("Capture component rejected.");
        return new SafeFileHandle((IntPtr)fd, true);
    }

    private static Stat Inspect(SafeFileHandle file)
    {
        if (fstat(file, out var stat) != 0) throw new IOException("Capture inventory unavailable.");
        return stat;
    }

    [System.Runtime.Versioning.SupportedOSPlatformGuard("macos")]
    private static bool IsSupportedPlatform() =>
        OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

    private static PlatformNotSupportedException UnsupportedPlatform() =>
        new("This custody experiment is macOS ARM64 only.");

    private sealed record Identity(
        string RunId,
        int Device,
        ulong Inode,
        long BirthSeconds,
        long BirthNanoseconds,
        long ExpiresUnixSeconds,
        long SecretByteLength,
        string? Authority,
        string? ClientId);

    // Native Darwin ARM64 layout verified against the installed SDK with sizeof/offsetof.
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct Stat
    {
        [FieldOffset(0)] internal int Device;
        [FieldOffset(4)] internal ushort Mode;
        [FieldOffset(6)] internal ushort Links;
        [FieldOffset(8)] internal ulong Inode;
        [FieldOffset(16)] internal uint Owner;
        [FieldOffset(80)] internal long BirthSeconds;
        [FieldOffset(88)] internal long BirthNanoseconds;
        [FieldOffset(96)] internal long Size;
        [FieldOffset(116)] internal uint Flags;
    }

    [DllImport("libSystem.B.dylib", SetLastError = true)] private static extern int open(string path, int flags);
    [DllImport("libSystem.B.dylib", SetLastError = true)] private static extern int openat(SafeFileHandle parent, string path, int flags);
    [DllImport("libSystem.B.dylib", SetLastError = true)] private static extern int fstat(SafeFileHandle fd, out Stat stat);
    [DllImport("libSystem.B.dylib", SetLastError = true)] private static extern int fchflags(SafeFileHandle fd, uint flags);
    [DllImport("libSystem.B.dylib")] private static extern uint getuid();
}
