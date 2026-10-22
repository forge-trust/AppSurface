using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactWindowsFileCreationTests
{
    [CoverageWindowsFact]
    [SupportedOSPlatform("windows")]
    public void NewPrivateFileSetsCurrentUserOwnerAndRestrictedAclBeforeWriting()
    {
        using var temp = TestDirectory.Create();
        using var directory = CoverageRunOutputLease.AcquireArtifactDirectory(Path.Join(temp.Path, "private"), true, true)!;
        var path = Path.Join(directory.ArtifactDirectoryPath, "payload");
        using (var output = directory.OpenArtifactFile("payload", true))
        {
            var security = new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            var user = WindowsIdentity.GetCurrent().User!;
            Assert.Equal(user, security.GetOwner(typeof(SecurityIdentifier)));
            Assert.True(security.AreAccessRulesProtected);
            Assert.All(security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(), rule =>
                Assert.True(rule.AccessControlType == AccessControlType.Deny || rule.IdentityReference.Equals(user)
                    || rule.IdentityReference.Value == "S-1-5-18"));
            output.Write("exact"u8);
        }
        using var input = directory.OpenArtifactFile("payload", false);
        using var reader = new StreamReader(input);
        Assert.Equal("exact", reader.ReadToEnd());
        Assert.Throws<IOException>(() => directory.OpenArtifactFile("payload", true));
    }

    [CoverageWindowsFact]
    [SupportedOSPlatform("windows")]
    public void ExistingForeignAccessIsRejectedWithoutRepairOrOverwrite()
    {
        using var temp = TestDirectory.Create();
        using var directory = CoverageRunOutputLease.AcquireArtifactDirectory(Path.Join(temp.Path, "private"), true, true)!;
        var path = Path.Join(directory.ArtifactDirectoryPath, "payload");
        using (var stream = directory.OpenArtifactFile("payload", true)) stream.Write("retained"u8);
        var file = new FileInfo(path);
        var security = file.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.ReadData, AccessControlType.Allow));
        file.SetAccessControl(security);
        var before = file.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access).GetSecurityDescriptorBinaryForm();
        Assert.Throws<IOException>(() => directory.OpenArtifactFile("payload", false));
        Assert.Throws<IOException>(() => directory.OpenArtifactFile("payload", true));
        Assert.Equal(before, file.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access).GetSecurityDescriptorBinaryForm());
        Assert.Equal("retained", File.ReadAllText(path));
    }
}
