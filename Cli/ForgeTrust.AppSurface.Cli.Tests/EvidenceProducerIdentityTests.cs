using System.Diagnostics;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceProducerIdentityTests
{
    [Fact]
    public async Task BuildAsync_ShouldBeStableAndIgnoreCommitHistory()
    {
        using var repo = await TestRepository.CreateAsync();
        await repo.WriteAsync("src/keep.cs", "one");
        await repo.CommitAsync("first");
        var first = await BuildAsync(repo);
        await repo.CommitAsync("history only");
        var second = await BuildAsync(repo);

        Assert.True(first.IsEligible, first.Diagnostic);
        Assert.Equal(first.Identity!.Key, second.Identity!.Key);
        Assert.Equal(first.Identity.RepositoryNamespace, second.Identity.RepositoryNamespace);
    }

    [Fact]
    public async Task BuildAsync_ShouldPreserveWhitespaceInRepositoryPath()
    {
        // Win32 aliases a trailing-space component; internal whitespace remains significant on every platform.
        using var repo = await TestRepository.CreateAsync(OperatingSystem.IsWindows() ? " producer root path" : " producer root ");
        await repo.WriteAsync("src/file", "data");
        await repo.CommitAsync("base");

        var result = await BuildAsync(repo);

        Assert.True(result.IsEligible, result.Diagnostic);
        Assert.Equal("src/file", Assert.Single(result.Identity!.Files).RelativePath);
    }

    [Fact]
    public async Task BuildAsync_ShouldShareNamespaceAcrossLinkedWorktreesButNotClones()
    {
        using var repo = await TestRepository.CreateAsync();
        await repo.WriteAsync("src/keep.cs", "one");
        await repo.CommitAsync("base");
        var source = await BuildAsync(repo);
        var worktreePath = repo.Root + "-worktree";
        await repo.GitAsync("worktree", "add", "--detach", worktreePath, "HEAD");
        try
        {
            var worktree = await BuildAsync(new TestRepository(worktreePath));
            Assert.Equal(source.Identity!.RepositoryNamespace, worktree.Identity!.RepositoryNamespace);
        }
        finally
        {
            await repo.GitAsync("worktree", "remove", "--force", worktreePath);
        }

        var clonePath = repo.Root + "-clone";
        await repo.GitAsync("clone", "--quiet", "--local", repo.Root, clonePath);
        try
        {
            var clone = await BuildAsync(new TestRepository(clonePath));
            Assert.NotEqual(source.Identity!.RepositoryNamespace, clone.Identity!.RepositoryNamespace);
        }
        finally
        {
            new TestDirectory(clonePath).Dispose();
        }
    }

    [Fact]
    public async Task BuildAsync_ShouldHashDirtyTrackedAndNonignoredUntrackedBytes()
    {
        using var repo = await TestRepository.CreateAsync();
        await repo.WriteAsync("src/keep.cs", "one");
        await repo.CommitAsync("base");
        var baseline = await BuildAsync(repo);
        await repo.WriteAsync("src/keep.cs", "two");
        var dirty = await BuildAsync(repo);
        Assert.NotEqual(baseline.Identity!.Key, dirty.Identity!.Key);
        await repo.WriteAsync("src/new.cs", "new");
        var untracked = await BuildAsync(repo);
        Assert.NotEqual(dirty.Identity.Key, untracked.Identity!.Key);
        Assert.Contains(untracked.Identity.Files, file => file.RelativePath == "src/new.cs");
    }

    [Fact]
    public async Task BuildAsync_ShouldRepresentTrackedDeletionAsAnInventoryChange()
    {
        using var repo = await TestRepository.CreateAsync();
        await repo.WriteAsync("src/keep.cs", "kept");
        await repo.WriteAsync("src/remove.cs", "removed");
        await repo.CommitAsync("base");
        var before = await BuildAsync(repo);
        File.Delete(Path.Combine(repo.Root, "src/remove.cs"));

        var after = await BuildAsync(repo);

        Assert.True(after.IsEligible, after.Diagnostic);
        Assert.NotEqual(before.Identity!.Key, after.Identity!.Key);
        Assert.DoesNotContain(after.Identity.Files, file => file.RelativePath == "src/remove.cs");
    }

    [Fact]
    public async Task BuildAsync_ShouldPreserveWhitespaceAndApplySegmentPatterns()
    {
        using var repo = await TestRepository.CreateAsync();
        await repo.WriteAsync("src/ spaced .cs", "selected");
        await repo.WriteAsync("src/ignored.cs", "excluded");
        await repo.CommitAsync("base");
        var result = await BuildAsync(repo, ["src/**"], ["src/ignored*"]);

        Assert.True(result.IsEligible, result.Diagnostic);
        Assert.Equal("src/ spaced .cs", Assert.Single(result.Identity!.Files).RelativePath);
    }

    [Fact]
    public async Task BuildAsync_ShouldInvalidateForModeToolResourceAndMaterialChanges()
    {
        using var repo = await TestRepository.CreateAsync();
        await repo.WriteAsync("src/file", "same");
        await repo.CommitAsync("base");
        var first = await BuildAsync(repo);
        Assert.NotEqual(first.Identity!.Key, (await BuildAsync(repo, tool: "tool-v2")).Identity!.Key);
        Assert.NotEqual(first.Identity.Key, (await BuildAsync(repo, resource: "image-v2")).Identity!.Key);
        Assert.NotEqual(first.Identity.Key, (await BuildAsync(repo, material: "flag-b")).Identity!.Key);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path.Combine(repo.Root, "src/file"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Assert.NotEqual(first.Identity.Key, (await BuildAsync(repo)).Identity!.Key);
        }
    }

    [Fact]
    public async Task BuildAsync_ShouldInvalidateForNonExecutablePermissionOrReadOnlyModeChange()
    {
        using var repo = await TestRepository.CreateAsync();
        await repo.WriteAsync("src/file", "same");
        await repo.CommitAsync("base");
        var path = Path.Combine(repo.Root, "src/file");
        var first = await BuildAsync(repo);
        Assert.NotNull(first.Identity);

        if (OperatingSystem.IsWindows())
        {
            var originalAttributes = File.GetAttributes(path);
            try
            {
                File.SetAttributes(path, originalAttributes ^ FileAttributes.ReadOnly);
                var changed = await BuildAsync(repo);
                Assert.True(changed.IsEligible, changed.Diagnostic);
                Assert.NotEqual(first.Identity.Key, changed.Identity!.Key);
            }
            finally { File.SetAttributes(path, originalAttributes); }
        }
        else
        {
            var originalMode = File.GetUnixFileMode(path);
            try
            {
                File.SetUnixFileMode(path, originalMode ^ UnixFileMode.OtherRead);
                var changed = await BuildAsync(repo);
                Assert.True(changed.IsEligible, changed.Diagnostic);
                Assert.NotEqual(first.Identity.Key, changed.Identity!.Key);
            }
            finally { File.SetUnixFileMode(path, originalMode); }
        }
    }

    [Fact]
    public async Task BuildAsync_ShouldRequireMatchingExactPatchDigestForPatchGate()
    {
        using var repo = await TestRepository.CreateAsync();
        await repo.WriteAsync("src/file", "data");
        await repo.CommitAsync("base");
        var missing = await BuildAsync(repo, patchGate: true);
        Assert.Null(missing.Identity);
        Assert.Equal("reuse.identity.patch_unavailable", missing.Reason);
        var planMismatch = await BuildAsync(repo, patchGate: true, patch: new string('a', 64), planPatch: new string('b', 64));
        Assert.Null(planMismatch.Identity);
        Assert.Equal("reuse.identity.patch_mismatch", planMismatch.Reason);
        var valid = await BuildAsync(repo, patchGate: true, patch: new string('a', 64), planPatch: new string('a', 64));
        Assert.NotNull(valid.Identity);
    }

    [Fact]
    public async Task BuildAsync_ShouldRejectUnsafePatternsLinksAndEmptyInventory()
    {
        using var repo = await TestRepository.CreateAsync();
        await repo.WriteAsync("src/file", "data");
        await repo.CommitAsync("base");
        foreach (var pattern in new[] { "../src/**", "/src/**", "src//**", "src/**/bad**" })
        {
            var result = await BuildAsync(repo, [pattern]);
            Assert.Null(result.Identity);
            Assert.Equal("reuse.identity.unavailable", result.Reason);
        }
        var empty = await BuildAsync(repo, ["missing/**"]);
        Assert.Equal("reuse.identity.empty", empty.Reason);

        Directory.CreateSymbolicLink(Path.Combine(repo.Root, "linked"), Path.Combine(repo.Root, "src"));
        var linked = await BuildAsync(repo, ["linked"]);
        Assert.Null(linked.Identity);
    }

    [Fact]
    public async Task BuildAsync_ShouldRejectAnIncludedUntrackedNestedRepositoryButIgnoreUnselectedOne()
    {
        using var repo = await TestRepository.CreateAsync();
        await repo.WriteAsync("src/file.cs", "outer");
        await repo.CommitAsync("base");
        var nested = new TestRepository(Path.Combine(repo.Root, "nested"));
        Directory.CreateDirectory(nested.Root);
        await nested.GitAsync("init", "--quiet");
        await nested.GitAsync("config", "user.email", "nested@example.invalid");
        await nested.GitAsync("config", "user.name", "Nested Test");
        await nested.WriteAsync("inside.cs", "nested");
        await nested.CommitAsync("nested base");

        var selected = await BuildAsync(repo, ["nested/**/*.cs"]);
        Assert.Null(selected.Identity);
        Assert.Equal("reuse.identity.nested_repository", selected.Reason);

        var unrelated = await BuildAsync(repo, ["src/**"]);
        Assert.True(unrelated.IsEligible, unrelated.Diagnostic);
    }

    [Fact]
    public async Task BuildAsync_ShouldRejectAnExactIncludedGitlinkWhenItsCheckoutIsMissing()
    {
        using var repo = await TestRepository.CreateAsync();
        await repo.WriteAsync("src/file.cs", "outer");
        await repo.CommitAsync("base");
        var nested = new TestRepository(Path.Combine(repo.Root, "module"));
        Directory.CreateDirectory(nested.Root);
        await nested.GitAsync("init", "--quiet");
        await nested.GitAsync("config", "user.email", "nested@example.invalid");
        await nested.GitAsync("config", "user.name", "Nested Test");
        await nested.WriteAsync("inside.cs", "nested");
        await nested.CommitAsync("nested base");
        var nestedCommit = await nested.GitOutputAsync("rev-parse", "HEAD");
        await repo.GitAsync("update-index", "--add", "--cacheinfo", $"160000,{nestedCommit},module");
        Directory.Delete(nested.Root, recursive: true);

        var selected = await BuildAsync(repo, ["src/**", "module"]);

        Assert.Null(selected.Identity);
        Assert.Equal("reuse.identity.nested_repository", selected.Reason);
    }

    [Fact]
    public void IncludeMaySelectPathOrDescendant_ShouldMatchExactGitlinksAndLinkedDirectoryPatterns()
    {
        Assert.True(EvidenceProducerIdentityBuilder.IncludeMaySelectPathOrDescendant("module", ["module"], CancellationToken.None));
        Assert.True(EvidenceProducerIdentityBuilder.IncludeMaySelectPathOrDescendant("linked", ["linked/**/*.cs"], CancellationToken.None));
        Assert.False(EvidenceProducerIdentityBuilder.IncludeMaySelectPathOrDescendant("linked", ["src/**"], CancellationToken.None));
    }

    [Fact]
    public async Task BuildAsync_ShouldEnforceMaterialBoundsWithoutExposingValues()
    {
        using var repo = await TestRepository.CreateAsync();
        await repo.WriteAsync("src/file", "data");
        await repo.CommitAsync("base");
        var tooMany = Enumerable.Range(0, EvidenceProducerIdentityBuilder.MaximumMaterialKeys + 1)
            .ToDictionary(static index => $"private-key-{index}", static index => $"private-value-{index}");
        var result = await BuildAsync(repo, materialValues: tooMany);
        Assert.Null(result.Identity);
        Assert.Equal("reuse.identity.material_invalid", result.Reason);
        var diagnostic = Assert.IsType<string>(result.Diagnostic);
        Assert.Contains("65", diagnostic, StringComparison.Ordinal);
        Assert.Contains("64", diagnostic, StringComparison.Ordinal);
        Assert.Contains("Remove keys", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("private-key-", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("private-value-", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildAsync_ShouldAcceptMaterialAtTheConfiguredByteLimits()
    {
        using var repo = await TestRepository.CreateAsync();
        await repo.WriteAsync("src/file", "data");
        await repo.CommitAsync("base");
        var exactTotal = Enumerable.Range(0, EvidenceProducerIdentityBuilder.MaximumMaterialKeys)
            .ToDictionary(static index => $"k{index:D2}", static _ => new string('v', 4093));
        // Three-byte keys plus each value make the aggregate exactly 256 KiB; each value remains within 4 KiB.
        Assert.Equal(EvidenceProducerIdentityBuilder.MaximumMaterialTotalBytes,
            exactTotal.Sum(static pair => System.Text.Encoding.UTF8.GetByteCount(pair.Key) + System.Text.Encoding.UTF8.GetByteCount(pair.Value)));

        var accepted = await BuildAsync(repo, materialValues: exactTotal);
        Assert.NotNull(accepted.Identity);

        const string keyMarker = "private-key-marker";
        var overlongKey = keyMarker + new string('k', EvidenceProducerIdentityBuilder.MaximumMaterialKeyLength + 1 - keyMarker.Length);
        var tooLongKey = await BuildAsync(repo, materialValues: new Dictionary<string, string> { [overlongKey] = "private-value-marker" });
        Assert.Null(tooLongKey.Identity);
        Assert.Equal("reuse.identity.material_invalid", tooLongKey.Reason);
        var keyDiagnostic = Assert.IsType<string>(tooLongKey.Diagnostic);
        Assert.Contains("129", keyDiagnostic, StringComparison.Ordinal);
        Assert.Contains("128", keyDiagnostic, StringComparison.Ordinal);
        Assert.Contains("Shorten key names", keyDiagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(keyMarker, keyDiagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("private-value-marker", keyDiagnostic, StringComparison.Ordinal);

        var overlongUtf8Value = new string('é', 2049);
        var tooLargeValue = await BuildAsync(repo, materialValues: new Dictionary<string, string> { ["private-key-marker"] = overlongUtf8Value });
        Assert.Null(tooLargeValue.Identity);
        Assert.Equal("reuse.identity.material_invalid", tooLargeValue.Reason);
        var valueDiagnostic = Assert.IsType<string>(tooLargeValue.Diagnostic);
        Assert.Contains("4098", valueDiagnostic, StringComparison.Ordinal);
        Assert.Contains("4096", valueDiagnostic, StringComparison.Ordinal);
        Assert.Contains("UTF-8 bytes", valueDiagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("private-key-marker", valueDiagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(overlongUtf8Value, valueDiagnostic, StringComparison.Ordinal);

        var tooLargeTotal = await BuildAsync(repo, materialValues: Enumerable.Range(0, EvidenceProducerIdentityBuilder.MaximumMaterialKeys)
            .ToDictionary(static index => $"k{index:D2}", static index => new string('v', index == 63 ? 4094 : 4093)));
        Assert.Null(tooLargeTotal.Identity);
        Assert.Equal("reuse.identity.material_invalid", tooLargeTotal.Reason);
        var totalDiagnostic = Assert.IsType<string>(tooLargeTotal.Diagnostic);
        Assert.Contains("262145", totalDiagnostic, StringComparison.Ordinal);
        Assert.Contains("262144", totalDiagnostic, StringComparison.Ordinal);
        Assert.Contains("Reduce key and value sizes", totalDiagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("k00", totalDiagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('v', 32), totalDiagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildAsync_ShouldRejectMalformedUtf16AcrossIdentityInputsAndPreserveValidReplacementCharacters()
    {
        using var repo = await TestRepository.CreateAsync();
        await repo.WriteAsync("src/file", "data");
        await repo.CommitAsync("base");
        var malformed = new string((char)0xD800, 1);

        var malformedMaterialKey = await BuildAsync(repo, materialValues: new Dictionary<string, string> { [malformed] = "value" });
        var malformedMaterialValue = await BuildAsync(repo, materialValues: new Dictionary<string, string> { ["key"] = malformed });
        var malformedImplementation = await BuildAsync(repo, implementation: malformed);
        var malformedTool = await BuildAsync(repo, tool: malformed);
        var malformedResource = await BuildAsync(repo, resource: malformed);
        var validUnicode = await BuildAsync(repo, implementation: "implementation-\U0001F600", tool: "\U0001F600",
            resource: "\uFFFD", materialValues: new Dictionary<string, string> { ["\U0001F600"] = "\uFFFD" });

        Assert.Equal("reuse.identity.material_invalid", malformedMaterialKey.Reason);
        Assert.Equal("reuse.identity.material_invalid", malformedMaterialValue.Reason);
        Assert.Equal("reuse.identity.unavailable", malformedImplementation.Reason);
        Assert.Equal("reuse.identity.unavailable", malformedTool.Reason);
        Assert.Equal("reuse.identity.unavailable", malformedResource.Reason);
        Assert.True(validUnicode.IsEligible, validUnicode.Diagnostic);
    }

    [Fact]
    public async Task BuildAsync_ShouldRejectMalformedUtf16InHashedProducerDeclarationFields()
    {
        using var repo = await TestRepository.CreateAsync();
        await repo.WriteAsync("src/file", "data");
        await repo.CommitAsync("base");
        var malformed = new string((char)0xD800, 1);
        var producer = new EvidenceProducerDeclaration("producer", "coverage", "1", ["db"], ["assert"],
            [new EvidenceArtifactSlot("report", "reports", "text/plain", false, 10)], 30)
        {
            Inputs = new EvidenceProducerInputs(["src/**"], []),
        };
        EvidenceProducerDeclaration[] invalidDeclarations =
        [
            producer with { Id = malformed },
            producer with { Kind = malformed },
            producer with { Version = malformed },
            producer with { AssertionIds = [malformed] },
            producer with { ArtifactSlots = [producer.ArtifactSlots[0] with { LogicalName = malformed }] },
            producer with { ArtifactSlots = [producer.ArtifactSlots[0] with { RelativeRoot = malformed }] },
            producer with { ArtifactSlots = [producer.ArtifactSlots[0] with { MediaType = malformed }] },
        ];

        foreach (var invalidDeclaration in invalidDeclarations)
        {
            var result = await BuildAsync(repo, producerOverride: invalidDeclaration);
            Assert.Equal("reuse.identity.unavailable", result.Reason);
            Assert.Null(result.Identity);
        }
    }

    [Fact]
    public void BuildAsync_ShouldReportInventoryAndTimeLimitsWithCountsAndNextActions()
    {
        var countLimit = EvidenceProducerIdentityBuilder.CheckFileCountLimit(EvidenceProducerIdentityBuilder.MaximumFiles + 1);
        Assert.NotNull(countLimit);
        Assert.Equal("reuse.identity.files_limit", countLimit.Reason);
        var countDiagnostic = Assert.IsType<string>(countLimit.Diagnostic);
        Assert.Contains("100001", countDiagnostic, StringComparison.Ordinal);
        Assert.Contains("100000", countDiagnostic, StringComparison.Ordinal);
        Assert.Contains("Narrow declared include/exclude patterns", countDiagnostic, StringComparison.Ordinal);
        Assert.Null(EvidenceProducerIdentityBuilder.CheckFileCountLimit(EvidenceProducerIdentityBuilder.MaximumFiles));

        var estimatedMetadataLimit = EvidenceProducerIdentityBuilder.CheckEstimatedMetadataLimit(
            3, EvidenceProducerIdentityBuilder.MaximumMetadataBytes + 1L);
        Assert.NotNull(estimatedMetadataLimit);
        Assert.Equal("reuse.identity.metadata_limit", estimatedMetadataLimit.Reason);
        var estimatedDiagnostic = Assert.IsType<string>(estimatedMetadataLimit.Diagnostic);
        Assert.Contains("16777217", estimatedDiagnostic, StringComparison.Ordinal);
        Assert.Contains("3 files", estimatedDiagnostic, StringComparison.Ordinal);
        Assert.Contains("16777216", estimatedDiagnostic, StringComparison.Ordinal);
        Assert.Contains("Narrow declared include/exclude patterns", estimatedDiagnostic, StringComparison.Ordinal);
        Assert.Null(EvidenceProducerIdentityBuilder.CheckEstimatedMetadataLimit(
            3, EvidenceProducerIdentityBuilder.MaximumMetadataBytes));

        var encodedMetadataLimit = EvidenceProducerIdentityBuilder.CheckEncodedMetadataLimit(
            4, EvidenceProducerIdentityBuilder.MaximumMetadataBytes + 2L);
        Assert.NotNull(encodedMetadataLimit);
        Assert.Equal("reuse.identity.metadata_limit", encodedMetadataLimit.Reason);
        var encodedDiagnostic = Assert.IsType<string>(encodedMetadataLimit.Diagnostic);
        Assert.Contains("16777218", encodedDiagnostic, StringComparison.Ordinal);
        Assert.Contains("after 4 files", encodedDiagnostic, StringComparison.Ordinal);
        Assert.Contains("16777216", encodedDiagnostic, StringComparison.Ordinal);
        Assert.Null(EvidenceProducerIdentityBuilder.CheckEncodedMetadataLimit(
            4, EvidenceProducerIdentityBuilder.MaximumMetadataBytes));

        var timeLimit = EvidenceProducerIdentityBuilder.TimeLimitResult();
        Assert.Equal("reuse.identity.time_limit", timeLimit.Reason);
        var timeDiagnostic = Assert.IsType<string>(timeLimit.Diagnostic);
        Assert.Contains("60000 ms", timeDiagnostic, StringComparison.Ordinal);
        Assert.Contains("Narrow declared inputs", timeDiagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void FirstDifferingPath_ShouldReturnFirstFullRelativePath()
    {
        EvidenceProducerInputFile File(string path, string digest = "a") => new(path, 0x81A4, 1, digest);
        Assert.Equal("src/a/file", EvidenceProducerIdentityBuilder.FirstDifferingPath([File("src/a/file")], [File("src/b/file")]));
        Assert.Equal("src/a/file", EvidenceProducerIdentityBuilder.FirstDifferingPath([File("src/a/file")], [File("src/a/file", "b")]));
        Assert.Equal("src/a/file", EvidenceProducerIdentityBuilder.FirstDifferingPath([], [File("src/a/file")]));
        Assert.Null(EvidenceProducerIdentityBuilder.FirstDifferingPath([File("same")], [File("same")]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/rooted")]
    [InlineData("../escape")]
    [InlineData("src/../escape")]
    [InlineData("src//file")]
    [InlineData("src\\file")]
    [InlineData("C:/rooted")]
    [InlineData("src/./file")]
    public void IsValidInputRelativePath_ShouldRejectMalformedOrEscapingPaths(string path)
        => Assert.False(EvidenceProducerIdentityBuilder.IsValidInputRelativePath(path));

    [Theory]
    [InlineData("src/file")]
    [InlineData("src/ spaced .cs")]
    [InlineData("src/trailing ")]
    [InlineData("unicode/é.txt")]
    public void IsValidInputRelativePath_ShouldPreserveValidRelativePathSpelling(string path)
        => Assert.True(EvidenceProducerIdentityBuilder.IsValidInputRelativePath(path));

    private static async Task<EvidenceProducerIdentityResult> BuildAsync(TestRepository repo,
        string[]? include = null, string[]? exclude = null, string tool = "tool-v1", string resource = "image-v1",
        string? material = "flag-a", IReadOnlyDictionary<string, string>? materialValues = null,
        bool patchGate = false, string? patch = null, string? planPatch = null, string implementation = "implementation-v1",
        EvidenceProducerDeclaration? producerOverride = null)
    {
        var producer = producerOverride ?? new EvidenceProducerDeclaration("producer", "coverage", "1", ["db"], [], [], 30,
            patchGate ? new EvidenceCoverageGateRequirements(MinLinePercent: 0, MinBranchPercent: 0, MinPatchLinePercent: 1) : null)
        {
            Inputs = new EvidenceProducerInputs(include ?? ["src/**"], exclude ?? [])
        };
        var plan = new EvidencePlan("1.1", "policy", new string('a', 64), new string('b', 64),
            new EvidenceProfile("profile", EvidenceProfileScope.Targeted, [new EvidenceResourceDeclaration("db", "completion", 10, [])], [producer], []),
            [], [], new string('c', 64))
        { PatchSha256 = planPatch };
        var materialMap = materialValues ?? new Dictionary<string, string> { ["mode"] = material ?? "" };
        return await EvidenceProducerIdentityBuilder.BuildAsync(new EvidenceProducerIdentityRequest(repo.Root, plan, producer,
            implementation, new Dictionary<string, string> { ["db"] = resource }, new Dictionary<string, string> { ["tool"] = tool }, materialMap, patch),
            CancellationToken.None);
    }

    private sealed class TestRepository : IDisposable
    {
        internal TestRepository(string root)
        {
            Root = root;
        }
        internal string Root { get; }
        internal static async Task<TestRepository> CreateAsync(string? directoryName = null)
        {
            var root = Path.Combine(Path.GetTempPath(), directoryName is null
                ? "evidence-producer-identity-" + Guid.NewGuid().ToString("N")
                : "evidence-producer-identity-" + Guid.NewGuid().ToString("N") + directoryName);
            Directory.CreateDirectory(root);
            var repository = new TestRepository(root);
            await repository.GitAsync("init", "--quiet");
            await repository.GitAsync("config", "user.email", "test@example.invalid");
            await repository.GitAsync("config", "user.name", "Identity Test");
            await repository.GitAsync("config", "commit.gpgsign", "false");
            await repository.GitAsync("config", "core.hooksPath", TestPathUtils.PathUnder(repository.Root, "no-hooks"));
            return repository;
        }
        internal async Task WriteAsync(string relative, string contents)
        {
            var path = TestPathUtils.PathUnder(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, contents);
        }
        internal async Task CommitAsync(string message)
        {
            await GitAsync("add", "--all");
            await GitAsync("commit", "--quiet", "-m", message, "--allow-empty");
        }
        internal async Task GitAsync(params string[] arguments)
        {
            _ = await RunGitAsync(captureOutput: false, arguments);
        }

        internal Task<string> GitOutputAsync(params string[] arguments)
            => RunGitAsync(captureOutput: true, arguments);

        private async Task<string> RunGitAsync(bool captureOutput, params string[] arguments)
        {
            using var process = new Process { StartInfo = new ProcessStartInfo("git") { WorkingDirectory = Root, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false } };
            foreach (var name in process.StartInfo.Environment.Keys.Where(name => name.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
                process.StartInfo.Environment.Remove(name);
            process.StartInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            process.StartInfo.Environment["GIT_CONFIG_GLOBAL"] = TestPathUtils.PathUnder(Root, "absent-global-config");
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            try
            {
                await process.WaitForExitAsync(deadline.Token);
                await Task.WhenAll(stdout, stderr).WaitAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await process.WaitForExitAsync(cleanup.Token); } catch (OperationCanceledException) { }
                try { await Task.WhenAll(stdout, stderr).WaitAsync(cleanup.Token); } catch (OperationCanceledException) { }
                throw new TimeoutException("Git fixture command exceeded its 20-second deadline.");
            }

            var output = await stdout;
            var error = await stderr;
            if (process.ExitCode != 0) throw new InvalidOperationException(error);
            return captureOutput ? output.TrimEnd('\r', '\n') : string.Empty;
        }
        public void Dispose() => new TestDirectory(Root).Dispose();
    }
}
