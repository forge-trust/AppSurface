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
    [Fact]
    public async Task Concurrent_evidence_publication_keeps_exactly_one_complete_winner()
    {
        using var root = new Root(); var evidence = Evidence(KeycloakRecipeMode.ThemeOnly);
        var path = Path.Combine(root.Path, "winner.json");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> Publish()
        {
            await ready.Task;
            try { await evidence.WriteAsync(path); return true; }
            catch (KeycloakSecurityException) { return false; }
        }
        var first = Task.Run(Publish); var second = Task.Run(Publish); ready.SetResult();
        Assert.Single(await Task.WhenAll(first, second), passed => passed);
        using var observed = JsonDocument.Parse(await File.ReadAllBytesAsync(path));
        Assert.Equal(evidence.Schema, observed.RootElement.GetProperty("schema").GetString());
        Assert.Equal(evidence.PlanDigest, observed.RootElement.GetProperty("planDigest").GetString());
        Assert.Empty(Directory.GetFiles(root.Path, ".evidence-*"));
    }

    [Fact]
    public async Task AtomicPublicationHasOneCompleteWinnerAcrossConcurrentInvocationsAndDestinations()
    {
        using var root = new Root();
        for (var round = 0; round < 32; round++)
        {
            var first = Path.Combine(root.Path, "first-" + round); var second = Path.Combine(root.Path, "second-" + round);
            var output = Path.Combine(root.Path, "winner-" + round);
            File.WriteAllText(first, "first-complete"); File.WriteAllText(second, "second-complete");
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<string?> Publish(string path)
            {
                await release.Task;
                try { AtomicFilePublication.Publish(path, output); return Path.GetFileName(path); }
                catch (IOException) { return null; }
            }
            var a = Task.Run(() => Publish(first)); var b = Task.Run(() => Publish(second)); release.SetResult();
            var winner = Assert.Single(await Task.WhenAll(a, b), value => value is not null);
            Assert.Equal(winner!.StartsWith("first-", StringComparison.Ordinal) ? "first-complete" : "second-complete", File.ReadAllText(output));
            Assert.Throws<IOException>(() => AtomicFilePublication.Publish(Path.Combine(root.Path, "missing"), output));
        }
        var node = Path.Combine(root.Path, "existing-directory"); Directory.CreateDirectory(node);
        var staged = Path.Combine(root.Path, "staged"); File.WriteAllText(staged, "complete");
        Assert.Throws<IOException>(() => AtomicFilePublication.Publish(staged, node));
        Assert.True(Directory.Exists(node)); Assert.Equal("complete", File.ReadAllText(staged));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBeforeAndAfterCommitHasAnExplicitLinearizationPoint(bool cancelAfterCommit)
    {
        using var root = new Root(); using var cancellation = new CancellationTokenSource();
        var path = Path.Combine(root.Path, "controlled-commit.json");
        var events = new List<KeycloakStageEvent>();
        using var subscription = KeycloakStageEvents.Subscribe(observed =>
        {
            if (observed.Phase != KeycloakStagePhase.Evidence) return;
            events.Add(observed);
            if (observed.Outcome == (cancelAfterCommit ? KeycloakStageOutcome.Succeeded : KeycloakStageOutcome.Started))
                cancellation.Cancel();
        });
        if (cancelAfterCommit)
        {
            Assert.Equal(path, await Evidence(KeycloakRecipeMode.ThemeOnly).WriteAsync(path, cancellation.Token));
            using var json = System.Text.Json.JsonDocument.Parse(await File.ReadAllBytesAsync(path));
            Assert.Equal("plan", json.RootElement.GetProperty("planDigest").GetString());
            Assert.Equal(KeycloakEffectState.Committed, events.Last().Effect);
            Assert.Equal(KeycloakStageOutcome.Succeeded, events.Last().Outcome);
        }
        else
        {
            var failure = await Assert.ThrowsAsync<KeycloakSecurityException>(() => Evidence(KeycloakRecipeMode.ThemeOnly).WriteAsync(path, cancellation.Token));
            Assert.Equal(KeycloakFailureCode.Cancelled, failure.Code);
            Assert.False(File.Exists(path));
            Assert.Equal(KeycloakStageOutcome.Rejected, events.Last().Outcome);
        }
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Empty(Directory.GetFiles(root.Path, ".evidence-*"));
        Assert.Equal(2, events.Count);
    }

    private static VerifiedKeycloakImageEvidence Evidence(KeycloakRecipeMode mode) => new("plan", new("sha256:" + new string('a', 64), "sha256:" + new string('b', 64), "linux/amd64", ["sha256:" + new string('c', 64)]), mode, [new("file:opt/keycloak/example", "sha256:known", "sha256:known")]);
    private sealed class Root : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : System.IO.Path.GetTempPath(), "keycloak-evidence-test-" + Guid.NewGuid().ToString("N"));
        internal Root()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose() => Directory.Delete(Path, true);
    }
}
