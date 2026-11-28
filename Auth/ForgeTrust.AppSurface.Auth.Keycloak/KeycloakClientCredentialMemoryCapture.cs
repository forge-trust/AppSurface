using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForgeTrust.AppSurface.Auth.Keycloak;

/// <summary>Holds and reads one opaque Keycloak credential in a sealed Linux anonymous file.</summary>
/// <remarks>
/// The production path is qualified only on Linux ARM64. Native operations are behind an internal seam so
/// protocol and failure behavior can be tested on other platforms; those tests do not establish kernel support.
/// Disposal closes the holder's descriptor. The capture is not secure erasure and does not isolate against the
/// same effective user, root, or a trusted host daemon.
/// </remarks>
internal sealed class KeycloakClientCredentialMemoryCapture : IDisposable
{
    private const int MemfdCloexec = 1;
    private const int AllowSealing = 2;
    private const int AddSeals = 1033;
    private const int GetSeals = 1034;
    private const int RequiredSeals = 15;
    private const uint RequiredStatx = LinuxArm64Statx.TypeField | LinuxArm64Statx.ModeField | LinuxArm64Statx.LinkCountField
        | LinuxArm64Statx.UserIdField | LinuxArm64Statx.InodeField | LinuxArm64Statx.SizeField;
    private const int MaximumHeaderBytes = 4096;
    private const int MaximumFrameBytes = 12 + MaximumHeaderBytes + KeycloakClientCredentialText.MaximumBytes;
    private const int Interrupted = 4;
    private const int MaximumInterruptedCalls = 16;
    private static readonly byte[] Magic = "SMB1"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string ExpiringLifetime = "expiring";
    private const string OwnerLifetime = "owner";

    private readonly IMemoryNative _native;
    private int _descriptor;

    private KeycloakClientCredentialMemoryCapture(IMemoryNative native, int descriptor,
        KeycloakClientCredentialReference reference)
    {
        _native = native;
        _descriptor = descriptor;
        Reference = reference;
    }

    /// <summary>Gets the metadata-only reference for this capture.</summary>
    internal KeycloakClientCredentialReference Reference { get; }

    /// <summary>Creates an expiring, sealed capture bound to the exact run, authority, and client.</summary>
    internal static KeycloakClientCredentialMemoryCapture Create(string runId, Uri authority, string clientId, string value)
    {
        EnsureSupportedPlatform();
        return Create(runId, authority, clientId, value, LinuxArm64MemoryNative.Instance, TimeProvider.System);
    }

    /// <summary>Exercises qualified native capture with an explicit clock for expiry acceptance fixtures.</summary>
    internal static KeycloakClientCredentialMemoryCapture Create(string runId, Uri authority, string clientId,
        string value, TimeProvider clock)
    {
        EnsureSupportedPlatform();
        return Create(runId, authority, clientId, value, LinuxArm64MemoryNative.Instance, clock);
    }

    // Injectable native and clock seams let protocol tests run on hosts without qualified Linux support.
    internal static KeycloakClientCredentialMemoryCapture Create(string runId, Uri authority, string clientId,
        string value, IMemoryNative native, TimeProvider clock)
        => CreateCore(runId, authority, clientId, value, native, clock, ownerLifetime: false);

    /// <summary>Creates a sealed capture that remains valid while its exact holder descriptor remains open.</summary>
    internal static KeycloakClientCredentialMemoryCapture CreateForOwnerLifetime(string runId, Uri authority,
        string clientId, string value)
    {
        EnsureSupportedPlatform();
        return CreateForOwnerLifetime(runId, authority, clientId, value, LinuxArm64MemoryNative.Instance, TimeProvider.System);
    }

    /// <summary>Exercises owner-lifetime capture with injected native operations and a clock for read fixtures.</summary>
    internal static KeycloakClientCredentialMemoryCapture CreateForOwnerLifetime(string runId, Uri authority,
        string clientId, string value, IMemoryNative native, TimeProvider clock)
        => CreateCore(runId, authority, clientId, value, native, clock, ownerLifetime: true);

    private static KeycloakClientCredentialMemoryCapture CreateCore(string runId, Uri authority, string clientId,
        string value, IMemoryNative native, TimeProvider clock, bool ownerLifetime)
    {
        ArgumentNullException.ThrowIfNull(runId);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(clientId);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(clock);

        if (!Guid.TryParseExact(runId, "N", out _)) throw new ArgumentException("The run identifier is invalid.", nameof(runId));
        if (!authority.IsAbsoluteUri) throw new ArgumentException("The authority must be absolute.", nameof(authority));
        var normalizedAuthority = KeycloakClientCredentialReference.NormalizeAuthority(authority.AbsoluteUri);
        KeycloakClientCredentialReference.ValidateClientId(clientId);
        var secret = EncodeCredential(value);
        var descriptor = -1;
        byte[]? frame = null;
        try
        {
            var startCounter = native.GetProcessStartCounter(Environment.ProcessId);
            EnsureSupportedProcessStartCounter(startCounter);
            long? expires = ownerLifetime ? null : clock.GetUtcNow().UtcDateTime.AddMinutes(5).Ticks;
            frame = CreateFrame(new Header(runId, normalizedAuthority, clientId,
                ownerLifetime ? OwnerLifetime : ExpiringLifetime, expires), secret);
            descriptor = native.CreateMemfd("keycloak-client-credential", AllowSealing | MemfdCloexec);
            if (descriptor < 0) throw new IOException("Sealed credential capture could not be created.");
            WriteFully(native, descriptor, frame);
            if (native.Fchmod(descriptor, 0x100) != 0 || native.Fcntl(descriptor, AddSeals, RequiredSeals) != 0)
                throw new IOException("Sealed credential capture could not be finalized.");
            var seals = native.Fcntl(descriptor, GetSeals, 0);
            if (seals < 0 || (seals & RequiredSeals) != RequiredSeals)
                throw new IOException("Sealed credential capture could not be finalized.");

            var status = native.GetFileStatus(descriptor);
            ValidateFileStatus(status, native.EffectiveUserId, maximumSize: MaximumFrameBytes);
            if (status.Size != (ulong)frame.Length || native.GetProcessStartCounter(Environment.ProcessId) != startCounter)
                throw new InvalidDataException("Sealed credential capture identity was rejected.");

            var identity = new KeycloakClientCredentialMemoryIdentity(
                Environment.ProcessId, startCounter, descriptor, status.Inode);
            var reference = KeycloakClientCredentialReference.ForSealedMemory(
                identity, runId, new Uri(normalizedAuthority, UriKind.Absolute), clientId);
            return new KeycloakClientCredentialMemoryCapture(native, descriptor, reference);
        }
        catch
        {
            if (descriptor >= 0) native.Close(descriptor);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            if (frame is not null) CryptographicOperations.ZeroMemory(frame);
        }
    }

    /// <summary>Reads and validates a sealed capture without exporting any secret-derived metadata.</summary>
    internal static string Read(KeycloakClientCredentialReference reference)
    {
        EnsureSupportedPlatform();
        return Read(reference, LinuxArm64MemoryNative.Instance, TimeProvider.System);
    }

    /// <summary>Reads with production Linux native operations and an explicit clock for native lifetime probes.</summary>
    internal static string Read(KeycloakClientCredentialReference reference, TimeProvider clock)
    {
        EnsureSupportedPlatform();
        return Read(reference, LinuxArm64MemoryNative.Instance, clock);
    }

    internal static string Read(KeycloakClientCredentialReference reference, IMemoryNative native, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(clock);
        var identity = reference.MemoryIdentity ?? throw new InvalidDataException("Sealed credential reference was rejected.");
        identity.Validate();

        var expectedAuthority = KeycloakClientCredentialReference.NormalizeAuthority(reference.Authority.AbsoluteUri);
        KeycloakClientCredentialReference.ValidateClientId(reference.ClientId);
        if (native.GetProcessStartCounter(identity.ProcessId) != identity.ProcessStartCounter)
            throw new InvalidDataException("Sealed credential holder identity was rejected.");

        var descriptor = native.OpenForRead(identity.ProcessId, identity.FileDescriptor);
        if (descriptor < 0) throw new IOException("Sealed credential capture could not be opened.");
        byte[]? frame = null;
        try
        {
            var seals = native.Fcntl(descriptor, GetSeals, 0);
            if (seals < 0 || (seals & RequiredSeals) != RequiredSeals)
                throw new InvalidDataException("Sealed credential capture was rejected.");
            var before = native.GetFileStatus(descriptor);
            ValidateFileStatus(before, native.EffectiveUserId, MaximumFrameBytes);
            if (before.Inode != identity.Inode) throw new InvalidDataException("Sealed credential file identity was rejected.");
            ValidateHolderStillOwnsFile(native, identity, before);

            frame = ReadFully(native, descriptor, checked((int)before.Size));
            if (ReadOneByte(native, descriptor) != 0)
                throw new InvalidDataException("Sealed credential frame length was rejected.");

            var after = native.GetFileStatus(descriptor);
            if (after != before || native.GetProcessStartCounter(identity.ProcessId) != identity.ProcessStartCounter)
                throw new InvalidDataException("Sealed credential identity changed during read.");
            ValidateHolderStillOwnsFile(native, identity, before);
            return DecodeFrame(frame, reference.RunId, expectedAuthority, reference.ClientId, clock.GetUtcNow().UtcDateTime.Ticks);
        }
        finally
        {
            if (frame is not null) CryptographicOperations.ZeroMemory(frame);
            native.Close(descriptor);
        }
    }

    /// <summary>Closes the holder descriptor. This does not claim secure erasure of prior memory contents.</summary>
    public void Dispose()
    {
        var descriptor = Interlocked.Exchange(ref _descriptor, -1);
        if (descriptor >= 0) _native.Close(descriptor);
    }

    private static string DecodeFrame(byte[] frame, string runId, string authority, string clientId, long nowTicks)
    {
        byte[]? headerBytes = null;
        byte[]? secret = null;
        try
        {
            if (frame.Length < 12 || !frame.AsSpan(0, 4).SequenceEqual(Magic))
                throw new InvalidDataException("Sealed credential frame was rejected.");
            var headerLength = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(4, 4));
            var secretLength = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(8, 4));
            if (headerLength is 0 or > MaximumHeaderBytes || secretLength is 0 or > KeycloakClientCredentialText.MaximumBytes
                || 12UL + headerLength + secretLength != (ulong)frame.Length)
                throw new InvalidDataException("Sealed credential frame length was rejected.");

            headerBytes = frame.AsSpan(12, (int)headerLength).ToArray();
            secret = frame.AsSpan(12 + (int)headerLength, (int)secretLength).ToArray();
            var header = ParseHeader(headerBytes);
            if (header.Run != runId || header.Authority != authority || header.Client != clientId
                || header.Lifetime == ExpiringLifetime && header.ExpiresUtcTicks is { } expiry && expiry <= nowTicks)
                throw new InvalidDataException("Sealed credential binding or expiry was rejected.");

            string value;
            try { value = KeycloakClientCredentialText.Decode(secret); }
            catch (DecoderFallbackException) { throw new InvalidDataException("Sealed credential text was rejected."); }
            if (!KeycloakClientCredentialText.IsValid(value))
                throw new InvalidDataException("Sealed credential text was rejected.");
            return value;
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Sealed credential header was rejected.");
        }
        finally
        {
            if (headerBytes is not null) CryptographicOperations.ZeroMemory(headerBytes);
            if (secret is not null) CryptographicOperations.ZeroMemory(secret);
        }
    }

    private static Header ParseHeader(byte[] headerBytes)
    {
        using var document = JsonDocument.Parse(headerBytes, new JsonDocumentOptions { MaxDepth = 2 });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Sealed credential header was rejected.");

        string? run = null;
        string? authority = null;
        string? client = null;
        string? lifetime = null;
        long? expiresUtcTicks = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw new InvalidDataException("Sealed credential header was rejected.");
            switch (property.Name)
            {
                case "run": run = ReadString(property.Value); break;
                case "authority": authority = ReadString(property.Value); break;
                case "client": client = ReadString(property.Value); break;
                case "lifetime": lifetime = ReadString(property.Value); break;
                case "expiresUtcTicks":
                    if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out var ticks))
                        expiresUtcTicks = ticks;
                    else if (property.Value.ValueKind != JsonValueKind.Null)
                        throw new InvalidDataException("Sealed credential header was rejected.");
                    break;
                default: throw new InvalidDataException("Sealed credential header was rejected.");
            }
        }

        if (run is null || authority is null || client is null || lifetime is null
            || lifetime == ExpiringLifetime && (!seen.Contains("expiresUtcTicks") || expiresUtcTicks is null)
            || lifetime == OwnerLifetime && seen.Contains("expiresUtcTicks")
            || lifetime is not (ExpiringLifetime or OwnerLifetime))
            throw new InvalidDataException("Sealed credential header was rejected.");

        return new Header(run, authority, client, lifetime, expiresUtcTicks);
    }

    private static string ReadString(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? value.GetString() ?? throw new InvalidDataException("Sealed credential header was rejected.")
        : throw new InvalidDataException("Sealed credential header was rejected.");

    private static void ValidateHolderStillOwnsFile(IMemoryNative native,
        KeycloakClientCredentialMemoryIdentity identity, MemoryFileStatus expectedStatus)
    {
        if (native.GetProcessStartCounter(identity.ProcessId) != identity.ProcessStartCounter)
            throw new InvalidDataException("Sealed credential holder identity was rejected.");
        var descriptor = native.OpenForRead(identity.ProcessId, identity.FileDescriptor);
        if (descriptor < 0) throw new InvalidDataException("Sealed credential holder descriptor was rejected.");
        try
        {
            var seals = native.Fcntl(descriptor, GetSeals, 0);
            var status = native.GetFileStatus(descriptor);
            ValidateFileStatus(status, native.EffectiveUserId, MaximumFrameBytes);
            if (seals < 0 || (seals & RequiredSeals) != RequiredSeals || status != expectedStatus
                || native.GetProcessStartCounter(identity.ProcessId) != identity.ProcessStartCounter)
                throw new InvalidDataException("Sealed credential holder descriptor was rejected.");
        }
        finally { native.Close(descriptor); }
    }

    private static byte[] EncodeCredential(string value)
    {
        byte[] bytes;
        try { bytes = KeycloakClientCredentialText.Encode(value); }
        catch (EncoderFallbackException) { throw new InvalidDataException("Credential text must be strict UTF-8."); }
        if (bytes.Length is < 1 or > KeycloakClientCredentialText.MaximumBytes || !KeycloakClientCredentialText.IsValid(value))
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new InvalidDataException("Credential text is outside the supported bounds.");
        }
        return bytes;
    }

    private static byte[] CreateFrame(Header header, byte[] secret)
    {
        var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header, JsonOptions);
        try
        {
            if (headerBytes.Length is < 1 or > MaximumHeaderBytes) throw new InvalidDataException("Capture header is outside the supported bounds.");
            var frame = new byte[12 + headerBytes.Length + secret.Length];
            Magic.CopyTo(frame, 0);
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), (uint)headerBytes.Length);
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(8), (uint)secret.Length);
            headerBytes.CopyTo(frame, 12);
            secret.CopyTo(frame, 12 + headerBytes.Length);
            return frame;
        }
        finally { CryptographicOperations.ZeroMemory(headerBytes); }
    }

    private static void WriteFully(IMemoryNative native, int descriptor, byte[] bytes)
    {
        var offset = 0;
        var interruptions = 0;
        while (offset < bytes.Length)
        {
            var written = native.Write(descriptor, bytes, offset, bytes.Length - offset);
            if (written < 0 && native.LastError == Interrupted)
            {
                if (++interruptions > MaximumInterruptedCalls) throw new IOException("Sealed credential capture write failed.");
                continue;
            }
            if (written <= 0 || written > bytes.Length - offset)
                throw new IOException("Sealed credential capture write failed.");
            offset += written;
        }
    }

    private static byte[] ReadFully(IMemoryNative native, int descriptor, int length)
    {
        var bytes = new byte[length];
        var offset = 0;
        var interruptions = 0;
        while (offset < bytes.Length)
        {
            var read = native.Read(descriptor, bytes, offset, bytes.Length - offset);
            if (read < 0 && native.LastError == Interrupted)
            {
                if (++interruptions > MaximumInterruptedCalls)
                {
                    CryptographicOperations.ZeroMemory(bytes);
                    throw new InvalidDataException("Sealed credential frame read was interrupted.");
                }
                continue;
            }
            if (read <= 0 || read > bytes.Length - offset)
            {
                CryptographicOperations.ZeroMemory(bytes);
                throw new InvalidDataException("Sealed credential frame was incomplete.");
            }
            offset += read;
        }
        return bytes;
    }

    private static int ReadOneByte(IMemoryNative native, int descriptor)
    {
        var byteBuffer = new byte[1];
        var interruptions = 0;
        while (true)
        {
            var read = native.Read(descriptor, byteBuffer, 0, 1);
            if (read < 0 && native.LastError == Interrupted)
            {
                if (++interruptions > MaximumInterruptedCalls)
                    throw new InvalidDataException("Sealed credential frame read was interrupted.");
                continue;
            }
            return read;
        }
    }

    private static void ValidateFileStatus(MemoryFileStatus status, uint effectiveUserId, int maximumSize)
    {
        if ((status.Mask & RequiredStatx) != RequiredStatx
            || (status.Mode & 0xF000) != 0x8000
            || (status.Mode & 0xFFF) != 0x100
            || status.UserId != effectiveUserId
            || status.LinkCount != 0
            || status.Inode == 0
            || status.Size < 12 || status.Size > (ulong)maximumSize)
            throw new InvalidDataException("Sealed credential file metadata was rejected.");
    }

    private static void EnsureSupportedPlatform()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
            throw new PlatformNotSupportedException("Sealed-memory credential custody is supported only on Linux ARM64.");
    }

    /// <summary>Parses field 22 from a Linux process stat record without reading the proc filesystem.</summary>
    /// <param name="stat">The complete text of one process stat record.</param>
    /// <exception cref="InvalidDataException">The record has no valid positive start counter.</exception>
    internal static long ParseProcessStartCounter(string stat)
    {
        ArgumentNullException.ThrowIfNull(stat);
        var closingParenthesis = stat.LastIndexOf(')');
        if (closingParenthesis < 0 || closingParenthesis + 2 > stat.Length
            || stat[closingParenthesis + 1] != ' ')
            throw new InvalidDataException("Sealed credential holder identity was rejected.");
        var fields = stat[(closingParenthesis + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length <= 19 || !long.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out var counter))
            throw new InvalidDataException("Sealed credential holder identity was rejected.");
        EnsureSupportedProcessStartCounter(counter);
        return counter;
    }

    private static void EnsureSupportedProcessStartCounter(long counter)
    {
        if (counter <= 0) throw new InvalidDataException("Sealed credential holder identity was rejected.");
    }

    private sealed record Header(string Run, string Authority, string Client, string Lifetime,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ExpiresUtcTicks);

    internal readonly record struct MemoryFileStatus(uint Mask, ushort Mode, uint UserId, uint LinkCount, ulong Inode, ulong Size);

    /// <summary>Decodes the fixed Linux statx memory layout without invoking a native operation.</summary>
    /// <param name="statx">A complete 256-byte statx result buffer.</param>
    /// <returns>The raw file status fields read from the buffer.</returns>
    /// <exception cref="InvalidDataException">The buffer does not have the expected statx size.</exception>
    internal static MemoryFileStatus ParseStatxMemoryStatus(ReadOnlySpan<byte> statx)
    {
        if (statx.Length != LinuxArm64Statx.BufferSize)
            throw new InvalidDataException("Sealed credential file metadata was rejected.");

        var status = new LinuxArm64Statx(statx);
        return new MemoryFileStatus(status.Mask, status.Mode, status.UserId, status.LinkCount, status.Inode, status.Size);
    }

    internal interface IMemoryNative
    {
        int LastError { get; }
        uint EffectiveUserId { get; }
        int CreateMemfd(string name, int flags);
        int Write(int descriptor, byte[] buffer, int offset, int count);
        int Read(int descriptor, byte[] buffer, int offset, int count);
        int Fchmod(int descriptor, uint mode);
        int Fcntl(int descriptor, int command, int argument);
        int OpenForRead(int processId, int descriptor);
        MemoryFileStatus GetFileStatus(int descriptor);
        long GetProcessStartCounter(int processId);
        void Close(int descriptor);
    }

    private sealed class LinuxArm64MemoryNative : IMemoryNative
    {
        internal static LinuxArm64MemoryNative Instance { get; } = new();
        private const int CloseOnExec = 0x80000;
        private const int NonBlocking = 0x800;
        private const int AtEmptyPath = 0x1000;

        public int LastError => Marshal.GetLastPInvokeError();
        public uint EffectiveUserId => Native.geteuid();
        public int CreateMemfd(string name, int flags) => Native.memfd_create(name, flags);
        public int Fchmod(int descriptor, uint mode) => Native.fchmod(descriptor, mode);
        public int Fcntl(int descriptor, int command, int argument) => Native.fcntl(descriptor, command, argument);
        public int OpenForRead(int processId, int descriptor) =>
            Native.open($"/proc/{processId}/fd/{descriptor}", NonBlocking | CloseOnExec);
        public void Close(int descriptor) => Native.close(descriptor);

        public int Write(int descriptor, byte[] buffer, int offset, int count) => Transfer(Native.write, descriptor, buffer, offset, count);
        public int Read(int descriptor, byte[] buffer, int offset, int count) => Transfer(Native.read, descriptor, buffer, offset, count);

        public MemoryFileStatus GetFileStatus(int descriptor)
        {
            var stat = new byte[LinuxArm64Statx.BufferSize];
            if (LinuxArm64Statx.ReadNative(descriptor, "", AtEmptyPath, RequiredStatx, stat) != 0)
                throw new IOException("Sealed credential file metadata could not be read.");
            return ParseStatxMemoryStatus(stat);
        }

        public long GetProcessStartCounter(int processId)
        {
            if (processId <= 0) throw new InvalidDataException("Sealed credential holder identity was rejected.");
            var stat = File.ReadAllText($"/proc/{processId}/stat");
            return ParseProcessStartCounter(stat);
        }

        private static int Transfer(TransferCall call, int descriptor, byte[] buffer, int offset, int count)
        {
            var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try { return checked((int)call(descriptor, IntPtr.Add(pin.AddrOfPinnedObject(), offset), (nuint)count)); }
            finally { pin.Free(); }
        }

        private delegate nint TransferCall(int descriptor, IntPtr buffer, nuint count);

        private static class Native
        {
            [DllImport("libc", SetLastError = true, EntryPoint = "memfd_create")]
            internal static extern int memfd_create([MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags);
            [DllImport("libc", SetLastError = true)] internal static extern int fcntl(int descriptor, int command, int argument);
            [DllImport("libc", SetLastError = true)] internal static extern int fchmod(int descriptor, uint mode);
            [DllImport("libc", SetLastError = true, EntryPoint = "open")]
            internal static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
            [DllImport("libc", SetLastError = true)] internal static extern nint write(int descriptor, IntPtr buffer, nuint count);
            [DllImport("libc", SetLastError = true)] internal static extern nint read(int descriptor, IntPtr buffer, nuint count);
            [DllImport("libc", SetLastError = true)] internal static extern int close(int descriptor);
            [DllImport("libc")] internal static extern uint geteuid();
        }
    }
}
