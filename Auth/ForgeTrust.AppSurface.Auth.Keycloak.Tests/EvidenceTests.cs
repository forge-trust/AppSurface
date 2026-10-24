using System.Text.Json;
using ForgeTrust.AppSurface.Auth.Keycloak;

namespace ForgeTrust.AppSurface.Auth.Keycloak.Tests;

// protects: portable evidence publication preserves absent-destination semantics and scope.
// fails_when: cancelled or competing writes replace prior bytes; no_claim: synthetic observations are not image proof.
public sealed class EvidenceTests
{
    [Theory]
    [InlineData(KeycloakRecipeMode.Security, "IMAGE VERIFIED; release gates pending")]
    [InlineData(KeycloakRecipeMode.ThemeOnly, "THEME-ONLY VERIFIED; security customization not evaluated")]
    public async Task Writes_portable_scope_once_without_local_paths(KeycloakRecipeMode mode, string expected)
    {
        using var root = new Root(); var evidence = Evidence(mode); var path = Path.Combine(root.Path, "evidence.json");
        Assert.Equal(path, await evidence.WriteAsync(path));
        using var json = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.Equal(evidence.Schema, json.RootElement.GetProperty("schema").GetString());
        Assert.Equal(expected, json.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("plan", json.RootElement.GetProperty("planDigest").GetString());
        Assert.DoesNotContain(root.Path, File.ReadAllText(path), StringComparison.Ordinal);
        var original = File.ReadAllBytes(path);
        var error = await Assert.ThrowsAsync<KeycloakSecurityException>(() => evidence.WriteAsync(path));
        Assert.Equal(KeycloakSafePhase.Evidence, error.Phase); Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(root.Path, ".evidence-*"));
    }

    [Fact]
    public async Task Cancellation_and_linked_parent_publish_nothing()
    {
        using var root = new Root(); var evidence = Evidence(KeycloakRecipeMode.Security);
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); var path = Path.Combine(root.Path, "cancelled.json");
        var failure = await Assert.ThrowsAsync<KeycloakSecurityException>(() => evidence.WriteAsync(path, cancel.Token));
        Assert.Equal(KeycloakFailureCode.Cancelled, failure.Code); Assert.False(File.Exists(path));
        var link = Path.Combine(root.Path, "link"); Directory.CreateSymbolicLink(link, root.Path);
        await Assert.ThrowsAsync<KeycloakSecurityException>(() => evidence.WriteAsync(Path.Combine(link, "bad.json")));
        Assert.False(File.Exists(Path.Combine(root.Path, "bad.json"))); Assert.Empty(Directory.GetFiles(root.Path, ".evidence-*"));
    }

    [Fact]
    public async Task Write_rejects_invalid_parent_and_keeps_output_absent()
    {
        using var root = new Root(); var evidence = Evidence(KeycloakRecipeMode.Security);
        var parentFile = Path.Combine(root.Path, "parent"); File.WriteAllText(parentFile, "parent");
        var invalidParent = Path.Combine(parentFile, "evidence.json");
        await Assert.ThrowsAsync<KeycloakSecurityException>(() => evidence.WriteAsync(invalidParent));
        Assert.False(File.Exists(invalidParent));
    }
    private static VerifiedKeycloakImageEvidence Evidence(KeycloakRecipeMode mode) => new("plan", new("sha256:" + new string('a', 64), "sha256:" + new string('b', 64), "linux/amd64", ["sha256:" + new string('c', 64)]), mode, [new("file:opt/keycloak/example", "sha256:known", "sha256:known")]);
    private sealed class Root : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : System.IO.Path.GetTempPath(), "keycloak-evidence-test-" + Guid.NewGuid().ToString("N"));
        internal Root() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
