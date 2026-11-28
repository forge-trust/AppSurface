using System.Diagnostics;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

[Collection(EvidenceReuseSyntheticLocalContextCollection.Name)]
public sealed class EvidenceIdentityProcessCoverageTests
{
    [Theory]
    [InlineData("inventory-valid", null)]
    [InlineData("inventory-change", "reuse.identity.changed_during_read")]
    [InlineData("inventory-delete", "reuse.identity.unavailable")]
    [InlineData("inventory-collision", "reuse.identity.path_collision")]
    public async Task CompleteGitInventoryRejectsChangesAndNormalizationAliases(string mode, string? refusal)
    {
        using var temporary = TestDirectory.Create();
        using var fixture = new GitProcessFixture(temporary.Path, mode);
        var declaration = Request(temporary.Path).Producer with { Inputs = new(["**"], []) };
        var request = Request(temporary.Path, declaration);

        var result = await EvidenceProducerIdentityBuilder.BuildAsync(request, CancellationToken.None);

        if (refusal is null)
        {
            Assert.True(result.IsEligible, result.Diagnostic);
            Assert.Equal("input.txt", Assert.Single(result.Identity!.Files).RelativePath);
        }
        else
        {
            Assert.Null(result.Identity);
            Assert.Equal(refusal, result.Reason);
        }
        Assert.Equal(mode == "inventory-delete", !File.Exists(TestPathUtils.PathUnder(temporary.Path, "input.txt")));
        Assert.Equal(mode == "inventory-change", File.Exists(TestPathUtils.PathUnder(temporary.Path, "added.txt")));
        Assert.Equal("caller-owned sentinel", await File.ReadAllTextAsync(TestPathUtils.PathUnder(temporary.Path, "sentinel.txt")));
    }

    [Theory]
    [InlineData("common-flip", 1, "reuse.repository_changed")]
    [InlineData("common-flip", 2, "reuse.coordination_unavailable")]
    [InlineData("common-loss", 1, "reuse.identity.repository_unverified")]
    public async Task PreparationCannotReuseARepositoryNamespaceThatChangesBetweenIdentityQueries(
        string mode, int producerCount, string refusal)
    {
        using var temporary = TestDirectory.Create();
        using var fixture = new GitProcessFixture(temporary.Path, mode);
        var declarations = Enumerable.Range(0, producerCount).Select(index => Request(temporary.Path).Producer with
        { Id = "producer-" + index }).ToArray();
        var producers = declarations.Select(declaration => new NeverStartedProducer(declaration.Id)).ToArray();
        var registrations = producers.ToDictionary(producer => producer.Id,
            producer => new EvidenceReuseProducerRegistration(producer, "fixture-v1", new Dictionary<string, string>()), StringComparer.Ordinal);
        var plan = Request(temporary.Path).Plan with
        { Profile = new("local", EvidenceProfileScope.Targeted, [], declarations, []) };
        var store = TestPathUtils.PathUnder(temporary.Path, "store");
        var artifacts = TestPathUtils.PathUnder(temporary.Path, "artifacts");
        var options = new EvidenceProducerReuseOptions(true, temporary.Path, store, TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(1), false, false, null, TimeProvider.System)
        { GetEnvironmentVariable = static _ => null };

        await using (var session = await EvidenceProducerReuseSession.PrepareAsync(plan, registrations,
            new Dictionary<string, string>(), options, declaration => TestPathUtils.PathUnder(artifacts, declaration.Id),
            CancellationToken.None))
        {
            Assert.Equal(declarations.Select(static declaration => declaration.Id).Order(StringComparer.Ordinal),
                session.Decisions.Select(static decision => decision.ProducerId).Order(StringComparer.Ordinal));
            Assert.All(session.Decisions, decision => Assert.Equal(refusal, decision.Reason));
            if (mode == "common-loss")
            {
                Assert.False(session.IsUnavailable);
                Assert.Equal(EvidenceProducerReuseStatus.Ineligible, Assert.Single(session.Decisions).Status);
            }
            else
            {
                Assert.True(session.IsUnavailable);
                Assert.False(await session.BeginExecutionAsync(CancellationToken.None));
            }
            Assert.All(producers, producer => Assert.Equal(0, producer.Runs));
            Assert.False(Directory.Exists(artifacts));
            if (producerCount == 2) Assert.False(Directory.Exists(store));
        }
        Assert.Equal("caller-owned sentinel", await File.ReadAllTextAsync(TestPathUtils.PathUnder(temporary.Path, "sentinel.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrLinkedGitCommonDirectoryCannotGrantARepositoryNamespace(bool linked)
    {
        using var temporary = TestDirectory.Create();
        var common = TestPathUtils.PathUnder(temporary.Path, ".git");
        var target = TestPathUtils.PathUnder(temporary.Path, "common-storage");
        Directory.CreateDirectory(target);
        var sentinel = TestPathUtils.PathUnder(target, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "preserve common directory contents");
        using var fixture = new GitProcessFixture(temporary.Path, linked ? "namespace-linked" : "namespace-missing");
        try
        {
            if (linked)
            {
                try { Directory.CreateSymbolicLink(common, target); }
                catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
                {
                    throw new InvalidOperationException("The linked-common-directory safety fixture requires directory symlink capability on this native worker.", exception);
                }
            }

            var result = await EvidenceProducerIdentityBuilder.BuildAsync(Request(temporary.Path), CancellationToken.None);

            Assert.Null(result.Identity);
            Assert.Equal("reuse.identity.repository_unverified", result.Reason);
            Assert.Equal("preserve common directory contents", await File.ReadAllTextAsync(sentinel));
            Assert.False(Directory.Exists(TestPathUtils.PathUnder(target, "keys")));
        }
        finally
        {
            if (linked && new DirectoryInfo(common).LinkTarget is not null) Directory.Delete(common);
        }
    }

    [Theory]
    [InlineData("stdout-limit")]
    [InlineData("stdout-boundary")]
    [InlineData("stderr-limit")]
    [InlineData("invalid-utf8")]
    [InlineData("nonzero")]
    public async Task MalformedOrExcessiveGitOutputReturnsSafeIneligibilityAfterActualProcessExit(string mode)
    {
        using var temporary = TestDirectory.Create();
        using var fixture = new GitProcessFixture(temporary.Path, mode);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var result = await EvidenceProducerIdentityBuilder.BuildAsync(Request(temporary.Path), deadline.Token);

        Assert.Null(result.Identity);
        Assert.Equal("reuse.identity.unavailable", result.Reason);
        Assert.Contains("during repository", result.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("private-git-probe-error-marker", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallerCancellationKillsAndJoinsTheActualGitQuery()
    {
        using var temporary = TestDirectory.Create();
        using var fixture = new GitProcessFixture(temporary.Path, "cancel");
        using var caller = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Process? process = null;
        var building = EvidenceProducerIdentityBuilder.BuildAsync(Request(temporary.Path), caller.Token);
        try
        {
            var pidPath = TestPathUtils.PathUnder(temporary.Path, "git-probe.pid");
            while (!File.Exists(pidPath))
            {
                deadline.Token.ThrowIfCancellationRequested();
                // gstack-timing-ok: bounded readiness poll for the actual query, no elapsed-time assertion.
                await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token);
            }
            process = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(pidPath, deadline.Token)));
            Assert.False(building.IsCompleted);

            caller.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => building.WaitAsync(deadline.Token));
            Assert.True(process.HasExited);
        }
        finally
        {
            caller.Cancel();
            await File.WriteAllTextAsync(TestPathUtils.PathUnder(temporary.Path, "stop-git-probe"), "stop");
            try
            {
                if (process is not null)
                {
                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (TimeoutException)
                    {
                        if (!process.HasExited) process.Kill();
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    }
                }
                await building.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException) when (building.IsCompleted) { }
            finally { process?.Dispose(); }
        }
    }

    private static EvidenceProducerIdentityRequest Request(string root, EvidenceProducerDeclaration? declaration = null)
    {
        var producer = declaration ?? new EvidenceProducerDeclaration("producer", "check", "1", [], [], [], 10)
        { Inputs = new(["input.txt"], []) };
        return new(root, new("1.1", "policy", "", "", new("local", EvidenceProfileScope.Targeted, [], [producer], []), [], [], ""),
            producer, "implementation", new Dictionary<string, string>(), new Dictionary<string, string>(),
            new Dictionary<string, string>(), null);
    }

    private sealed class GitProcessFixture : IDisposable
    {
        private readonly Dictionary<string, string?> _original;

        internal GitProcessFixture(string root, string mode)
        {
            if (mode.StartsWith("inventory-", StringComparison.Ordinal) || mode.StartsWith("common-", StringComparison.Ordinal))
            {
                Directory.CreateDirectory(TestPathUtils.PathUnder(root, "common-a"));
                Directory.CreateDirectory(TestPathUtils.PathUnder(root, "common-b"));
                File.WriteAllText(TestPathUtils.PathUnder(root, "input.txt"), "stable input");
                File.WriteAllText(TestPathUtils.PathUnder(root, "sentinel.txt"), "caller-owned sentinel");
                string[] files = mode == "inventory-collision" ? ["input.txt", "caf\u00e9.txt", "cafe\u0301.txt"] : ["input.txt"];
                foreach (var path in files) File.WriteAllText(TestPathUtils.PathUnder(root, path), "stable input");
                File.WriteAllText(TestPathUtils.PathUnder(root, "git-probe-files.json"), JsonSerializer.Serialize(files));
            }
            var bin = TestPathUtils.PathUnder(root, "git-bin");
            CoverageLeaseProbeStager.Stage(bin, "git");
            var values = new Dictionary<string, string>
            {
                ["APPSURFACE_IDENTITY_GIT_PROBE_ROOT"] = root,
                ["APPSURFACE_IDENTITY_GIT_PROBE_MODE"] = mode,
                ["PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH")
            };
            _original = values.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable);
            try
            {
                foreach (var (name, value) in values) Environment.SetEnvironmentVariable(name, value);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            foreach (var (name, value) in _original) Environment.SetEnvironmentVariable(name, value);
        }
    }

    private sealed class NeverStartedProducer(string id) : IEvidenceProducer
    {
        public string Id { get; } = id;
        internal int Runs { get; private set; }
        public ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            Runs++;
            return ValueTask.FromResult(new EvidenceProducerResult(Id, EvidenceProducerOutcome.Passed, []));
        }
    }
}
