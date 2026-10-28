using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Verifies preview/apply separation and preservation when schema-v1 continuity cannot be proved.</summary>
[Collection("CoverageGate process state")]
public sealed class CoverageStorageCommandNewDomainTests
{
    /// <summary>Refuses a moved retained parent on Unix and verifies native rename prevention on Windows without creating a domain.</summary>
    [Fact]
    public void NewDomain_ParentDisappearingBeforeInspectionNeverCreatesOrAdopts()
    {
        using var temp = TestDirectory.Create();
        var parent = temp.CreateDirectory("parent");
        var detached = Path.Join(temp.Path, "detached-parent");
        File.WriteAllText(Path.Join(parent, "sentinel"), "preserve parent bytes");
        var destination = Path.Join(parent, "fresh");
        var error = Record.Exception(() => CoverageArtifactStore.CreateNewDomain(destination, false, CancellationToken.None,
            beforeDestinationInspection: () =>
            {
                if (OperatingSystem.IsWindows()) Assert.Throws<IOException>(() => Directory.Move(parent, detached));
                else Directory.Move(parent, detached);
            }));
        if (OperatingSystem.IsWindows()) Assert.Null(error);
        else Assert.IsType<IOException>(error);
        var retainedParent = OperatingSystem.IsWindows() ? parent : detached;
        Assert.Equal("preserve parent bytes", File.ReadAllText(Path.Join(retainedParent, "sentinel")));
        Assert.Single(Directory.EnumerateFileSystemEntries(retainedParent));
        Assert.False(Directory.Exists(destination));
        Assert.False(Directory.Exists(Path.Join(detached, "fresh")));
    }

    /// <summary>Partial initialization remains visible and directs callers to a separate fresh destination.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewDomain_InterruptedCommandReportsPartialStateAndFreshPathRecovery(bool cancel)
    {
        using var temp = TestDirectory.Create();
        var destination = TestPathUtils.PathUnder(temp.Path, "partial");
        using var cancellation = new CancellationTokenSource();
        using var console = new FakeInMemoryConsole();
        var command = new CoverageStorageNewDomainCommand(() =>
        {
            if (cancel) cancellation.Cancel();
            else throw new IOException("injected interruption");
        })
        { Output = destination, Apply = true, Json = true };
        var error = await Assert.ThrowsAsync<CliFx.CommandException>(async () => await command.ExecuteAsync(console, cancellation.Token));
        Assert.Equal(cancel ? 130 : 1, error.ExitCode);
        using var result = JsonDocument.Parse(console.ReadOutputString());
        Assert.Equal("partial", result.RootElement.GetProperty("sideEffects").GetString());
        Assert.Contains("different fresh --output", result.RootElement.GetProperty("recoveryAction").GetString(), StringComparison.Ordinal);
        Assert.Contains("never reinitialize", result.RootElement.GetProperty("reconcileAction").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(destination, console.ReadOutputString(), StringComparison.Ordinal);
        var marker = File.ReadAllBytes(Path.Join(destination, "store.json"));
        Assert.False(File.Exists(Path.Join(destination, "registry.json")));
        Assert.Throws<IOException>(() => CoverageArtifactStore.CreateNewDomain(destination, true, CancellationToken.None));
        Assert.Equal(marker, File.ReadAllBytes(Path.Join(destination, "store.json")));
    }

    /// <summary>The creation observation fires at native publication even if later work fails, never on a competing-name refusal.</summary>
    [Fact]
    public void NativeCreate_ReportsPublicationBeforeLaterFailureAndNeverForExistingEntry()
    {
        using var temp = TestDirectory.Create();
        using var parent = CoverageRunOutputLease.AcquireArtifactDirectory(temp.Path, false, false)!;
        var observations = 0;
        Assert.Throws<IOException>(() => parent.CreateArtifactDirectory("partial", onCreated: () =>
        {
            observations++;
            Assert.True(Directory.Exists(Path.Join(temp.Path, "partial")));
            throw new IOException("after native publication");
        }));
        Assert.Equal(1, observations);
        Assert.True(Directory.Exists(Path.Join(temp.Path, "partial")));
        Assert.Throws<IOException>(() => parent.CreateArtifactDirectory("partial", onCreated: () => observations++));
        Assert.Equal(1, observations);
    }

    /// <summary>Only the documented competing-name error is tolerated in the idempotent walker.</summary>
    [Theory]
    [InlineData(183, true)]
    [InlineData(80, false)]
    [InlineData(5, false)]
    public void WindowsComponentCreation_OnlyToleratesAlreadyExists(int nativeError, bool tolerated)
    {
        var calls = 0;
        var error = Record.Exception(() => CoverageRunOutputLease.CreateMissingWindowsDirectory("component", path =>
        {
            Assert.Equal("component", path);
            calls++;
            throw new IOException("native creation failed", new Win32Exception(nativeError));
        }));
        Assert.Equal(1, calls);
        if (tolerated) Assert.Null(error);
        else Assert.Equal(nativeError, Assert.IsType<Win32Exception>(Assert.IsType<IOException>(error).InnerException).NativeErrorCode);
        CoverageRunOutputLease.CreateMissingWindowsDirectory("component", _ => calls++);
        Assert.Equal(2, calls);
        Assert.Throws<IOException>(() => CoverageRunOutputLease.CreateMissingWindowsDirectory("component", _ => throw new IOException("unclassified")));
    }

    /// <summary>A real competing private creator can be admitted only after retained-handle validation.</summary>
    [CoverageWindowsFact]
    [SupportedOSPlatform("windows")]
    public void WindowsComponentRace_ReopensPrivateCompetitorAndPreservesItsBytes()
    {
        using var temp = TestDirectory.Create();
        using var parent = CoverageRunOutputLease.AcquireArtifactDirectory(temp.Path, false, false)!;
        var destination = Path.Join(temp.Path, "competitor");
        var calls = 0;
        using var opened = CoverageRunOutputLease.AcquireArtifactDirectory(destination, true, true, beforeWindowsCreate: path =>
        {
            Assert.Equal(destination, path);
            calls++;
            using var competitor = parent.CreateArtifactDirectory("competitor");
            using var sentinel = competitor.OpenArtifactFile("sentinel", true);
            sentinel.Write("preserved competitor"u8);
        })!;
        Assert.Equal(1, calls);
        opened.VerifyArtifactBinding();
        Assert.Equal("preserved competitor", File.ReadAllText(Path.Join(destination, "sentinel")));
        Assert.Single(Directory.EnumerateFileSystemEntries(destination));
    }

    /// <summary>Reopening a competing name never repairs broad ACLs, accepts files, or follows links.</summary>
    [CoverageWindowsFact]
    [SupportedOSPlatform("windows")]
    public void WindowsComponentRace_RejectsUnsafeCompetitorsWithoutRepair()
    {
        using var temp = TestDirectory.Create();
        var file = Path.Join(temp.Path, "file");
        Assert.Throws<IOException>(() => CoverageRunOutputLease.AcquireArtifactDirectory(file, true, true,
            beforeWindowsCreate: path => File.WriteAllText(path, "foreign file")));
        Assert.Equal("foreign file", File.ReadAllText(file));

        var broad = Path.Join(temp.Path, "broad");
        byte[]? originalAcl = null;
        Assert.Throws<IOException>(() => CoverageRunOutputLease.AcquireArtifactDirectory(broad, true, true, beforeWindowsCreate: path =>
        {
            var directory = Directory.CreateDirectory(path);
            var security = directory.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.ReadAndExecute, AccessControlType.Allow));
            directory.SetAccessControl(security);
            originalAcl = directory.GetAccessControl().GetSecurityDescriptorBinaryForm();
            File.WriteAllText(Path.Join(path, "sentinel"), "foreign directory");
        }));
        Assert.Equal(originalAcl, new DirectoryInfo(broad).GetAccessControl().GetSecurityDescriptorBinaryForm());
        Assert.Equal("foreign directory", File.ReadAllText(Path.Join(broad, "sentinel")));

        var target = temp.CreateDirectory("target");
        var link = Path.Join(temp.Path, "link");
        Assert.Throws<IOException>(() => CoverageRunOutputLease.AcquireArtifactDirectory(link, true, true,
            beforeWindowsCreate: path => TestFileSystem.CreateDirectoryLinkOrSkip(path, target)));
        Assert.True(Directory.Exists(target));
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }

    /// <summary>Normalizes a trailing separator without creating anything during preview.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewDomain_TrailingSeparatorNamesTheSameFreshDestination(bool apply)
    {
        using var temp = TestDirectory.Create();
        var destination = TestPathUtils.PathUnder(temp.Path, "fresh");
        var normalized = CoverageArtifactStore.CreateNewDomain(destination + Path.DirectorySeparatorChar, apply, CancellationToken.None);
        Assert.Equal(CoverageRunOutputLease.NormalizePlatformPath(destination), normalized);
        Assert.Equal(apply, Directory.Exists(destination));
        if (apply)
        {
            using var store = CoverageArtifactStore.OpenReadOnly(destination);
            Assert.Equal(0, store.GetStatus().RegisteredRuns);
        }
    }

    /// <summary>Reports a pre-publication refusal truthfully and preserves competing bytes.</summary>
    [Theory]
    [InlineData("directory")]
    [InlineData("file")]
    [InlineData("missing-parent")]
    public async Task NewDomain_PreCreationRefusalHasNoSideEffectsOrPartialCommitHint(string state)
    {
        using var temp = TestDirectory.Create();
        var destination = state switch
        {
            "directory" => temp.CreateDirectory("occupied"),
            "file" => temp.WriteFile("occupied", "preserve competing bytes"),
            _ => TestPathUtils.PathUnder(temp.Path, "missing", "fresh")
        };
        using var console = new FakeInMemoryConsole();
        await Assert.ThrowsAsync<CliFx.CommandException>(async () =>
            await new CoverageStorageNewDomainCommand { Json = true, Apply = true, Output = destination }.ExecuteAsync(console, CancellationToken.None));
        using var result = JsonDocument.Parse(console.ReadOutputString());
        Assert.Equal("none", result.RootElement.GetProperty("sideEffects").GetString());
        Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("reconcileAction").ValueKind);
        if (state == "file") Assert.Equal("preserve competing bytes", File.ReadAllText(destination));
        else if (state == "directory") Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
        else Assert.False(Directory.Exists(destination));
    }

    /// <summary>Text mode distinguishes preview from creation and provides safe selection guidance.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewDomain_TextResultExplainsCreationAndNextAction(bool apply)
    {
        using var temp = TestDirectory.Create();
        var destination = TestPathUtils.PathUnder(temp.Path, "fresh");
        using var console = new FakeInMemoryConsole();
        await new CoverageStorageNewDomainCommand { Apply = apply, Output = destination }.ExecuteAsync(console, CancellationToken.None);
        var output = console.ReadOutputString();
        Assert.Contains($"created: {apply}", output, StringComparison.Ordinal);
        Assert.Contains("history transferred: False; configuration changed: False", output, StringComparison.Ordinal);
        Assert.Contains("APPSURFACE_ARTIFACT_HOME", output, StringComparison.Ordinal);
        Assert.DoesNotContain(destination, output, StringComparison.Ordinal);
        Assert.Equal(apply, Directory.Exists(destination));
    }

    /// <summary>Keeps preview and apply isolated from the configured old store and environment selection.</summary>
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

    /// <summary>Preserves an existing directory instead of adopting or reinitializing it.</summary>
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

    /// <summary>Refuses an occupied regular-file destination without changing its bytes.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewDomain_RefusesExistingRegularFileDuringPreviewAndApply(bool apply)
    {
        using var temp = TestDirectory.Create();
        var destination = temp.WriteFile("existing-file", "unowned file bytes");
        Assert.Throws<IOException>(() => CoverageArtifactStore.CreateNewDomain(destination, apply, CancellationToken.None));
        Assert.Equal("unowned file bytes", File.ReadAllText(destination));
        Assert.False(Directory.Exists(destination));
    }

    /// <summary>Rejects unsafe or missing parents without creating a new domain.</summary>
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

    /// <summary>Rejects missing or relative destinations before any filesystem mutation.</summary>
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

    /// <summary>Preserves filesystem state when cancellation or a broad root prevents creation.</summary>
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

    /// <summary>Preserves interrupted initialization and refuses to adopt its partial metadata on retry.</summary>
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

    /// <summary>Creates a separate empty domain without bypassing the old domain's active lease.</summary>
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

    /// <summary>Exercises native no-replace publication against a private competing creator and preserves its sentinel.</summary>
    [Fact]
    public void NativeCreate_RefusesACompetingCreatorBetweenPreflightAndPublication()
    {
        using var temp = TestDirectory.Create();
        using var parent = CoverageRunOutputLease.AcquireArtifactDirectory(temp.Path, false, false)!;
        var destination = TestPathUtils.PathUnder(temp.Path, "competing-domain");
        var sentinel = TestPathUtils.PathUnder(destination, "sentinel");
        Assert.Throws<IOException>(() => parent.CreateArtifactDirectory("competing-domain", () =>
        {
            // The competing directory must itself satisfy private admission. Otherwise
            // a later ACL refusal could hide an idempotent-create ownership bug.
            using var competitor = parent.CreateArtifactDirectory("competing-domain");
            using var file = competitor.OpenArtifactFile("sentinel", createNew: true);
            file.Write("belongs to the competing creator"u8);
            file.Flush(true);
        }));
        Assert.Equal("belongs to the competing creator", File.ReadAllText(sentinel));
        Assert.Single(Directory.EnumerateFileSystemEntries(destination));
        Assert.False(File.Exists(TestPathUtils.PathUnder(destination, "store.json")));
    }

    /// <summary>Simulates the native call boundary to verify pinned security attributes and strict existing-name refusal.</summary>
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
