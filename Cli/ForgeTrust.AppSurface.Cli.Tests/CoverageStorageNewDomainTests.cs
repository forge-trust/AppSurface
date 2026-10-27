using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Verifies preview/apply separation and preservation when schema-v1 continuity cannot be proved.</summary>
[Collection("CoverageGate process state")]
public sealed class CoverageStorageCommandNewDomainTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewDomain_NeverOpensOldStoreOrChangesConfiguration(bool apply)
    {
        using var temp = TestDirectory.Create();
        var old = temp.CreateDirectory("old");
        var marker = temp.WriteFile("old/store.json", "unproven metadata must survive");
        var destination = TestPathUtils.PathUnder(temp.Path, "fresh-domain");
        var prior = Environment.GetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME");
        Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", old);
        try
        {
            using var console = new FakeInMemoryConsole();
            await new CoverageStorageNewDomainCommand { Json = true, Apply = apply, Output = destination }.ExecuteAsync(console, CancellationToken.None);
            using var result = JsonDocument.Parse(console.ReadOutputString());
            Assert.Equal("complete", result.RootElement.GetProperty("status").GetString());
            Assert.Equal(apply ? "complete" : "none", result.RootElement.GetProperty("sideEffects").GetString());
            Assert.Equal("new-domain", result.RootElement.GetProperty("observation").GetProperty("scope").GetString());
            Assert.False(result.RootElement.GetProperty("data").GetProperty("historyTransferred").GetBoolean());
            Assert.False(result.RootElement.GetProperty("data").GetProperty("configurationChanged").GetBoolean());
            Assert.DoesNotContain(destination, console.ReadOutputString(), StringComparison.Ordinal);
            Assert.Equal(apply, Directory.Exists(destination));
            Assert.Equal("unproven metadata must survive", File.ReadAllText(marker));
            Assert.Equal(old, Environment.GetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME"));
            Assert.Single(Directory.GetFileSystemEntries(old));
            if (apply)
            {
                using var store = CoverageArtifactStore.OpenReadOnly(destination);
                Assert.Equal(0, store.GetStatus().RegisteredRuns);
                Assert.Equal(0, store.GetStatus().ArchivedRuns);
            }
        }
        finally { Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", prior); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewDomain_RefusesExistingDestinationsWithoutChangingTheirBytes(bool apply)
    {
        using var temp = TestDirectory.Create();
        var destination = temp.CreateDirectory("exists");
        var sentinel = temp.WriteFile("exists/sentinel", "preserve");
        Assert.Throws<IOException>(() => CoverageArtifactStore.CreateNewDomain(destination, apply, CancellationToken.None));
        Assert.Equal("preserve", File.ReadAllText(sentinel));
        Assert.Single(Directory.EnumerateFileSystemEntries(destination));
    }

    [Fact]
    public void NewDomain_RefusesLinkedParentOrRootAndMissingParent()
    {
        using var temp = TestDirectory.Create();
        var parent = temp.CreateDirectory("parent");
        var link = TestPathUtils.PathUnder(temp.Path, "link");
        TestFileSystem.CreateDirectoryLinkOrSkip(link, parent);
        Assert.Throws<IOException>(() => CoverageArtifactStore.CreateNewDomain(TestPathUtils.PathUnder(link, "fresh"), true, CancellationToken.None));
        Assert.Throws<IOException>(() => CoverageArtifactStore.CreateNewDomain(link, true, CancellationToken.None));
        Assert.Throws<DirectoryNotFoundException>(() => CoverageArtifactStore.CreateNewDomain(TestPathUtils.PathUnder(temp.Path, "missing", "fresh"), true, CancellationToken.None));
        Assert.Empty(Directory.EnumerateFileSystemEntries(parent));
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative")]
    public async Task NewDomain_InvalidInputFailsBeforeMutation(string output)
    {
        using var console = new FakeInMemoryConsole();
        var error = await Assert.ThrowsAsync<CliFx.CommandException>(async () =>
            await new CoverageStorageNewDomainCommand { Output = output, Apply = true, Json = true }.ExecuteAsync(console, CancellationToken.None));
        Assert.Equal(2, error.ExitCode);
        Assert.Contains("fresh absolute", console.ReadOutputString(), StringComparison.Ordinal);
        Assert.ThrowsAny<ArgumentException>(() => CoverageArtifactStore.CreateNewDomain(output, true, CancellationToken.None));
    }

    [Fact]
    public void NewDomain_CancellationAndBroadRootNeverCreateOrAdopt()
    {
        using var temp = TestDirectory.Create();
        var destination = TestPathUtils.PathUnder(temp.Path, "fresh");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => CoverageArtifactStore.CreateNewDomain(destination, true, cancelled.Token));
        Assert.False(Directory.Exists(destination));
        Assert.Throws<IOException>(() => CoverageArtifactStore.CreateNewDomain(Path.GetPathRoot(temp.Path)!, true, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewDomain_InterruptedPublicationPreservesPartialMetadataAndRefusesReinitialization(bool cancel)
    {
        using var temp = TestDirectory.Create();
        var destination = TestPathUtils.PathUnder(temp.Path, "partial-domain");
        using var cancellation = new CancellationTokenSource();
        var exception = Record.Exception(() => CoverageArtifactStore.CreateNewDomain(destination, true, cancellation.Token, () =>
        {
            if (cancel) cancellation.Cancel();
            else throw new IOException("simulated interruption before registry publication");
        }));
        if (cancel) Assert.IsType<OperationCanceledException>(exception);
        else Assert.IsType<IOException>(exception);
        var marker = TestPathUtils.PathUnder(destination, "store.json");
        var before = File.ReadAllBytes(marker);
        Assert.False(File.Exists(TestPathUtils.PathUnder(destination, "registry.json")));
        Assert.Throws<IOException>(() => CoverageArtifactStore.CreateNewDomain(destination, true, CancellationToken.None));
        Assert.Throws<InvalidDataException>(() => CoverageArtifactStore.Open(destination));
        Assert.Equal(before, File.ReadAllBytes(marker));
        Assert.False(File.Exists(TestPathUtils.PathUnder(destination, "registry.json")));
    }

    [Fact]
    public void NewDomain_DoesNotStealActiveOldLeaseOrAdoptOldHistory()
    {
        using var temp = TestDirectory.Create();
        var old = TestPathUtils.PathUnder(temp.Path, "old-domain");
        using (CoverageArtifactStore.Open(old)) { }
        using var lease = CoverageArtifactOperationLease.Acquire(old, true);
        var marker = File.ReadAllBytes(TestPathUtils.PathUnder(old, "store.json"));
        var fresh = TestPathUtils.PathUnder(temp.Path, "fresh-domain");
        CoverageArtifactStore.CreateNewDomain(fresh, true, CancellationToken.None);
        lease.VerifyBinding();
        Assert.Throws<CoverageExecutionException>(() => CoverageArtifactOperationLease.Acquire(old, true));
        Assert.Equal(marker, File.ReadAllBytes(TestPathUtils.PathUnder(old, "store.json")));
        using var store = CoverageArtifactStore.OpenReadOnly(fresh);
        Assert.Equal(0, store.GetStatus().RegisteredRuns);
    }

    [Fact]
    public void NativeCreate_RefusesACompetingCreatorBetweenPreflightAndPublication()
    {
        using var temp = TestDirectory.Create();
        using var parent = CoverageRunOutputLease.AcquireArtifactDirectory(temp.Path, false, false)!;
        var destination = TestPathUtils.PathUnder(temp.Path, "competing-domain");
        var sentinel = TestPathUtils.PathUnder(destination, "sentinel");
        Assert.Throws<IOException>(() => parent.CreateArtifactDirectory("competing-domain", () =>
        {
            Directory.CreateDirectory(destination);
            File.WriteAllText(sentinel, "belongs to the competing creator");
        }));
        Assert.Equal("belongs to the competing creator", File.ReadAllText(sentinel));
        Assert.Single(Directory.EnumerateFileSystemEntries(destination));
        Assert.False(File.Exists(TestPathUtils.PathUnder(destination, "store.json")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WindowsDirectoryPublication_UsesPinnedSecurityAndPreservesNativeNoReplaceFailure(bool created)
    {
        // Managed native-call simulation, separate from the real Windows race/ACL lane.
        var calls = 0;
        var error = Record.Exception(() => CoverageRunOutputLease.CreateArtifactWindowsDirectory(@"C:\private\fresh", [7, 11], (path, attributes) =>
        {
            calls++;
            Assert.Equal(@"\\?\C:\private\fresh", path);
            Assert.NotEqual(0, attributes);
            Assert.Equal(IntPtr.Size == 8 ? 24 : 12, Marshal.ReadInt32(attributes));
            var security = Marshal.ReadIntPtr(attributes, IntPtr.Size == 8 ? 8 : 4);
            Assert.Equal(7, Marshal.ReadByte(security));
            Assert.Equal(11, Marshal.ReadByte(security, 1));
            Assert.Equal(0, Marshal.ReadInt32(attributes, IntPtr.Size == 8 ? 16 : 8));
            Marshal.SetLastPInvokeError(183); // ERROR_ALREADY_EXISTS must refuse, never adopt.
            return created;
        }));
        Assert.Equal(1, calls);
        if (created) Assert.Null(error);
        else Assert.Equal(183, Assert.IsType<Win32Exception>(Assert.IsType<IOException>(error).InnerException).NativeErrorCode);
        Assert.Throws<ArgumentNullException>(() => CoverageRunOutputLease.CreateArtifactWindowsDirectory(@"C:\private\fresh", null!));
    }
}
