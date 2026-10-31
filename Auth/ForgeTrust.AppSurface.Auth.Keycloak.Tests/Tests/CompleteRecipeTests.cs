using ForgeTrust.AppSurface.Auth.Keycloak;

namespace ForgeTrust.AppSurface.Auth.Keycloak.Tests.Tests;

// Test card: deterministic byte identity for the three comprehensive recipe families.
// This is fixture and verifier coverage; it does not claim a full capture/materialize/inspect pass.
public sealed class CompleteRecipeTests
{
    [Fact]
    public void Maven_association_rejects_original_and_replacement_coordinate_swaps()
    {
        using var fixture = CompleteRecipeFixture.Create();
        Assert.Throws<InvalidDataException>(() => KeycloakSecurity.VerifyMavenIdentity(fixture.SourceArtifactPath,
            new ArtifactCoordinate("com.example", "security-adapter", "2.0.0", null, "jar")));
        Assert.Throws<InvalidDataException>(() => KeycloakSecurity.VerifyMavenIdentity(fixture.ReplacementArtifactPath,
            new ArtifactCoordinate("com.example", "security-adapter", "1.0.0", null, "jar")));
    }

    [Fact]
    public void Template_baseline_has_exact_archive_member_and_hash_identity()
    {
        using var fixture = CompleteRecipeFixture.Create();
        using var archive = new System.IO.Compression.ZipArchive(File.OpenRead(fixture.TemplateBaselinePath));
        var entry = Assert.Single(archive.Entries, x => x.FullName == fixture.TemplateEntry);
        using var input = entry.Open();
        Assert.Equal(fixture.TemplateEntrySha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)).ToLowerInvariant());
        Assert.Equal("/opt/keycloak/keycloak.jar", fixture.TemplateArchivePath);
        Assert.InRange(fixture.Lengths["template-baseline"], 1, 16 * 1024 * 1024);
    }
}
