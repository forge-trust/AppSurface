using System.Collections;
using System.Diagnostics;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

[Collection(EvidenceReuseSyntheticLocalContextCollection.Name)]
public sealed class EvidenceIdentityCoverageTests
{
    [Fact]
    public async Task FixtureOwnsItsCreatedDirectoryDespiteInheritedGitOverrides()
    {
        using var outside = await TestRepository.CreateAsync();
        await outside.WriteAsync("sentinel.txt", "caller-owned bytes");
        await outside.CommitAsync();
        var outsideIndex = await File.ReadAllBytesAsync(TestPathUtils.PathUnder(outside.Root, ".git", "index"));
        var overrides = new Dictionary<string, string>
        {
            ["GIT_DIR"] = TestPathUtils.PathUnder(outside.Root, ".git"),
            ["GIT_WORK_TREE"] = outside.Root,
            ["GIT_COMMON_DIR"] = TestPathUtils.PathUnder(outside.Root, ".git"),
            ["GIT_INDEX_FILE"] = TestPathUtils.PathUnder(outside.Root, "inherited-index"),
            ["GIT_CONFIG_COUNT"] = "1",
            ["GIT_CONFIG_KEY_0"] = "core.worktree",
            ["GIT_CONFIG_VALUE_0"] = outside.Root
        };
        var original = overrides.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        string ownedRoot;
        try
        {
            foreach (var (name, value) in overrides) Environment.SetEnvironmentVariable(name, value);
            using (var created = await TestRepository.CreateAsync())
            {
                ownedRoot = created.Root;
                Assert.NotEqual(outside.Root, ownedRoot);
                await created.WriteAsync("owned.txt", "fixture bytes");
                await created.CommitAsync();
                Assert.True(Directory.Exists(TestPathUtils.PathUnder(ownedRoot, ".git")));
            }
        }
        finally
        {
            foreach (var (name, value) in original) Environment.SetEnvironmentVariable(name, value);
        }

        Assert.False(Directory.Exists(ownedRoot));
        Assert.Equal("caller-owned bytes", await File.ReadAllTextAsync(TestPathUtils.PathUnder(outside.Root, "sentinel.txt")));
        Assert.Equal(outsideIndex, await File.ReadAllBytesAsync(TestPathUtils.PathUnder(outside.Root, ".git", "index")));
        Assert.False(File.Exists(TestPathUtils.PathUnder(outside.Root, "inherited-index")));
        Assert.False(File.Exists(TestPathUtils.PathUnder(outside.Root, "owned.txt")));
    }

    [Fact]
    public async Task InvalidMaterialDoesNotProduceReusableIdentityOrLeakMaterial()
    {
        using var repository = await TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "stable input");
        await repository.CommitAsync();
        var producer = new EvidenceProducerDeclaration("producer", "check", "1", [], [], [], 10)
        { Inputs = new EvidenceProducerInputs(["src/**"], []) };
        var plan = new EvidencePlan("1.1", "policy", new string('a', 64), new string('b', 64),
            new EvidenceProfile("profile", EvidenceProfileScope.Targeted, [], [producer], []), [], [], new string('c', 64));
        const string secret = "identity-test-secret-marker";
        var result = await EvidenceProducerIdentityBuilder.BuildAsync(new EvidenceProducerIdentityRequest(
            repository.Root, plan, producer, "implementation", new Dictionary<string, string>(), new Dictionary<string, string>(),
            new Dictionary<string, string> { [string.Empty] = secret }, null), CancellationToken.None);

        Assert.False(result.IsEligible);
        Assert.Null(result.Identity);
        Assert.DoesNotContain(secret, result.Diagnostic ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolResourceAndMaterialChangesEachInvalidateTheContentIdentity()
    {
        using var repository = await TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "stable input");
        await repository.CommitAsync();
        var producer = new EvidenceProducerDeclaration("producer", "check", "1", ["database"], [], [], 10)
        { Inputs = new EvidenceProducerInputs(["src/**"], []) };
        var resource = new EvidenceResourceDeclaration("database", "completion", 10, []);
        var plan = new EvidencePlan("1.1", "policy", new string('a', 64), new string('b', 64),
            new EvidenceProfile("profile", EvidenceProfileScope.Targeted, [resource], [producer], []), [], [], new string('c', 64));

        async Task<string> BuildKey(string tool, string resourceIdentity, string material)
        {
            var built = await EvidenceProducerIdentityBuilder.BuildAsync(new EvidenceProducerIdentityRequest(
                repository.Root, plan, producer, "implementation-v1",
                new Dictionary<string, string> { ["database"] = resourceIdentity },
                new Dictionary<string, string> { ["compiler"] = tool },
                new Dictionary<string, string> { ["mode"] = material }, null), CancellationToken.None);
            Assert.True(built.IsEligible, built.Diagnostic);
            return built.Identity!.Key;
        }

        var baseline = await BuildKey("compiler-v1", "database-v1", "mode-a");
        Assert.NotEqual(baseline, await BuildKey("compiler-v2", "database-v1", "mode-a"));
        Assert.NotEqual(baseline, await BuildKey("compiler-v1", "database-v2", "mode-a"));
        Assert.NotEqual(baseline, await BuildKey("compiler-v1", "database-v1", "mode-b"));
        await repository.WriteAsync("src/input.txt", "changed input bytes");
        Assert.NotEqual(baseline, await BuildKey("compiler-v1", "database-v1", "mode-a"));
    }

    [Fact]
    public async Task PatchGateRequiresTwoValidMatchingDigestsAndNormalizesHexCase()
    {
        using var repository = await TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "stable input");
        await repository.CommitAsync();
        var producer = new EvidenceProducerDeclaration("producer", "coverage", "1", [], [], [], 10,
            new EvidenceCoverageGateRequirements(95, 85, MinPatchLinePercent: 95))
        { Inputs = new EvidenceProducerInputs(["src/**"], []) };

        async Task<EvidenceProducerIdentityResult> Build(string? planned, string? captured)
            => await BuildIdentityAsync(repository, producer, [], new Dictionary<string, string>(), planned, captured);

        var missingCaptured = await Build(new string('a', 64), null);
        var malformedPlanned = await Build("not-a-digest", new string('a', 64));
        var malformedCaptured = await Build(new string('a', 64), new string('g', 64));
        var mismatch = await Build(new string('a', 64), new string('b', 64));
        var lower = await Build(new string('a', 64), new string('a', 64));
        var upper = await Build(new string('A', 64), new string('a', 64));

        Assert.Equal("reuse.identity.patch_unavailable", missingCaptured.Reason);
        Assert.Equal("reuse.identity.patch_unavailable", malformedPlanned.Reason);
        Assert.Equal("reuse.identity.patch_unavailable", malformedCaptured.Reason);
        Assert.Equal("reuse.identity.patch_mismatch", mismatch.Reason);
        Assert.True(lower.IsEligible, lower.Diagnostic);
        Assert.True(upper.IsEligible, upper.Diagnostic);
        Assert.Equal(lower.Identity!.Key, upper.Identity!.Key);
    }

    [Fact]
    public async Task TransitiveResourceClosureRequiresEveryDeclarationAndIdentity()
    {
        using var repository = await TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "stable input");
        await repository.CommitAsync();
        var producer = new EvidenceProducerDeclaration("producer", "check", "1", ["api"], [], [], 10)
        { Inputs = new EvidenceProducerInputs(["src/**"], []) };
        var api = new EvidenceResourceDeclaration("api", "completion", 10, ["database"]);
        var database = new EvidenceResourceDeclaration("database", "completion", 10, []);
        var resources = new[] { api, database };

        var complete = await BuildIdentityAsync(repository, producer, resources,
            new Dictionary<string, string> { ["api"] = "api-v1", ["database"] = "db-v1" });
        var changedDependency = await BuildIdentityAsync(repository, producer, resources,
            new Dictionary<string, string> { ["api"] = "api-v1", ["database"] = "db-v2" });
        var missingTransitiveIdentity = await BuildIdentityAsync(repository, producer, resources,
            new Dictionary<string, string> { ["api"] = "api-v1" });
        var missingTransitiveDeclaration = await BuildIdentityAsync(repository, producer, [api],
            new Dictionary<string, string> { ["api"] = "api-v1" });
        var missingDirectDeclaration = await BuildIdentityAsync(repository, producer with
        {
            RequiredResources = ["missing"],
        }, resources, new Dictionary<string, string>());

        Assert.True(complete.IsEligible, complete.Diagnostic);
        Assert.True(changedDependency.IsEligible, changedDependency.Diagnostic);
        Assert.NotEqual(complete.Identity!.Key, changedDependency.Identity!.Key);
        Assert.Equal("reuse.identity.resource_unavailable", missingTransitiveIdentity.Reason);
        Assert.Null(missingTransitiveIdentity.Identity);
        Assert.Equal("reuse.identity.resource_unavailable", missingTransitiveDeclaration.Reason);
        Assert.Null(missingTransitiveDeclaration.Identity);
        Assert.Equal("reuse.identity.resource_unavailable", missingDirectDeclaration.Reason);
        Assert.Null(missingDirectDeclaration.Identity);
    }

    [Fact]
    public async Task SharedTransitiveDependencyChangesInvalidateTheProducerResourceIdentity()
    {
        using var repository = await TestRepository.CreateAsync();
        await repository.WriteAsync("input.txt", "stable input");
        var producer = new EvidenceProducerDeclaration("producer", "check", "1", ["left", "right"], [], [], 10)
        { Inputs = new(["input.txt"], []) };
        EvidenceResourceDeclaration[] resources =
        [new("left", "completion", 10, ["shared"]), new("right", "completion", 10, ["shared"]),
            new("shared", "completion", 10, [])];
        var identities = new Dictionary<string, string> { ["left"] = "left-v1", ["right"] = "right-v1", ["shared"] = "shared-v1" };

        var original = await BuildIdentityAsync(repository, producer, resources, identities);
        identities["shared"] = "shared-v2";
        var changed = await BuildIdentityAsync(repository, producer, resources, identities);

        Assert.True(original.IsEligible, original.Diagnostic);
        Assert.True(changed.IsEligible, changed.Diagnostic);
        Assert.NotEqual(original.Identity!.Key, changed.Identity!.Key);
        Assert.NotEqual(original.Identity.ComponentHashes["resources"], changed.Identity.ComponentHashes["resources"]);
    }

    [Fact]
    public async Task IncludeAndExcludePatternsSelectOnlyTheDeclaredPortableInventory()
    {
        using var repository = await TestRepository.CreateAsync();
        await repository.WriteAsync("src/a/keep-one.txt", "one");
        await repository.WriteAsync("src/a/omit-one.txt", "excluded");
        await repository.WriteAsync("src/b/keep-two.txt", "two");
        await repository.WriteAsync("docs/keep.txt", "docs");
        await repository.CommitAsync();
        var producer = new EvidenceProducerDeclaration("producer", "check", "1", [], [], [], 10)
        { Inputs = new EvidenceProducerInputs(["src/**/keep*", "docs/**"], ["src/**/omit*"]) };

        var result = await BuildIdentityAsync(repository, producer, [], new Dictionary<string, string>());

        Assert.True(result.IsEligible, result.Diagnostic);
        Assert.Equal(["docs/keep.txt", "src/a/keep-one.txt", "src/b/keep-two.txt"],
            result.Identity!.Files.Select(static file => file.RelativePath));
    }

    [Fact]
    public async Task InvalidExcludeAndIncompleteRepositoryInventoryRefuseIdentity()
    {
        using var repository = await TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "stable input");
        await repository.CommitAsync();
        var producer = new EvidenceProducerDeclaration("producer", "check", "1", [], [], [], 10)
        { Inputs = new EvidenceProducerInputs(["src/**"], ["src/**/bad**"]) };
        var invalidExclude = await BuildIdentityAsync(repository, producer, [], new Dictionary<string, string>());
        var validPatternProducer = producer with { Inputs = new EvidenceProducerInputs(["src/**"], []) };
        var nonRoot = await BuildIdentityAsync(repository, validPatternProducer, [], new Dictionary<string, string>(), repositoryRoot:
            TestPathUtils.PathUnder(repository.Root, "src"));

        Assert.Equal("reuse.identity.unavailable", invalidExclude.Reason);
        Assert.Null(invalidExclude.Identity);
        Assert.Equal("reuse.identity.repository_mismatch", nonRoot.Reason);
        Assert.Null(nonRoot.Identity);

        File.Delete(TestPathUtils.PathUnder(repository.Root, "src/input.txt"));
        var deletedSelectedFile = await BuildIdentityAsync(repository, producer with
        {
            Inputs = new EvidenceProducerInputs(["src/**"], []),
        }, [], new Dictionary<string, string>());
        Assert.Equal("reuse.identity.empty", deletedSelectedFile.Reason);
        Assert.Null(deletedSelectedFile.Identity);
    }

    [Fact]
    public async Task MaterialUtf8AccountingHandlesPairedAndUnpairedSurrogatesWithoutLeakingValues()
    {
        using var repository = await TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "stable input");
        await repository.CommitAsync();
        var producer = new EvidenceProducerDeclaration("producer", "check", "1", [], [], [], 10)
        { Inputs = new EvidenceProducerInputs(["src/**"], []) };
        var validBoundary = await BuildIdentityAsync(repository, producer, [], new Dictionary<string, string>(),
            material: new Dictionary<string, string> { ["mode"] = string.Concat(Enumerable.Repeat("😀", 1024)) });
        var unpairedSurrogates = new string('\ud800', 1366);
        var unpairedOverLimit = await BuildIdentityAsync(repository, producer, [], new Dictionary<string, string>(),
            material: new Dictionary<string, string> { ["mode"] = unpairedSurrogates });

        Assert.True(validBoundary.IsEligible, validBoundary.Diagnostic);
        Assert.Null(unpairedOverLimit.Identity);
        Assert.Equal("reuse.identity.material_invalid", unpairedOverLimit.Reason);
        Assert.Contains("4098", unpairedOverLimit.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(unpairedSurrogates, unpairedOverLimit.Diagnostic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task NullMaterialKeyOrValueReturnsSafeIneligibility(bool nullKey, bool nullValue)
    {
        using var repository = await TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "stable input");
        await repository.CommitAsync();
        var producer = new EvidenceProducerDeclaration("producer", "check", "1", [], [], [], 10)
        { Inputs = new EvidenceProducerInputs(["src/**"], []) };
        const string secret = "must-not-appear-in-diagnostic";
        IReadOnlyDictionary<string, string> material = new MaterialEntries(new KeyValuePair<string, string>(
            nullKey ? null! : secret, nullValue ? null! : secret));

        var result = await BuildIdentityAsync(repository, producer, [], new Dictionary<string, string>(), material: material);

        Assert.Equal("reuse.identity.material_invalid", result.Reason);
        Assert.Null(result.Identity);
        Assert.DoesNotContain(secret, result.Diagnostic ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnderreportedMaterialCountCannotAdmitMoreThanTheEntryLimit()
    {
        using var repository = await TestRepository.CreateAsync();
        await repository.WriteAsync("input.txt", "stable");
        var producer = new EvidenceProducerDeclaration("producer", "check", "1", [], [], [], 10)
        { Inputs = new EvidenceProducerInputs(["input.txt"], []) };
        var entries = new MaterialEntries(Enumerable.Range(0, 65)
            .Select(index => new KeyValuePair<string, string>("private-key-" + index, "private-value")).ToArray())
        { ReportedCount = 0 };

        var result = await BuildIdentityAsync(repository, producer, [], new Dictionary<string, string>(), material: entries);

        Assert.Null(result.Identity);
        Assert.Equal("reuse.identity.material_invalid", result.Reason);
        Assert.Contains("65 observed", result.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("private-", result.Diagnostic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("x\ud800y")]
    [InlineData("\udc00")]
    [InlineData("\u0800")]
    public async Task BoundedReplacementEncodedMaterialRemainsUsable(string value)
    {
        using var repository = await TestRepository.CreateAsync();
        await repository.WriteAsync("input.txt", "stable");
        var producer = new EvidenceProducerDeclaration("producer", "check", "1", [], [], [], 10)
        { Inputs = new EvidenceProducerInputs(["input.txt"], []) };

        var result = await BuildIdentityAsync(repository, producer, [], new Dictionary<string, string>(),
            material: new Dictionary<string, string> { ["mode"] = value });

        Assert.True(result.IsEligible, result.Diagnostic);
        Assert.NotNull(result.Identity);
    }

    [Fact]
    public void FirstDifferingPathReportsModeLengthDigestAndOrderedAdditionsOrRemovals()
    {
        EvidenceProducerInputFile File(string path, int mode = 0x81A4, long length = 1, string digest = "a")
            => new(path, mode, length, digest);

        Assert.Equal("src/file", EvidenceProducerIdentityBuilder.FirstDifferingPath(
            [File("src/file")], [File("src/file", mode: 0x81ED)]));
        Assert.Equal("src/file", EvidenceProducerIdentityBuilder.FirstDifferingPath(
            [File("src/file")], [File("src/file", length: 2)]));
        Assert.Equal("src/file", EvidenceProducerIdentityBuilder.FirstDifferingPath(
            [File("src/file")], [File("src/file", digest: "b")]));
        Assert.Equal("a/added", EvidenceProducerIdentityBuilder.FirstDifferingPath(
            [File("b/kept")], [File("a/added"), File("b/kept")]));
        Assert.Equal("a/removed", EvidenceProducerIdentityBuilder.FirstDifferingPath(
            [File("a/removed"), File("b/kept")], [File("b/kept")]));
        Assert.Equal("z/removed", EvidenceProducerIdentityBuilder.FirstDifferingPath(
            [File("a/kept"), File("z/removed")], [File("a/kept")]));
    }

    private static Task<EvidenceProducerIdentityResult> BuildIdentityAsync(TestRepository repository,
        EvidenceProducerDeclaration producer, IReadOnlyList<EvidenceResourceDeclaration> resources,
        IReadOnlyDictionary<string, string> resourceIdentities, string? plannedPatch = null, string? capturedPatch = null,
        IReadOnlyDictionary<string, string>? material = null, string? repositoryRoot = null)
    {
        var plan = new EvidencePlan("1.1", "policy", new string('a', 64), new string('b', 64),
            new EvidenceProfile("profile", EvidenceProfileScope.Targeted, resources, [producer], []), [], [], new string('c', 64))
        { PatchSha256 = plannedPatch };
        return EvidenceProducerIdentityBuilder.BuildAsync(new EvidenceProducerIdentityRequest(repositoryRoot ?? repository.Root,
            plan, producer, "implementation", resourceIdentities, new Dictionary<string, string>(),
            material ?? new Dictionary<string, string>(), capturedPatch), CancellationToken.None);
    }

    private sealed class MaterialEntries : IReadOnlyDictionary<string, string>
    {
        private readonly KeyValuePair<string, string>[] _entries;
        internal MaterialEntries(params KeyValuePair<string, string>[] entries)
        {
            _entries = entries;
        }

        internal int? ReportedCount { get; init; }
        public int Count => ReportedCount ?? _entries.Length;
        public IEnumerable<string> Keys => _entries.Select(static entry => entry.Key);
        public IEnumerable<string> Values => _entries.Select(static entry => entry.Value);
        public string this[string key] => _entries.First(entry => EqualityComparer<string>.Default.Equals(entry.Key, key)).Value;
        public bool ContainsKey(string key) => _entries.Any(entry => EqualityComparer<string>.Default.Equals(entry.Key, key));
        public bool TryGetValue(string key, out string value)
        {
            var found = _entries.FirstOrDefault(entry => EqualityComparer<string>.Default.Equals(entry.Key, key));
            value = found.Value;
            return _entries.Any(entry => EqualityComparer<string>.Default.Equals(entry.Key, key));
        }
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => ((IEnumerable<KeyValuePair<string, string>>)_entries).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _entries.GetEnumerator();
    }

    internal sealed class TestRepository : IDisposable
    {
        private TestRepository(string root)
        {
            Root = root;
        }

        internal string Root { get; }
        internal static async Task<TestRepository> CreateAsync()
        {
            var created = Directory.CreateTempSubdirectory("evidence-identity-coverage-");
            try
            {
                // Resolve the owned directory independently of Git, including macOS /var -> /private/var.
                var repository = new TestRepository(PhysicalDirectory(created));
                await repository.GitAsync("init", "--quiet");
                await repository.GitAsync("config", "user.email", "test@example.invalid");
                await repository.GitAsync("config", "user.name", "Identity Coverage Test");
                await repository.GitAsync("config", "commit.gpgsign", "false");
                await repository.GitAsync("config", "core.hooksPath", TestPathUtils.PathUnder(repository.Root, "no-hooks"));
                var gitRoot = Path.GetFullPath((await repository.GitAsync("rev-parse", "--show-toplevel")).Trim());
                if (!string.Equals(repository.Root, gitRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidOperationException("Git did not initialize the fixture-owned directory.");
                return repository;
            }
            catch
            {
                // Never use a Git-derived path as deletion authority, even when initialization fails.
                created.Delete(recursive: true);
                throw;
            }
        }

        private static string PhysicalDirectory(DirectoryInfo directory)
        {
            if (directory.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                return PhysicalDirectory(new DirectoryInfo(target.FullName));
            return directory.Parent is { } parent
                ? TestPathUtils.PathUnder(PhysicalDirectory(parent), directory.Name)
                : directory.FullName;
        }
        internal async Task WriteAsync(string path, string contents)
        {
            var fullPath = TestPathUtils.PathUnder(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, contents);
        }
        internal async Task CommitAsync()
        {
            await GitAsync("add", "--all");
            await GitAsync("commit", "--quiet", "--allow-empty", "-m", "identity coverage");
        }
        private async Task<string> GitAsync(params string[] arguments)
        {
            using var process = new Process { StartInfo = new ProcessStartInfo("git") { WorkingDirectory = Root, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false } };
            foreach (var name in process.StartInfo.Environment.Keys.Where(name => name.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
                process.StartInfo.Environment.Remove(name);
            process.StartInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            process.StartInfo.Environment["GIT_CONFIG_GLOBAL"] = TestPathUtils.PathUnder(Root, "absent-global-config");
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await process.WaitForExitAsync(deadline.Token); }
            catch
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                await Task.WhenAll(output, error);
                throw;
            }
            await Task.WhenAll(output, error);
            if (process.ExitCode != 0) throw new InvalidOperationException(await error);
            return await output;
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
