using System.Security.Cryptography;
using System.Text;
using ForgeTrust.AppSurface.Auth.Keycloak;

namespace ForgeTrust.AppSurface.Auth.Keycloak.Tests;

// testcard protects: the published schema-1 byte contract and its content identity.
// fails_when: a serializer changes field order, spelling, omission, or policy ordering.
// why_new: this vector is authored independently of the production model builders.
// seam: public canonical read/write APIs; expected JSON and SHA-256 are fixed literals.
// no_claim: this does not prove semantic validation beyond the represented ThemeOnly shape.
public sealed class CanonicalGoldenTests
{
    private const string RecipeJson = "{\"schemaVersion\":1,\"sourceImage\":{\"index\":\"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"platformManifest\":\"sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"platform\":\"linux/amd64\",\"indexMaterialId\":\"index\",\"archiveMaterialId\":\"archive\",\"configDigest\":\"sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc\",\"diffIds\":[\"sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd\"]},\"runtimeFrontendOciIdentities\":[{\"id\":\"frontend\",\"platform\":\"linux/amd64\",\"reference\":\"frontend\",\"digest\":\"sha256:eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee\",\"archiveMaterialId\":\"frontend-archive\",\"platformManifestDigest\":\"sha256:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff\",\"configDigest\":\"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"indexMaterialId\":\"frontend-index\",\"diffIds\":[\"sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"]}],\"operations\":[],\"templateBaseline\":null,\"mode\":\"ThemeOnly\",\"keycloakVersion\":\"26.7.2\",\"entrypointPath\":\"/opt/keycloak/bin/kc.sh\",\"themeInputs\":[],\"capturedHostInputs\":[],\"sourceProfiles\":[\"keycloak-26.7.2\"],\"materialReferences\":[{\"id\":\"index\",\"exactHttpsUri\":\"https://example.test/material/index\",\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"maxBytes\":1024,\"capturedHostInput\":false},{\"id\":\"archive\",\"exactHttpsUri\":\"https://example.test/material/archive\",\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"maxBytes\":1024,\"capturedHostInput\":false},{\"id\":\"frontend-archive\",\"exactHttpsUri\":\"https://example.test/material/frontend-archive\",\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"maxBytes\":1024,\"capturedHostInput\":false},{\"id\":\"frontend-index\",\"exactHttpsUri\":\"https://example.test/material/frontend-index\",\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"maxBytes\":1024,\"capturedHostInput\":false}],\"resourceLimits\":{\"maxMaterialBytes\":1024,\"maxArchiveEntries\":1,\"maxArchiveExpandedBytes\":1024,\"maxOutputBytes\":1024,\"maxOperationSeconds\":1}}";
    private const string RecipeSha256 = "19e1621795473cd4d9dea970a43426f615fcc0699c017a1cac1ed3fdd7ee5ee0";
    private const string PolicyJson = "{\"version\":\"v1\",\"ownerApprovalReference\":\"owner\",\"permittedHttpsOrigins\":[\"https://example.test/\"],\"permittedPathMappings\":[\"https://example.test/material/\"],\"permittedCoordinateMappings\":[\"com.example\"],\"materialSha256ByIdentity\":{\"index\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"},\"signingFingerprints\":[],\"signingAlgorithms\":[],\"withdrawnKeys\":[],\"historicalExactReleaseBytes\":[]}";
    private const string PolicySha256 = "92fca0f87bf0c6ee4e3efdec60f5ad7c9a7450b58882edbf944db981f1058f2c";

    [Fact]
    public void Recipe_matches_independently_authored_golden_bytes_and_digest()
    {
        var expected = Encoding.UTF8.GetBytes(RecipeJson);
        var parsed = KeycloakCanonicalJson.ReadRecipe(expected);
        var actual = KeycloakCanonicalJson.Write(parsed);

        Assert.Equal(expected, actual);
        Assert.Equal(RecipeSha256, Convert.ToHexStringLower(SHA256.HashData(actual)));
    }

    [Fact]
    public void Policy_matches_independently_authored_golden_bytes_and_digest()
    {
        var expected = Encoding.UTF8.GetBytes(PolicyJson);
        var parsed = KeycloakCanonicalJson.ReadPolicy(expected);
        var actual = KeycloakCanonicalJson.Write(parsed);

        Assert.Equal(expected, actual);
        Assert.Equal(PolicySha256, Convert.ToHexStringLower(SHA256.HashData(actual)));
    }
}
