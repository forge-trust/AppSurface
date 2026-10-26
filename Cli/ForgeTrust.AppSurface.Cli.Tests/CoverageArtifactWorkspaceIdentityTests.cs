using System.Runtime.Versioning;
using ForgeTrust.AppSurface.Evidence.Aspire;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Exercises workspace identity in the unchanged native coverage selection.</summary>
public sealed class CoverageArtifactWorkspaceIdentityTests
{
    [Theory]
    [InlineData(0UL, 0UL, false)]
    [InlineData(1UL, 0UL, true)]
    [InlineData(0UL, 1UL, true)]
    public void NativeFileId_RejectsUnavailableIdentityAndUsesBothHalves(ulong low, ulong high, bool usable)
        => Assert.Equal(usable, EvidenceWorkspaceWindowsIdentity.IsUsableFileId(low, high));

    [Theory]
    [InlineData(@"C:\scratch\workspace", @"\\?\C:\scratch\workspace")]
    [InlineData("C:/scratch/workspace", @"\\?\C:\scratch\workspace")]
    [InlineData(@"\\server\share\workspace", @"\\?\UNC\server\share\workspace")]
    [InlineData(@"\\?\C:\scratch\workspace", @"\\?\C:\scratch\workspace")]
    [InlineData(@"\\?\UNC\server\share\workspace", @"\\?\UNC\server\share\workspace")]
    public void NativePath_PreservesAbsoluteDriveAndUncIdentity(string path, string expected)
        => Assert.Equal(expected, EvidenceWorkspaceWindowsIdentity.ExtendedPath(path));

    [CoverageWindowsFact]
    [SupportedOSPlatform("windows")]
    public void RetainedIdentity_RejectsInvalidObjectsAndDistinguishesARenamedOriginal()
    {
        using var parent = TestDirectory.Create();
        var file = Path.Join(parent.Path, "caller.txt");
        File.WriteAllText(file, "caller bytes");
        Assert.Throws<IOException>(() => EvidenceWorkspaceWindowsIdentity.Open(Path.Join(parent.Path, "missing")));
        Assert.Throws<IOException>(() => EvidenceWorkspaceWindowsIdentity.Open(file));
        var root = Path.Join(parent.Path, "root");
        Directory.CreateDirectory(root);
        using var identity = EvidenceWorkspaceWindowsIdentity.Open(root);
        Assert.True(identity.Matches(root));
        var retained = Path.Join(parent.Path, "retained");
        Directory.Move(root, retained);
        Directory.CreateDirectory(root);
        Assert.False(identity.Matches(root));
        Assert.True(identity.Matches(retained));
        identity.Dispose();
        Assert.False(identity.Matches(retained));
        Assert.Equal("caller bytes", File.ReadAllText(file));
    }

    [CoverageWindowsFact]
    [SupportedOSPlatform("windows")]
    public async Task RetainedIdentity_RejectsAJunctionWithoutFollowingItsTarget()
    {
        using var parent = TestDirectory.Create();
        var target = Path.Join(parent.Path, "target");
        Directory.CreateDirectory(target);
        var sentinel = Path.Join(target, "caller.txt");
        File.WriteAllText(sentinel, "caller bytes");
        var link = Path.Join(parent.Path, "linked");
        var created = await new EvidenceCommandRunner().RunAsync(new EvidenceCommandRequest("cmd.exe",
            ["/d", "/c", "mklink", "/J", link, target], parent.Path, TimeSpan.FromSeconds(10)));
        Assert.True(created.ExitCode == 0 && !created.OutputTruncated, created.StandardError);
        try
        {
            Assert.Throws<IOException>(() => EvidenceWorkspaceWindowsIdentity.Open(link));
            Assert.Equal("caller bytes", File.ReadAllText(sentinel));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task CopiedMarkerAndTimestamps_CannotAuthorizeAReplacementRoot()
    {
        using var parent = TestDirectory.Create();
        var workspace = EvidenceWorkspace.Create(parent.Path, new EvidenceWorkspaceOptions(MinimumFreeBytes: 0));
        var root = workspace.RootPath;
        var marker = Path.Join(root, ".appsurface-evidence-workspace-owner");
        var bytes = File.ReadAllBytes(marker);
        var rootCreated = Directory.GetCreationTimeUtc(root);
        var markerCreated = File.GetCreationTimeUtc(marker);
        var retained = Path.Join(parent.Path, "original");
        Directory.Move(root, retained);
        Directory.CreateDirectory(root);
        File.WriteAllBytes(marker, bytes);
        var sentinel = Path.Join(root, "caller.txt");
        File.WriteAllText(sentinel, "replacement bytes");
        if (OperatingSystem.IsWindows())
        {
            Directory.SetCreationTimeUtc(root, rootCreated);
            File.SetCreationTimeUtc(marker, markerCreated);
            Assert.Equal(rootCreated, Directory.GetCreationTimeUtc(root));
            Assert.Equal(markerCreated, File.GetCreationTimeUtc(marker));
        }

        try
        {
            Assert.Throws<IOException>(workspace.CheckBudget);
            Assert.Throws<IOException>(() => workspace.GetPath("new-output.txt"));
            await Assert.ThrowsAsync<IOException>(async () => await workspace.DisposeAsync());
            Assert.Equal("replacement bytes", File.ReadAllText(sentinel));
            Assert.Equal(bytes, File.ReadAllBytes(marker));
            Assert.True(Directory.Exists(retained));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Move(retained, root);
            await workspace.DisposeAsync();
        }
        Assert.False(Directory.Exists(root));
    }
}
