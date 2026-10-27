using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Tests ACL policy over normalized facts; native Windows retrieval runs on Windows.</summary>
public sealed class CoverageArtifactWindowsAccessPolicyTests
{
    private const string User = "S-1-5-21-1000";

    [Theory]
    [InlineData(User)]
    [InlineData("S-1-5-18")]
    [InlineData("S-1-5-32-544")]
    public void CurrentOwnerAndNecessaryPrincipalsCanRetainAccess(string principal)
        => CoverageRunOutputLease.ValidateArtifactWindowsAccess(User, User,
            [new(principal, 0x001f01ffu, true)]);

    [Theory]
    [InlineData(false, 0x001f01ffu)]
    [InlineData(true, 0u)]
    public void ForeignDenyAndEmptyRulesDoNotGrantAccess(bool kind, uint rights)
        => CoverageRunOutputLease.ValidateArtifactWindowsAccess(User, User, [new("S-1-1-0", rights, kind)]);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("S-1-5-21-9999")]
    public void ForeignOrMissingOwnerFailsBeforeAcceptingRules(string? owner)
        => Assert.Throws<IOException>(() => CoverageRunOutputLease.ValidateArtifactWindowsAccess(owner, User, []));

    [Fact]
    public void AnyForeignAllowIsRejectedRegardlessOfPriorPermittedRules()
        => Assert.Throws<IOException>(() => CoverageRunOutputLease.ValidateArtifactWindowsAccess(User, User,
            [new(User, 0x001f01ffu, true),
             new("S-1-1-0", 0x00020089u, true)]));

    [Fact]
    public void RequiredObservationInputsAreValidated()
    {
        Assert.Throws<ArgumentException>(() => CoverageRunOutputLease.ValidateArtifactWindowsAccess(User, " ", []));
        Assert.Throws<ArgumentNullException>(() => CoverageRunOutputLease.ValidateArtifactWindowsAccess(User, User, null!));
    }
}
