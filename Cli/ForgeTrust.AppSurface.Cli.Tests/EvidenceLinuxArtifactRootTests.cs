using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceLinuxArtifactRootTests
{
    [Fact]
    public async Task Linux_root_retains_handles_and_rejects_namespace_or_content_drift()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Throws<PlatformNotSupportedException>(() => EvidenceLinuxArtifactRoot.Allocate("/tmp", default, "slot", 0, 0));
            return;
        }

        var parent = NewPrivateDirectory();
        try
        {
            var identity = EvidenceLinuxArtifactRoot.InspectDirectoryIdentity(parent);
            var bytes = Encoding.UTF8.GetBytes("retained evidence payload");
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            await using (var root = EvidenceLinuxArtifactRoot.Allocate(parent, identity, "run-slot", identity.Uid, identity.Gid))
            {
                Assert.NotEqual(default, root.Identity);
                await root.WriteAsync("nested/evidence.bin", bytes, default);
                await root.VerifyAsync("nested/evidence.bin", bytes.Length, hash, default);
                await Assert.ThrowsAsync<IOException>(() => root.WriteAsync("nested/evidence.bin", bytes, default).AsTask());
                await Assert.ThrowsAsync<IOException>(() => root.VerifyAsync("nested/evidence.bin", bytes.Length, new string('0', 64), default).AsTask());
                await Assert.ThrowsAsync<ArgumentException>(() => root.WriteAsync("../escape", bytes, default).AsTask());

                var file = Path.Combine(parent, "run-slot", "nested", "evidence.bin");
                File.WriteAllBytes(file, Encoding.UTF8.GetBytes(new string('x', bytes.Length)));
                await Assert.ThrowsAsync<IOException>(() => root.VerifyAsync("nested/evidence.bin", bytes.Length, hash, default).AsTask());
                if (OperatingSystem.IsLinux()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
                await Assert.ThrowsAsync<IOException>(() => root.VerifyAsync("nested/evidence.bin", bytes.Length, hash, default).AsTask());
            }
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public void Allocation_rejects_collision_symlink_and_parent_identity_substitution()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Throws<PlatformNotSupportedException>(() => EvidenceLinuxArtifactRoot.Allocate("/tmp", default, "slot", 0, 0));
            return;
        }

        var parent = NewPrivateDirectory();
        var occupied = Path.Combine(parent, "occupied");
        Directory.CreateDirectory(occupied);
        try
        {
            var identity = EvidenceLinuxArtifactRoot.InspectDirectoryIdentity(parent);
            Assert.Throws<IOException>(() => EvidenceLinuxArtifactRoot.Allocate(parent, identity, "occupied", identity.Uid, identity.Gid));

            var symlink = Path.Combine(parent, "linked");
            Directory.CreateSymbolicLink(symlink, occupied);
            Assert.Throws<IOException>(() => EvidenceLinuxArtifactRoot.Allocate(parent, identity, "linked", identity.Uid, identity.Gid));

            var moved = parent + "-moved";
            Directory.Move(parent, moved);
            Directory.CreateDirectory(parent);
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Assert.Throws<IOException>(() => EvidenceLinuxArtifactRoot.Allocate(parent, identity, "substituted", identity.Uid, identity.Gid));
            Directory.Delete(parent);
            Directory.Delete(moved, recursive: true);
        }
        finally
        {
            if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
            if (Directory.Exists(parent + "-moved")) Directory.Delete(parent + "-moved", recursive: true);
        }
    }

    [Fact]
    public async Task Verification_rejects_unlink_replacement_and_hardlink()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Throws<PlatformNotSupportedException>(() => EvidenceLinuxArtifactRoot.Allocate("/tmp", default, "slot", 0, 0));
            return;
        }

        var parent = NewPrivateDirectory();
        try
        {
            var identity = EvidenceLinuxArtifactRoot.InspectDirectoryIdentity(parent);
            var payload = Encoding.UTF8.GetBytes("payload");
            var digest = Convert.ToHexString(SHA256.HashData(payload));
            await using (var root = EvidenceLinuxArtifactRoot.Allocate(parent, identity, "run", identity.Uid, identity.Gid))
            {
                await root.WriteAsync("first.bin", payload, default);
                var original = Path.Combine(parent, "run", "first.bin");
                Assert.Equal(0, Link(original, Path.Combine(parent, "run", "alias.bin")));
                await Assert.ThrowsAsync<IOException>(() => root.VerifyAsync("first.bin", payload.Length, digest, default).AsTask());
            }

            await using (var root = EvidenceLinuxArtifactRoot.Allocate(parent, identity, "run-two", identity.Uid, identity.Gid))
            {
                await root.WriteAsync("replace.bin", payload, default);
                var file = Path.Combine(parent, "run-two", "replace.bin");
                File.Delete(file);
                File.WriteAllBytes(file, payload);
                await Assert.ThrowsAsync<IOException>(() => root.VerifyAsync("replace.bin", payload.Length, digest, default).AsTask());
            }
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public async Task Rejected_nested_paths_release_every_intermediate_directory_descriptor()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Throws<PlatformNotSupportedException>(() => EvidenceLinuxArtifactRoot.Allocate("/tmp", default, "slot", 0, 0));
            return;
        }

        var parent = NewPrivateDirectory();
        try
        {
            var identity = EvidenceLinuxArtifactRoot.InspectDirectoryIdentity(parent);
            await using var root = EvidenceLinuxArtifactRoot.Allocate(parent, identity, "run", identity.Uid, identity.Gid);
            var nested = Path.Combine(parent, "run", "nested");
            Directory.CreateDirectory(nested);
            File.SetUnixFileMode(nested, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.CreateSymbolicLink(Path.Combine(nested, "linked"), parent);

            for (var attempt = 0; attempt < 16; attempt++)
                await Assert.ThrowsAsync<IOException>(() => root.WriteAsync("nested/linked/rejected.bin", "rejected"u8.ToArray(), default).AsTask());

            // The directory is unique to this test. Inspect only descriptors naming that object,
            // so unrelated concurrent tests cannot affect the ownership assertion.
            Assert.DoesNotContain(Directory.EnumerateFileSystemEntries("/proc/self/fd"), descriptor => NamesDirectory(descriptor, nested));
            Assert.False(File.Exists(Path.Combine(parent, "rejected.bin")));
            var payload = "allowed neighboring artifact"u8.ToArray();
            await root.WriteAsync("nested/allowed.bin", payload, default);
            await root.VerifyAsync("nested/allowed.bin", payload.Length, Convert.ToHexString(SHA256.HashData(payload)), default);
            Assert.DoesNotContain(Directory.EnumerateFileSystemEntries("/proc/self/fd"), descriptor => NamesDirectory(descriptor, nested));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    private static bool NamesDirectory(string descriptor, string directory)
    {
        try { return new FileInfo(descriptor).LinkTarget == directory; }
        catch (IOException) { return false; } // A descriptor owned by unrelated work may disappear.
    }

    [Fact]
    public void Names_and_size_limits_are_checked_before_filesystem_mutation()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Throws<PlatformNotSupportedException>(() => EvidenceLinuxArtifactRoot.Allocate("/tmp", default, "slot", 0, 0));
            return;
        }

        var parent = NewPrivateDirectory();
        try
        {
            var identity = EvidenceLinuxArtifactRoot.InspectDirectoryIdentity(parent);
            foreach (var name in new[] { "", ".", "..", "a/b", "a\\b", "bad\nname" })
                Assert.Throws<ArgumentException>(() => EvidenceLinuxArtifactRoot.Allocate(parent, identity, name, identity.Uid, identity.Gid));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public void Unbounded_or_relative_parent_paths_are_rejected_before_platform_or_allocation_work()
    {
        foreach (var path in new[] { null, " ", "relative-parent", "/" + new string('a', 4097) })
        {
            Assert.ThrowsAny<ArgumentException>(() => EvidenceLinuxArtifactRoot.Allocate(path!, default, "run", 1, 1));
            Assert.ThrowsAny<ArgumentException>(() => EvidenceLinuxArtifactRoot.InspectDirectoryIdentity(path!));
        }
    }

    [Fact]
    public void Allocation_requires_exact_parent_owner_group_and_mode_without_creating_a_slot()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Throws<PlatformNotSupportedException>(() => EvidenceLinuxArtifactRoot.Allocate("/tmp", default, "slot", 0, 0));
            return;
        }

        var parent = NewPrivateDirectory();
        try
        {
            var identity = EvidenceLinuxArtifactRoot.InspectDirectoryIdentity(parent);
            Assert.Throws<IOException>(() => EvidenceLinuxArtifactRoot.Allocate(parent, identity, "wrong-owner", identity.Uid ^ 1, identity.Gid));
            Assert.Throws<IOException>(() => EvidenceLinuxArtifactRoot.Allocate(parent, identity, "wrong-group", identity.Uid, identity.Gid ^ 1));
            File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
            Assert.Throws<IOException>(() => EvidenceLinuxArtifactRoot.Allocate(parent, identity, "shared-parent", identity.Uid, identity.Gid));
            Assert.Empty(Directory.EnumerateFileSystemEntries(parent));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public async Task Verification_requires_retained_bytes_length_and_a_well_formed_digest()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Throws<PlatformNotSupportedException>(() => EvidenceLinuxArtifactRoot.Allocate("/tmp", default, "slot", 0, 0));
            return;
        }

        var parent = NewPrivateDirectory();
        try
        {
            var identity = EvidenceLinuxArtifactRoot.InspectDirectoryIdentity(parent);
            await using var root = EvidenceLinuxArtifactRoot.Allocate(parent, identity, "run", identity.Uid, identity.Gid);
            var payload = "retained"u8.ToArray();
            var hash = Convert.ToHexString(SHA256.HashData(payload));
            await root.WriteAsync("report.bin", payload, default);
            foreach (var length in new[] { -1L, 256L * 1024 * 1024 + 1 })
                await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => root.VerifyAsync("report.bin", length, hash, default).AsTask());
            foreach (var invalid in new[] { null, "", new string('g', 64), new string('a', 63), new string('a', 65) })
                await Assert.ThrowsAsync<ArgumentException>(() => root.VerifyAsync("report.bin", payload.Length, invalid!, default).AsTask());
            await Assert.ThrowsAsync<IOException>(() => root.VerifyAsync("unwritten.bin", payload.Length, hash, default).AsTask());
            await Assert.ThrowsAsync<IOException>(() => root.VerifyAsync("report.bin", payload.Length + 1, hash, default).AsTask());
            await root.VerifyAsync("report.bin", payload.Length, hash.ToLowerInvariant(), default);
            await root.WriteAsync("empty.bin", ReadOnlyMemory<byte>.Empty, default);
            await root.VerifyAsync("empty.bin", 0, Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())), default);
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replacing_a_retained_root_or_parent_prevents_further_writes_and_verification(bool replaceParent)
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Throws<PlatformNotSupportedException>(() => EvidenceLinuxArtifactRoot.Allocate("/tmp", default, "slot", 0, 0));
            return;
        }

        var parent = NewPrivateDirectory();
        var original = parent + "-retained";
        try
        {
            var identity = EvidenceLinuxArtifactRoot.InspectDirectoryIdentity(parent);
            await using var root = EvidenceLinuxArtifactRoot.Allocate(parent, identity, "run", identity.Uid, identity.Gid);
            var payload = "original bytes"u8.ToArray();
            await root.WriteAsync("report.bin", payload, default);
            var replaced = replaceParent ? parent : TestPathUtils.PathUnder(parent, "run");
            original = replaced + "-retained";
            Directory.Move(replaced, original);
            Directory.CreateDirectory(replaced);
            File.SetUnixFileMode(replaced, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await Assert.ThrowsAsync<IOException>(() => root.WriteAsync("late.bin", payload, default).AsTask());
            await Assert.ThrowsAsync<IOException>(() => root.VerifyAsync("report.bin", payload.Length, Convert.ToHexString(SHA256.HashData(payload)), default).AsTask());
            Assert.Empty(Directory.EnumerateFileSystemEntries(replaced));
            var retainedReport = TestPathUtils.PathUnder(original, replaceParent ? "run/report.bin" : "report.bin");
            Assert.Equal(payload, File.ReadAllBytes(retainedReport));
        }
        finally
        {
            if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
            if (Directory.Exists(original)) Directory.Delete(original, recursive: true);
        }
    }

    [Fact]
    public async Task Cancellation_and_disposal_cannot_start_new_artifact_work()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Throws<PlatformNotSupportedException>(() => EvidenceLinuxArtifactRoot.Allocate("/tmp", default, "slot", 0, 0));
            return;
        }

        var parent = NewPrivateDirectory();
        try
        {
            var identity = EvidenceLinuxArtifactRoot.InspectDirectoryIdentity(parent);
            await using var root = EvidenceLinuxArtifactRoot.Allocate(parent, identity, "run", identity.Uid, identity.Gid);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var payload = "neighbor"u8.ToArray();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => root.WriteAsync("cancelled.bin", payload, cancellation.Token).AsTask());
            Assert.Empty(Directory.EnumerateFileSystemEntries(TestPathUtils.PathUnder(parent, "run")));
            await root.WriteAsync("allowed.bin", payload, default);
            var hash = Convert.ToHexString(SHA256.HashData(payload));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => root.VerifyAsync("allowed.bin", payload.Length, hash, cancellation.Token).AsTask());
            await root.VerifyAsync("allowed.bin", payload.Length, hash, default);
            await root.DisposeAsync();
            await root.DisposeAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => root.WriteAsync("late.bin", payload, default).AsTask());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => root.VerifyAsync("allowed.bin", payload.Length, hash, default).AsTask());
            Assert.False(File.Exists(TestPathUtils.PathUnder(parent, "run", "late.bin")));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public async Task Unsafe_nested_directory_mode_rejects_writes_without_leaking_a_descriptor()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Throws<PlatformNotSupportedException>(() => EvidenceLinuxArtifactRoot.Allocate("/tmp", default, "slot", 0, 0));
            return;
        }

        var parent = NewPrivateDirectory();
        try
        {
            var identity = EvidenceLinuxArtifactRoot.InspectDirectoryIdentity(parent);
            await using var root = EvidenceLinuxArtifactRoot.Allocate(parent, identity, "run", identity.Uid, identity.Gid);
            var nested = TestPathUtils.PathUnder(parent, "run", "nested");
            Directory.CreateDirectory(nested);
            File.SetUnixFileMode(nested, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead);
            await Assert.ThrowsAsync<IOException>(() => root.WriteAsync("nested/rejected.bin", "payload"u8.ToArray(), default).AsTask());
            Assert.Empty(Directory.EnumerateFileSystemEntries(nested));
            Assert.DoesNotContain(Directory.EnumerateFileSystemEntries("/proc/self/fd"), descriptor => NamesDirectory(descriptor, nested));
            File.SetUnixFileMode(nested, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await root.WriteAsync("nested/allowed.bin", "payload"u8.ToArray(), default);
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    private static string NewPrivateDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "evidence-linux-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link([MarshalAs(UnmanagedType.LPUTF8Str)] string existingPath, [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);
}
