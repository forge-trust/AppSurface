using System.Buffers.Binary;
using System.Runtime.InteropServices;
using ForgeTrust.AppSurface.Auth.Keycloak;

namespace ForgeTrust.AppSurface.Auth.Keycloak.Tests;

// protects: the fixed Linux ARM64 statx layout and the fail-closed root metadata boundary.
// fails_when: truncated/extended buffers, missing identity fields, non-directories, or invalid birth times are accepted.
// seam: internal Parse over synthetic statx bytes; no reflection, filesystem contents, or native call.
// no_claim: these tests do not prove Docker daemon, volume, mountpoint, or owner-lease attestation.
public sealed class KeycloakVolumeIncarnationTests
{
    private const int StatxBufferSize = 256;
    private const uint RequiredMask = 0x901;
    private const uint DatabaseOwnershipMask = 0x11D;
    private const int DirectoryOpenFlags = 0x8C000;
    private const int SymlinkNoFollowFlag = 0x100;

    [Fact]
    public void ParsesDeviceInodeAndBirthTimeFromTheFixedStatxOffsets()
    {
        var stat = ValidStat();
        BinaryPrimitives.WriteUInt32LittleEndian(stat, RequiredMask | 0x002U);

        var incarnation = KeycloakVolumeIncarnation.Parse(stat);

        Assert.Equal(new KeycloakVolumeIncarnation(
            DeviceMajor: 0x89ABCDEF,
            DeviceMinor: 0x76543210,
            Inode: 0xFEDCBA9876543210,
            BirthSeconds: 1_800_000_001,
            BirthNanoseconds: 999_999_999), incarnation);
    }

    [Fact]
    public void ReadsNumericOwnershipWithoutChangingTheIncarnation()
    {
        var stat = ValidStat();
        BinaryPrimitives.WriteUInt32LittleEndian(stat, RequiredMask | 0x018);
        BinaryPrimitives.WriteUInt32LittleEndian(stat.AsSpan(20), 1000);
        BinaryPrimitives.WriteUInt32LittleEndian(stat.AsSpan(24), 42);

        var (incarnation, uid, gid) = KeycloakVolumeIncarnation.ParseOwnership(stat);

        Assert.Equal(KeycloakVolumeIncarnation.Parse(stat), incarnation);
        Assert.Equal(1000u, uid);
        Assert.Equal(42u, gid);
    }

    [Fact]
    public void AcceptsAdditionalStatxFieldsAndDirectoryPermissionBits()
    {
        var stat = ValidStat();
        BinaryPrimitives.WriteUInt32LittleEndian(stat, RequiredMask | 0x40000000);
        BinaryPrimitives.WriteUInt16LittleEndian(stat.AsSpan(28), 0x41C0);

        Assert.Equal(new KeycloakVolumeIncarnation(
            DeviceMajor: 0x89ABCDEF,
            DeviceMinor: 0x76543210,
            Inode: 0xFEDCBA9876543210,
            BirthSeconds: 1_800_000_001,
            BirthNanoseconds: 999_999_999), KeycloakVolumeIncarnation.Parse(stat));
    }

    [Fact]
    public void ReadsMaximumNumericOwnershipValues()
    {
        var stat = ValidStat();
        BinaryPrimitives.WriteUInt32LittleEndian(stat, RequiredMask | 0x018);
        BinaryPrimitives.WriteUInt32LittleEndian(stat.AsSpan(20), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(stat.AsSpan(24), uint.MaxValue - 1);

        var (_, uid, gid) = KeycloakVolumeIncarnation.ParseOwnership(stat);

        Assert.Equal(uint.MaxValue, uid);
        Assert.Equal(uint.MaxValue - 1, gid);
    }

    [Fact]
    public void ChangingNumericOwnerLeavesTheVolumeIncarnationUnchanged()
    {
        var first = ValidStat();
        BinaryPrimitives.WriteUInt32LittleEndian(first, RequiredMask | 0x018);
        BinaryPrimitives.WriteUInt32LittleEndian(first.AsSpan(20), 1000);
        BinaryPrimitives.WriteUInt32LittleEndian(first.AsSpan(24), 42);
        var second = first.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(second.AsSpan(20), 2000);
        BinaryPrimitives.WriteUInt32LittleEndian(second.AsSpan(24), 99);

        var firstOwner = KeycloakVolumeIncarnation.ParseOwnership(first);
        var secondOwner = KeycloakVolumeIncarnation.ParseOwnership(second);

        Assert.Equal(firstOwner.Incarnation, secondOwner.Incarnation);
        Assert.NotEqual((firstOwner.Uid, firstOwner.Gid), (secondOwner.Uid, secondOwner.Gid));
    }

    [Fact]
    public void ReadsRootIncarnationThroughNoFollowStatxWithTheExactIdentityMask()
    {
        var native = new FakeNativeOperations { StatxMetadata = ValidStat() };

        var incarnation = KeycloakVolumeIncarnation.ReadCore("/volume", native);

        Assert.Equal(KeycloakVolumeIncarnation.Parse(ValidStat()), incarnation);
        Assert.NotNull(native.LastStatxCall);
        Assert.Equal(new StatxCall(-100, "/volume", SymlinkNoFollowFlag, RequiredMask), native.LastStatxCall.Value);
        Assert.Empty(native.OpenCalls);
    }

    [Fact]
    public void ReadsRootNumericOwnershipWithTheExactOwnershipMask()
    {
        var stat = ValidStat();
        BinaryPrimitives.WriteUInt32LittleEndian(stat, RequiredMask | 0x018);
        BinaryPrimitives.WriteUInt32LittleEndian(stat.AsSpan(20), 1000);
        BinaryPrimitives.WriteUInt32LittleEndian(stat.AsSpan(24), 42);
        var native = new FakeNativeOperations { StatxMetadata = stat };

        var (incarnation, uid, gid) = KeycloakVolumeIncarnation.ReadOwnershipCore("/volume", native);

        Assert.Equal(KeycloakVolumeIncarnation.Parse(stat), incarnation);
        Assert.Equal(1000u, uid);
        Assert.Equal(42u, gid);
        Assert.NotNull(native.LastStatxCall);
        Assert.Equal(new StatxCall(-100, "/volume", SymlinkNoFollowFlag, RequiredMask | 0x018), native.LastStatxCall.Value);
        Assert.Empty(native.OpenCalls);
    }

    [Fact]
    public void ReportsRootStatxFailureThroughTheNativeOperationsBoundary()
    {
        var native = new FakeNativeOperations { StatxResult = -1, StatxError = 13 };

        Assert.Throws<IOException>(() => KeycloakVolumeIncarnation.ReadCore("/volume", native));

        Assert.NotNull(native.LastStatxCall);
        Assert.Equal(new StatxCall(-100, "/volume", SymlinkNoFollowFlag, RequiredMask), native.LastStatxCall.Value);
    }

    [Fact]
    public void ReadsPinnedDatabaseOwnerThroughDirectoryRelativeNoFollowCallsAndReleasesDescriptors()
    {
        var native = new FakeNativeOperations();
        native.QueueDirectory(101);
        native.QueueDirectory(102);
        native.QueueDirectory(103);
        native.QueueDirectory(104);

        var ownership = KeycloakVolumeIncarnation.ReadPinnedDatabaseOwnershipCore("/mnt/volume", native);

        Assert.NotNull(ownership);
        Assert.Equal((1000u, 42u), ownership.Value);
        Assert.Equal(new[]
        {
            new OpenAtCall(-100, "/", DirectoryOpenFlags),
            new OpenAtCall(101, "mnt", DirectoryOpenFlags),
            new OpenAtCall(102, "volume", DirectoryOpenFlags),
            new OpenAtCall(103, "h2", DirectoryOpenFlags)
        }, native.OpenCalls);
        Assert.NotNull(native.LastStatxCall);
        Assert.Equal(
            new StatxCall(104, "keycloakdb.mv.db", SymlinkNoFollowFlag, DatabaseOwnershipMask),
            native.LastStatxCall.Value);
        Assert.Equal(new[] { 104, 103, 102, 101 }, native.DisposedDescriptors);
    }

    [Fact]
    public void ReturnsNullWhenTheH2DirectoryIsMissingAndReleasesOpenedDescriptors()
    {
        var native = new FakeNativeOperations();
        native.QueueDirectory(201);
        native.QueueDirectory(202);
        native.QueueOpenError(2);

        var ownership = KeycloakVolumeIncarnation.ReadPinnedDatabaseOwnershipCore("/mnt", native);

        Assert.Null(ownership);
        Assert.Null(native.LastStatxCall);
        Assert.Equal(new[] { 202, 201 }, native.DisposedDescriptors);
    }

    [Fact]
    public void ReturnsNullWhenTheH2DatabaseFileIsMissingAndReleasesOpenedDescriptors()
    {
        var native = OpenVolumeAndH2Directories(301, 302, 303);
        native.StatxResult = -1;
        native.StatxError = 2;

        var ownership = KeycloakVolumeIncarnation.ReadPinnedDatabaseOwnershipCore("/volume", native);

        Assert.Null(ownership);
        Assert.Equal(new[] { 303, 302, 301 }, native.DisposedDescriptors);
    }

    [Theory]
    [InlineData("relative-volume")]
    [InlineData("")]
    [InlineData(" ")]
    public void RejectsNonAbsoluteOrEmptyRootsBeforeOpeningDirectories(string volumeRoot)
    {
        var native = new FakeNativeOperations();

        Assert.Throws<ArgumentException>(() =>
            KeycloakVolumeIncarnation.ReadPinnedDatabaseOwnershipCore(volumeRoot, native));
        Assert.Empty(native.OpenCalls);
    }

    [Theory]
    [InlineData("/tmp/../volume")]
    [InlineData("/tmp/./volume")]
    public void RejectsTraversalRootsBeforeOpeningDirectories(string volumeRoot)
    {
        var native = new FakeNativeOperations();

        Assert.Throws<InvalidDataException>(() =>
            KeycloakVolumeIncarnation.ReadPinnedDatabaseOwnershipCore(volumeRoot, native));
        Assert.Empty(native.OpenCalls);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(40)]
    public void RejectsLinkOrNondirectoryComponentsAndReleasesOpenedDescriptors(int error)
    {
        var native = new FakeNativeOperations();
        native.QueueDirectory(401);
        native.QueueOpenError(error);

        Assert.Throws<InvalidDataException>(() =>
            KeycloakVolumeIncarnation.ReadPinnedDatabaseOwnershipCore("/volume", native));

        Assert.Equal(new[] { 401 }, native.DisposedDescriptors);
    }

    [Fact]
    public void ReportsRootDescriptorOpenFailure()
    {
        var native = new FakeNativeOperations();
        native.QueueOpenError(13);

        Assert.Throws<IOException>(() =>
            KeycloakVolumeIncarnation.ReadPinnedDatabaseOwnershipCore("/volume", native));

        Assert.Empty(native.DisposedDescriptors);
    }

    [Fact]
    public void RejectsAnH2DirectoryLinkAndReleasesOpenedDescriptors()
    {
        var native = new FakeNativeOperations();
        native.QueueDirectory(501);
        native.QueueDirectory(502);
        native.QueueOpenError(40);

        Assert.Throws<InvalidDataException>(() =>
            KeycloakVolumeIncarnation.ReadPinnedDatabaseOwnershipCore("/volume", native));

        Assert.Equal(new[] { 502, 501 }, native.DisposedDescriptors);
    }

    [Fact]
    public void ReportsAnUnexpectedDirectoryOpenFailureAndReleasesOpenedDescriptors()
    {
        var native = new FakeNativeOperations();
        native.QueueDirectory(601);
        native.QueueOpenError(13);

        Assert.Throws<IOException>(() =>
            KeycloakVolumeIncarnation.ReadPinnedDatabaseOwnershipCore("/volume", native));

        Assert.Equal(new[] { 601 }, native.DisposedDescriptors);
    }

    [Theory]
    [InlineData(0x11CU, 0x81A4, 1U, 707UL)]
    [InlineData(0x119U, 0x81A4, 1U, 707UL)]
    [InlineData(0x115U, 0x81A4, 1U, 707UL)]
    [InlineData(0x10DU, 0x81A4, 1U, 707UL)]
    [InlineData(0x11DU, 0xA000, 1U, 707UL)]
    [InlineData(0x11DU, 0x4000, 1U, 707UL)]
    [InlineData(0x11DU, 0x81A4, 0U, 707UL)]
    [InlineData(0x11DU, 0x81A4, 1U, 0UL)]
    public void RejectsMalformedDatabaseMetadataAndReleasesDescriptors(
        uint mask,
        int mode,
        uint links,
        ulong inode)
    {
        var native = OpenVolumeAndH2Directories(701, 702, 703);
        native.StatxMetadata = DatabaseStat(mask, mode, links, inode);

        Assert.Throws<InvalidDataException>(() =>
            KeycloakVolumeIncarnation.ReadPinnedDatabaseOwnershipCore("/volume", native));

        Assert.Equal(new[] { 703, 702, 701 }, native.DisposedDescriptors);
    }

    [Fact]
    public void ReportsStaleDatabaseMetadataReadFailureAndReleasesDescriptors()
    {
        var native = OpenVolumeAndH2Directories(801, 802, 803);
        native.StatxResult = -1;
        native.StatxError = 116;

        Assert.Throws<IOException>(() =>
            KeycloakVolumeIncarnation.ReadPinnedDatabaseOwnershipCore("/volume", native));

        Assert.Equal(new[] { 803, 802, 801 }, native.DisposedDescriptors);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [Fact]
    public void RejectsRelativeAndTraversalRootsBeforeOpeningDirectories()
    {
        if (!IsQualifiedLane()) return;

        Assert.Throws<ArgumentException>(() => KeycloakVolumeIncarnation.ReadPinnedDatabaseOwnership("relative-volume"));
        Assert.Throws<InvalidDataException>(() => KeycloakVolumeIncarnation.ReadPinnedDatabaseOwnership("/tmp/../keycloak-fixture"));
    }

    [Fact]
    public void ProductionEntryPointPreservesTheLinuxArm64PlatformGate()
    {
        if (IsQualifiedLane()) return;

        Assert.Throws<PlatformNotSupportedException>(() => KeycloakVolumeIncarnation.Read("/volume"));
        Assert.Throws<PlatformNotSupportedException>(() => KeycloakVolumeIncarnation.ReadOwnership("/volume"));
        Assert.Throws<PlatformNotSupportedException>(() =>
            KeycloakVolumeIncarnation.ReadPinnedDatabaseOwnership("/volume"));
    }

    [Theory]
    [InlineData(0x008u)]
    [InlineData(0x010u)]
    public void RejectsAnOwnershipResponseMissingEitherNumericIdentity(uint ownershipMask)
    {
        var stat = ValidStat();
        BinaryPrimitives.WriteUInt32LittleEndian(stat, RequiredMask | ownershipMask);

        Assert.Throws<InvalidDataException>(() => KeycloakVolumeIncarnation.ParseOwnership(stat));
    }

    [Theory]
    [InlineData(255)]
    [InlineData(257)]
    public void RejectsBuffersThatAreNotExactlyTheStatxSize(int size)
    {
        var stat = ValidStat(size);

        Assert.Throws<InvalidDataException>(() => KeycloakVolumeIncarnation.Parse(stat));
    }

    [Theory]
    [InlineData(0x100)] // missing type
    [InlineData(0x801)] // missing inode
    [InlineData(0x101)] // missing birth time
    public void RejectsAResultMissingAnyRequestedMaskBit(int mask)
    {
        var stat = ValidStat();
        BinaryPrimitives.WriteUInt32LittleEndian(stat, (uint)mask);

        Assert.Throws<InvalidDataException>(() => KeycloakVolumeIncarnation.Parse(stat));
    }

    [Theory]
    [InlineData(0x81A4)] // regular file
    [InlineData(0xA000)] // symbolic link
    public void RejectsRootsThatAreNotDirectories(int mode)
    {
        var stat = ValidStat();
        BinaryPrimitives.WriteUInt16LittleEndian(stat.AsSpan(28), (ushort)mode);

        Assert.Throws<InvalidDataException>(() => KeycloakVolumeIncarnation.Parse(stat));
    }

    [Fact]
    public void RejectsAZeroRootInode()
    {
        var stat = ValidStat();
        BinaryPrimitives.WriteUInt64LittleEndian(stat.AsSpan(32), 0);

        Assert.Throws<InvalidDataException>(() => KeycloakVolumeIncarnation.Parse(stat));
    }

    [Theory]
    [InlineData(0L, 0U)]
    [InlineData(-1L, 0U)]
    [InlineData(1L, 1_000_000_000U)]
    public void RejectsInvalidBirthTimestamps(long seconds, uint nanoseconds)
    {
        var stat = ValidStat();
        BinaryPrimitives.WriteInt64LittleEndian(stat.AsSpan(80), seconds);
        BinaryPrimitives.WriteUInt32LittleEndian(stat.AsSpan(88), nanoseconds);

        Assert.Throws<InvalidDataException>(() => KeycloakVolumeIncarnation.Parse(stat));
    }

    private static byte[] ValidStat(int size = StatxBufferSize)
    {
        var stat = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(stat, RequiredMask);
        BinaryPrimitives.WriteUInt16LittleEndian(stat.AsSpan(28), 0x41FF);
        BinaryPrimitives.WriteUInt64LittleEndian(stat.AsSpan(32), 0xFEDCBA9876543210);
        BinaryPrimitives.WriteInt64LittleEndian(stat.AsSpan(80), 1_800_000_001);
        BinaryPrimitives.WriteUInt32LittleEndian(stat.AsSpan(88), 999_999_999);
        BinaryPrimitives.WriteUInt32LittleEndian(stat.AsSpan(136), 0x89ABCDEF);
        BinaryPrimitives.WriteUInt32LittleEndian(stat.AsSpan(140), 0x76543210);
        return stat;
    }

    private static FakeNativeOperations OpenVolumeAndH2Directories(int rootDescriptor, int volumeDescriptor, int h2Descriptor)
    {
        var native = new FakeNativeOperations();
        native.QueueDirectory(rootDescriptor);
        native.QueueDirectory(volumeDescriptor);
        native.QueueDirectory(h2Descriptor);
        return native;
    }

    private static byte[] DatabaseStat(uint mask = DatabaseOwnershipMask, int mode = 0x81A4, uint links = 1, ulong inode = 707)
    {
        var stat = new byte[StatxBufferSize];
        BinaryPrimitives.WriteUInt32LittleEndian(stat, mask);
        BinaryPrimitives.WriteUInt32LittleEndian(stat.AsSpan(16), links);
        BinaryPrimitives.WriteUInt32LittleEndian(stat.AsSpan(20), 1000);
        BinaryPrimitives.WriteUInt32LittleEndian(stat.AsSpan(24), 42);
        BinaryPrimitives.WriteUInt16LittleEndian(stat.AsSpan(28), (ushort)mode);
        BinaryPrimitives.WriteUInt64LittleEndian(stat.AsSpan(32), inode);
        return stat;
    }

    private sealed class FakeNativeOperations : IKeycloakVolumeIncarnationNativeOperations
    {
        private readonly Queue<OpenAtResponse> _openAtResponses = new();

        internal List<OpenAtCall> OpenCalls { get; } = [];
        internal List<int> DisposedDescriptors { get; } = [];
        internal byte[] StatxMetadata { get; set; } = DatabaseStat();
        internal int StatxResult { get; set; }
        internal int StatxError { get; set; }
        internal StatxCall? LastStatxCall { get; private set; }

        internal void QueueDirectory(int descriptor)
            => _openAtResponses.Enqueue(new(Descriptor: descriptor, Error: 0));

        internal void QueueOpenError(int error)
            => _openAtResponses.Enqueue(new(Descriptor: -1, Error: error));

        public SafeHandle? OpenAt(int directoryDescriptor, string path, int flags, out int error)
        {
            OpenCalls.Add(new(directoryDescriptor, path, flags));
            var response = _openAtResponses.Dequeue();
            error = response.Error;
            return error == 0 ? new TrackingSafeHandle(response.Descriptor, DisposedDescriptors) : null;
        }

        public int Statx(int directoryDescriptor, string path, int flags, uint mask, byte[] buffer, out int error)
        {
            LastStatxCall = new(directoryDescriptor, path, flags, mask);
            error = StatxError;
            if (StatxResult == 0) StatxMetadata.CopyTo(buffer, 0);
            return StatxResult;
        }
    }

    private sealed class TrackingSafeHandle : SafeHandle
    {
        private readonly List<int> _disposedDescriptors;

        internal TrackingSafeHandle(int descriptor, List<int> disposedDescriptors)
            : base(new IntPtr(-1), ownsHandle: true)
        {
            _disposedDescriptors = disposedDescriptors;
            SetHandle(new IntPtr(descriptor));
        }

        public override bool IsInvalid => handle == new IntPtr(-1);

        protected override bool ReleaseHandle()
        {
            _disposedDescriptors.Add(handle.ToInt32());
            return true;
        }
    }

    private readonly record struct OpenAtResponse(int Descriptor, int Error);
    private readonly record struct OpenAtCall(int DirectoryDescriptor, string Path, int Flags);
    private readonly record struct StatxCall(int DirectoryDescriptor, string Path, int Flags, uint Mask);

    [System.Runtime.Versioning.SupportedOSPlatformGuard("linux")]
    private static bool IsQualifiedLane()
        => OperatingSystem.IsLinux()
           && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
           == System.Runtime.InteropServices.Architecture.Arm64;
}
