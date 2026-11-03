using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using ForgeTrust.AppSurface.Evidence.Supervision;
using ForgeTrust.AppSurface.Testing;
using Microsoft.Win32.SafeHandles;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Exercises producer report discovery with actual Linux directory descriptors and filesystem entries.</summary>
/// <remarks>These cases prove discovery behavior only; they do not create a producer, owner, account, or proof.</remarks>
[SupportedOSPlatform("linux")]
public sealed class LinuxProducerReportDiscoveryNativeTests
{
    private const uint SubjectUid = 65534;
    private const uint SubjectGid = 65534;
    private static readonly UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private static readonly UnixFileMode ReadOnlyFile = UnixFileMode.UserRead;

    [RootDiscoveryFact]
    public void ActualResultsDescriptorDiscoversOnlySortedCanonicalReportPaths()
    {
        using var fixture = RootFixture.Create();
        fixture.WriteReport("coverage.cobertura.xml", "root report"u8.ToArray());
        fixture.WriteReport("guid-a/coverage.cobertura.xml", "nested report"u8.ToArray());
        using var root = fixture.OpenResults();

        Assert.Equal(new[] { "coverage.cobertura.xml", "guid-a/coverage.cobertura.xml" },
            Discover(root));
    }

    [RootDiscoveryTheory]
    [InlineData("non-report")]
    [InlineData("empty-nested-directory")]
    public void NonReportEntriesAndEmptyNestedDirectoriesReject(string kind)
    {
        using var fixture = RootFixture.Create();
        if (kind == "non-report") fixture.WriteFile("notes.txt", "sentinel"u8.ToArray(), ReadOnlyFile, SubjectUid, SubjectGid);
        else fixture.CreateDirectory("empty");
        using var root = fixture.OpenResults();

        Assert.Throws<IOException>(() => Discover(root));
    }

    [RootDiscoveryTheory]
    [InlineData("symlink")]
    [InlineData("hardlink")]
    [InlineData("fifo")]
    [InlineData("wrong-mode")]
    [InlineData("wrong-owner")]
    public void SpecialOrUnsafeActualEntriesRejectWithoutChangingReportBytes(string kind)
    {
        using var fixture = RootFixture.Create();
        var original = "unchanged report"u8.ToArray();
        switch (kind)
        {
            case "symlink":
                fixture.WriteFile("outside-sentinel", original, ReadOnlyFile, 0, 0, underResults: false);
                fixture.CreateSymlink("coverage.cobertura.xml", fixture.PathOutsideResults("outside-sentinel"));
                break;
            case "hardlink":
                fixture.WriteReport("a/coverage.cobertura.xml", original);
                fixture.CreateHardlink("a/coverage.cobertura.xml", "b/coverage.cobertura.xml");
                break;
            case "fifo":
                fixture.CreateFifo("coverage.cobertura.xml");
                break;
            case "wrong-mode":
                fixture.WriteFile("coverage.cobertura.xml", original,
                    UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.GroupWrite, SubjectUid, SubjectGid);
                break;
            case "wrong-owner":
                fixture.WriteReport("coverage.cobertura.xml", original, SubjectUid - 1, SubjectGid);
                break;
        }
        using var root = fixture.OpenResults();

        Assert.Throws<IOException>(() => Discover(root));
        if (kind is "hardlink" or "wrong-mode" or "wrong-owner")
            Assert.Equal(original, File.ReadAllBytes(fixture.PathFor(kind == "hardlink"
                ? "a/coverage.cobertura.xml" : "coverage.cobertura.xml")));
        if (kind == "symlink") Assert.Equal(original, File.ReadAllBytes(fixture.PathOutsideResults("outside-sentinel")));
    }

    [RootDiscoveryFact]
    public void SixtyFourthReportIsAcceptedAndTheNextActualReportIsRejected()
    {
        using var fixture = RootFixture.Create();
        for (var index = 0; index < LinuxRetainedReportSet.MaximumFiles; index++)
            fixture.WriteReport($"{index:D3}/coverage.cobertura.xml", "report"u8.ToArray());
        using var root = fixture.OpenResults();
        Assert.Equal(LinuxRetainedReportSet.MaximumFiles, Discover(root).Length);

        fixture.WriteReport("064/coverage.cobertura.xml", "overflow report"u8.ToArray());
        Assert.Throws<IOException>(() => Discover(root));
        Assert.Equal("overflow report", Encoding.UTF8.GetString(
            File.ReadAllBytes(fixture.PathFor("064/coverage.cobertura.xml"))));
    }

    [RootDiscoveryFact]
    public void ThirtySecondPathComponentIsAcceptedAndThirtyThirdIsRejected()
    {
        using var fixture = RootFixture.Create();
        var accepted = string.Join('/', Enumerable.Repeat("a", 31)) + "/coverage.cobertura.xml";
        fixture.WriteReport(accepted, "depth boundary"u8.ToArray());
        using var root = fixture.OpenResults();
        Assert.Equal(new[] { accepted }, Discover(root));

        var rejected = string.Join('/', Enumerable.Repeat("b", 32)) + "/coverage.cobertura.xml";
        fixture.WriteReport(rejected, "too deep"u8.ToArray());
        Assert.Throws<IOException>(() => Discover(root));
        Assert.Equal("too deep", Encoding.UTF8.GetString(File.ReadAllBytes(fixture.PathFor(rejected))));
    }

    [RootDiscoveryFact]
    public void PreCancelledDiscoveryStopsBeforeNativeEnumeration()
    {
        using var fixture = RootFixture.Create();
        fixture.WriteReport("coverage.cobertura.xml", "unchanged"u8.ToArray());
        using var root = fixture.OpenResults();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var error = Assert.Throws<OperationCanceledException>(() => Discover(root, cancelled.Token));
        Assert.Equal(cancelled.Token, error.CancellationToken);
        Assert.Equal("unchanged", Encoding.UTF8.GetString(
            File.ReadAllBytes(fixture.PathFor("coverage.cobertura.xml"))));
    }

    private static string[] Discover(SafeFileHandle root, CancellationToken token = default) =>
        LinuxProducerReportDiscovery.Discover(root, SubjectUid, SubjectGid,
            new LinuxInputDeadline(TimeProvider.System.GetTimestamp(), TimeSpan.FromMinutes(1)), token);

    private sealed class RootDiscoveryFactAttribute : FactAttribute
    {
        public RootDiscoveryFactAttribute()
        {
            if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64 || GetEffectiveUid() != 0)
                Skip = "Requires actual Linux x64 root for an exclusive /run fixture; a skip is not native proof.";
        }
    }

    private sealed class RootDiscoveryTheoryAttribute : TheoryAttribute
    {
        public RootDiscoveryTheoryAttribute()
        {
            if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64 || GetEffectiveUid() != 0)
                Skip = "Requires actual Linux x64 root for an exclusive /run fixture; a skip is not native proof.";
        }
    }

    private sealed class RootFixture : IDisposable
    {
        private const int AtFdcwd = -100;
        private const long OpenAt2Number = 437;
        private const ulong DirectoryFlags = 0x10000 | 0x80000; // O_DIRECTORY | O_CLOEXEC.
        private const ulong NoSymlinkResolution = 0x02 | 0x04; // RESOLVE_NO_MAGICLINKS | RESOLVE_NO_SYMLINKS.
        private readonly string _root;

        private RootFixture(string root, string results) { _root = root; Results = results; }
        internal string Results { get; }

        internal string PathFor(string relative) => TestPathUtils.PathUnder(Results, relative.Split('/'));
        internal string PathOutsideResults(string relative) => TestPathUtils.PathUnder(_root, relative);

        internal static RootFixture Create()
        {
            var root = TestPathUtils.PathUnder("/run", "issue779-producer-report-discovery-" + Guid.NewGuid().ToString("N"));
            if (!root.StartsWith("/run/issue779-producer-report-discovery-", StringComparison.Ordinal)
                || root.Contains("/../", StringComparison.Ordinal))
                throw new InvalidOperationException("The native discovery fixture escaped its fixed /run namespace.");
            if (MakeDirectory(root, 0x1c0) != 0)
                throw new IOException($"Exclusive native fixture mkdir failed, errno {Marshal.GetLastPInvokeError()}.");
            var results = TestPathUtils.PathUnder(root, "results");
            var fixture = new RootFixture(root, results);
            try
            {
                File.SetUnixFileMode(root, PrivateDirectory);
                Directory.CreateDirectory(results);
                File.SetUnixFileMode(results, PrivateDirectory);
                SetOwner(results, SubjectUid, SubjectGid);
                return fixture;
            }
            catch
            {
                fixture.Dispose();
                throw;
            }
        }

        internal SafeFileHandle OpenResults()
        {
            var how = new OpenHow { Flags = DirectoryFlags, Resolve = NoSymlinkResolution };
            var descriptor = Syscall(OpenAt2Number, AtFdcwd, Results, ref how, (nuint)Marshal.SizeOf<OpenHow>());
            if (descriptor < 0) throw new IOException($"Opening actual results directory failed, errno {Marshal.GetLastPInvokeError()}.");
            return new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
        }

        internal void WriteReport(string relative, byte[] bytes, uint uid = SubjectUid, uint gid = SubjectGid) =>
            WriteFile(relative, bytes, ReadOnlyFile, uid, gid);

        internal void WriteFile(string relative, byte[] bytes, UnixFileMode mode, uint uid, uint gid,
            bool underResults = true)
        {
            var path = underResults ? PathFor(relative) : PathOutsideResults(relative);
            if (underResults) EnsureParentDirectories(relative);
            File.WriteAllBytes(path, bytes);
            File.SetUnixFileMode(path, mode);
            SetOwner(path, uid, gid);
        }

        internal void CreateDirectory(string relative)
        {
            var path = PathFor(relative);
            Directory.CreateDirectory(path);
            File.SetUnixFileMode(path, PrivateDirectory);
            SetOwner(path, SubjectUid, SubjectGid);
        }

        internal void CreateSymlink(string relative, string target)
        {
            EnsureParentDirectories(relative);
            File.CreateSymbolicLink(PathFor(relative), target);
        }

        internal void CreateHardlink(string existingRelative, string newRelative)
        {
            EnsureParentDirectories(newRelative);
            if (Link(PathFor(existingRelative), PathFor(newRelative)) != 0)
                throw new IOException($"Creating actual report hard link failed, errno {Marshal.GetLastPInvokeError()}.");
        }

        internal void CreateFifo(string relative)
        {
            EnsureParentDirectories(relative);
            if (MakeFifo(PathFor(relative), 0x100) != 0)
                throw new IOException($"Creating actual report FIFO failed, errno {Marshal.GetLastPInvokeError()}.");
            SetOwner(PathFor(relative), SubjectUid, SubjectGid);
        }

        private void EnsureParentDirectories(string relative)
        {
            var parts = relative.Split('/');
            var current = Results;
            foreach (var part in parts[..^1])
            {
                current = TestPathUtils.PathUnder(current, part);
                if (Directory.Exists(current)) continue;
                Directory.CreateDirectory(current);
                File.SetUnixFileMode(current, PrivateDirectory);
                SetOwner(current, SubjectUid, SubjectGid);
            }
        }

        public void Dispose()
        {
            if (!Directory.Exists(_root)) return;
            foreach (var directory in Directory.GetDirectories(_root, "*", SearchOption.AllDirectories)
                         .OrderByDescending(static path => path.Length))
            {
                SetOwner(directory, 0, 0);
                File.SetUnixFileMode(directory, PrivateDirectory);
            }
            if (Directory.Exists(Results))
            {
                SetOwner(Results, 0, 0);
                File.SetUnixFileMode(Results, PrivateDirectory);
            }
            File.SetUnixFileMode(_root, PrivateDirectory);
            Directory.Delete(_root, recursive: true);
        }

        private static void SetOwner(string path, uint uid, uint gid)
        {
            if (Chown(path, uid, gid) != 0)
                throw new IOException($"Setting actual fixture owner failed, errno {Marshal.GetLastPInvokeError()}.");
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct OpenHow { internal ulong Flags; internal ulong Mode; internal ulong Resolve; }

        [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
        private static extern long Syscall(long number, int dirfd,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string path, ref OpenHow how, nuint size);
        [DllImport("libc", EntryPoint = "mkdir", SetLastError = true)]
        private static extern int MakeDirectory([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
        [DllImport("libc", EntryPoint = "chown", SetLastError = true)]
        private static extern int Chown([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint uid, uint gid);
        [DllImport("libc", EntryPoint = "link", SetLastError = true)]
        private static extern int Link([MarshalAs(UnmanagedType.LPUTF8Str)] string existing,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string created);
        [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
        private static extern int MakeFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUid();
}
