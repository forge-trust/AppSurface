using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Validation;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Verifies real fixture policy bytes and permissions in the existing native coverage shard.</summary>
public sealed class CoverageArtifactFixturePolicyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetentionPolicy_IsPrivateAndPreservesUnrelatedFiles(bool replaceBroadFixturePolicy)
    {
        using var temp = TestDirectory.Create();
        var policy = Path.Join(temp.Path, "policy.json");
        var sentinel = Path.Join(temp.Path, "caller.txt");
        await File.WriteAllTextAsync(sentinel, "preserve caller bytes");
        if (replaceBroadFixturePolicy)
        {
            await File.WriteAllTextAsync(policy, "old fixture policy");
            if (OperatingSystem.IsWindows())
            {
                var file = new FileInfo(policy);
                var broad = file.GetAccessControl();
                broad.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                    FileSystemRights.ReadData, AccessControlType.Allow));
                file.SetAccessControl(broad);
            }
            else File.SetUnixFileMode(policy, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        }

        await PackedStorageProducer.WritePrivateRetentionPolicyAsync(policy, CancellationToken.None);

        using var contents = JsonDocument.Parse(await File.ReadAllTextAsync(policy));
        Assert.Equal(1, contents.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(0, contents.RootElement.GetProperty("historyMaxBytes").GetInt64());
        Assert.Equal("preserve caller bytes", await File.ReadAllTextAsync(sentinel));
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var owner = identity.User!;
            var security = new FileInfo(policy).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            Assert.Equal(owner, security.GetOwner(typeof(SecurityIdentifier)));
            Assert.True(security.AreAccessRulesProtected);
            var rule = Assert.Single(security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>());
            Assert.Equal(owner, rule.IdentityReference);
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
            Assert.False(rule.IsInherited);
        }
        else Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(policy));
    }

    [Fact]
    public async Task CancelledPolicyWrite_PreservesExistingFixtureBytes()
    {
        using var temp = TestDirectory.Create();
        var policy = Path.Join(temp.Path, "policy.json");
        await File.WriteAllTextAsync(policy, "preserve fixture policy");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PackedStorageProducer.WritePrivateRetentionPolicyAsync(policy, cancellation.Token));

        Assert.Equal("preserve fixture policy", await File.ReadAllTextAsync(policy));
    }
}
