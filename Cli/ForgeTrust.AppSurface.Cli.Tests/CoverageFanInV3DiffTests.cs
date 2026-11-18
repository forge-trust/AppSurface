using System.Diagnostics;
using System.Text;
using ForgeTrust.AppSurface.Coverage;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Protects bounded deterministic diff admission; synthetic object IDs make no native execution claim.</summary>
public sealed class CoverageFanInV3DiffTests
{
    [Theory]
    [InlineData("sha1", "manifest")]
    [InlineData("sha256", "manifest")]
    [InlineData("sha1", "space")]
    [InlineData("sha1", "unicode")]
    [InlineData("sha1", "quote")]
    [InlineData("sha1", "replacement")]
    [InlineData("sha1", "rename")]
    [InlineData("sha1", "deletion")]
    [InlineData("sha1", "empty")]
    [InlineData("sha1", "tag-object")]
    [InlineData("sha256", "tag-object")]
    public async Task RealPinnedObjectViewsHaveExactPortableCorrespondence(string format, string shape)
    {
        using var temp = TestDirectory.Create();
        var root = temp.Path;
        async Task<string> GitWithInput(string? input, params string[] args)
        {
            using var process = new Process { StartInfo = new("git") { WorkingDirectory = root, UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true } };
            foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            if (input is not null) await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, await stderr);
            return (await stdout).TrimEnd('\r', '\n');
        }
        Task<string> Git(params string[] args) => GitWithInput(null, args);
        await Git("init", "--object-format=" + format);
        await Git("config", "user.email", "fixture@example.invalid"); await Git("config", "user.name", "Fixture");
        var name = shape switch { "space" => "with space.cs", "unicode" => "café.cs", "quote" => "quote\".cs", "replacement" => "�.cs", _ => "file.cs" };
        string basis;
        string head;
        string tree;
        if (shape == "quote")
        {
            // Keep this Windows-invalid name in Git objects, never in the working tree.
            var beforeBlob = await GitWithInput("before\n", "hash-object", "-w", "--stdin");
            var beforeTree = await GitWithInput($"100644 blob {beforeBlob}\t{name}\0", "mktree", "-z");
            basis = await GitWithInput("fixture baseline\n", "commit-tree", beforeTree);
            var afterBlob = await GitWithInput("after\n", "hash-object", "-w", "--stdin");
            var afterTree = await GitWithInput($"100644 blob {afterBlob}\t{name}\0", "mktree", "-z");
            head = await GitWithInput("fixture change\n", "commit-tree", afterTree, "-p", basis);
            await Git("update-ref", "HEAD", head);
            tree = afterTree;
        }
        else
        {
            File.WriteAllText(Path.Join(root, name), "before\n");
            await Git("add", "."); await Git("commit", "-m", "fixture baseline");
            basis = await Git("rev-parse", "HEAD");
            if (shape == "rename") File.Move(Path.Join(root, name), Path.Join(root, "renamed.json"));
            else if (shape == "deletion") File.Delete(Path.Join(root, name));
            else if (shape != "empty") File.WriteAllText(Path.Join(root, name), "after\n");
            await Git("add", "-A"); await Git("commit", "--allow-empty", "-m", "fixture change");
            head = await Git("rev-parse", "HEAD"); tree = await Git("rev-parse", "HEAD^{tree}");
        }
        var policy = CoverageFanInPolicyTests.Policy() with
        {
            Source = new(head, tree, "10.0.402", "Release"),
            ContractVersion = 3,
            ReportProfileId = "coverlet-collector-cobertura-v1",
            Gate = new(CoverageGateMode.AggregateAndPatch, DiffBase: basis)
        };
        if (shape == "tag-object")
        {
            await Git("tag", "-a", "baseline", basis, "-m", "annotated baseline");
            policy = policy with { Gate = policy.Gate with { DiffBase = await Git("rev-parse", "refs/tags/baseline") } };
        }
        var request = new CoverageFanInEngineRequest(new(CoveragePolicyEncoding.ToElement(policy), CoveragePolicyEncoding.Digest(policy)), root);
        var context = new CoverageFanInDiffContext(request, new CoverageFanInBudget(policy.Limits?.TotalBytes ?? 1L << 32));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await context.InitializeAsync(deadline.Token);
        if (shape == "tag-object")
        {
            var refusal = await Assert.ThrowsAsync<CoverageFanInEvidenceException>(() => context.ReadAsync(deadline.Token));
            Assert.Equal("identity-mismatch", refusal.Cause);
            return;
        }
        var input = await context.ReadAsync(deadline.Token);
        var changes = CoverageFanInDiffParser.Parse(input);
        Assert.Equal(basis, input.RequestedBase); Assert.Equal(basis, input.MergeBase); Assert.Equal(head, input.Head);
        Assert.Equal(format == "sha1" ? 40 : 64, input.ObjectIdLength);
        if (shape == "empty") { Assert.Empty(changes); Assert.Empty(input.Patch); return; }
        var change = Assert.Single(changes);
        Assert.Equal(shape == "rename" ? "renamed" : shape == "deletion" ? "deleted" : "modified", change.Kind);
        Assert.Equal(name, change.OldPath);
        Assert.Equal(shape == "rename" ? "renamed.json" : shape == "deletion" ? null : name, change.NewPath);
        if (shape is "rename" or "deletion") Assert.Empty(change.AddedRanges);
        else Assert.Equal(new CoverageFanInAddedRange(1, 1), Assert.Single(change.AddedRanges));
        var replay = await context.ReadAsync(deadline.Token);
        Assert.Equal(input.Patch, replay.Patch); Assert.Equal(input.Raw, replay.Raw);
    }

    [Theory]
    [InlineData("git version 2.46.0", true)]
    [InlineData("git version 2.46", true)]
    [InlineData("git version 3.0", true)]
    [InlineData("git version 2.54.0 (Apple Git-157)", true)]
    [InlineData("git version 2.46.2.windows.1", true)]
    [InlineData("git version 1.99.0", false)]
    [InlineData("git version 2.45.9", false)]
    [InlineData("git version 2.43.0", false)]
    [InlineData("git version 2.99.0-unqualified", false)]
    [InlineData("git version 999999999999999999.1", false)]
    [InlineData("git version 2.999999999999999999", false)]
    [InlineData("not git", false)]
    public void VersionPrerequisiteIsEnforcedBeforeObjects(string version, bool valid)
    {
        if (valid) CoverageFanInDiffContext.RequireSupportedVersion(version);
        else Assert.Equal("unsupported-git-version", Assert.Throws<CoverageFanInEvidenceException>(() => CoverageFanInDiffContext.RequireSupportedVersion(version)).Cause);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20971519)]
    [InlineData(20971520)]
    public void BlobSizeBelowAndAtLimitChargesExactly(long bytes)
    {
        var budget = new CoverageFanInBudget(bytes + 100);
        var oid = new string('a', 40);
        CoverageFanInDiffContext.AdmitBlobSizes([oid], $"{oid} blob {bytes}\n", budget);
        Assert.Equal(100, budget.Remaining);
    }

    [Theory]
    [InlineData("20971521", "blob", "limit-exceeded")]
    [InlineData("-1", "blob", "malformed-diff")]
    [InlineData("9223372036854775808", "blob", "malformed-diff")]
    [InlineData("1", "tree", "malformed-diff")]
    [InlineData("missing", "blob", "malformed-diff")]
    public void InvalidOrOversizedBlobNeverBecomesEmpty(string size, string type, string cause)
    {
        var oid = new string('a', 40); var budget = new CoverageFanInBudget(1L << 32);
        Assert.Equal(cause, Assert.Throws<CoverageFanInEvidenceException>(() => CoverageFanInDiffContext.AdmitBlobSizes([oid], $"{oid} {type} {size}\n", budget)).Cause);
        Assert.Equal(1L << 32, budget.Remaining);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void AggregateBlobCapIsDistinctFromIndividualCap(int delta, bool valid)
    {
        var objects = Enumerable.Range(1, 13).Select(i => i.ToString("x40")).ToArray();
        var sizes = Enumerable.Repeat(CoverageFanInDiffContext.BlobLimit, 12)
            .Append(CoverageFanInDiffContext.BlobTotalLimit - 12 * CoverageFanInDiffContext.BlobLimit + delta).ToArray();
        var output = string.Concat(objects.Select((oid, i) => $"{oid} blob {sizes[i]}\n"));
        var budget = new CoverageFanInBudget(1L << 32);
        if (valid)
        {
            CoverageFanInDiffContext.AdmitBlobSizes(objects, output, budget);
            Assert.Equal((1L << 32) - sizes.Sum(), budget.Remaining);
        }
        else Assert.Equal("limit-exceeded", Assert.Throws<CoverageFanInEvidenceException>(() => CoverageFanInDiffContext.AdmitBlobSizes(objects, output, budget)).Cause);
    }

    [Fact]
    public async Task SharedBudgetCannotRacePastItsLimitOrResetAcrossProbes()
    {
        var budget = new CoverageFanInBudget(1000);
        var accepted = 0;
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
        {
            try { budget.Charge(100); Interlocked.Increment(ref accepted); }
            catch (CoverageFanInEvidenceException exception) { Assert.Equal("limit-exceeded", exception.Cause); }
        })));
        Assert.Equal(10, accepted); Assert.Equal(0, budget.Remaining);
        Assert.Throws<CoverageFanInEvidenceException>(() => budget.Charge(1));
        Assert.Throws<CoverageFanInEvidenceException>(() => budget.Charge(-1));
    }

    [Fact]
    public void BlobWorkSharesTheRemainingOperationBudget()
    {
        var oid = new string('a', 40); var budget = new CoverageFanInBudget(100);
        budget.Charge(90);
        Assert.Equal("limit-exceeded", Assert.Throws<CoverageFanInEvidenceException>(() =>
            CoverageFanInDiffContext.AdmitBlobSizes([oid], $"{oid} blob 11\n", budget)).Cause);
        Assert.Equal(10, budget.Remaining);
    }

    [Theory]
    [InlineData("src/café.cs")]
    [InlineData("src/with space.cs")]
    [InlineData("src/quote\".cs")]
    [InlineData("src/-option.cs")]
    [InlineData("src/�.cs")]
    public void ExactPortablePathsPreserveSpelling(string path) => Assert.Equal(path, CoverageFanInDiffParser.Path(path));

    [Theory]
    [InlineData("src\\File.cs")]
    [InlineData("../File.cs")]
    [InlineData("src/CON.cs")]
    [InlineData("src/NUL.txt")]
    [InlineData("src/PRN.txt")]
    [InlineData("src/AUX.txt")]
    [InlineData("src/COM1.txt")]
    [InlineData("src/LPT9.txt")]
    [InlineData("src/name.")]
    [InlineData("src/./name.cs")]
    [InlineData("src/name?.cs")]
    [InlineData("src/File.cs ")]
    [InlineData("src/with\nnewline.cs")]
    [InlineData("src//File.cs")]
    public void AliasesAndAmbiguousPathsRefuse(string path) => Assert.Equal("unsupported-diff",
        Assert.Throws<CoverageFanInEvidenceException>(() => CoverageFanInDiffParser.Path(path)).Cause);

    [Theory]
    [InlineData("src/COM0.cs")]
    [InlineData("src/LPT0.cs")]
    [InlineData("src/COMX.cs")]
    [InlineData("src/LPTX.cs")]
    [InlineData("src/COM10.cs")]
    public void DeviceLikeOrdinaryFileNamesKeepTheirExactPortableIdentity(string path)
        => Assert.Equal(path, CoverageFanInDiffParser.Path(path));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/absolute.cs")]
    [InlineData("src/name\u200B.cs")]
    public void EmptyRootedAndFormatControlPathsRefuse(string path) => Assert.Equal("unsupported-diff",
        Assert.Throws<CoverageFanInEvidenceException>(() => CoverageFanInDiffParser.Path(path)).Cause);

    [Fact]
    public void PortablePathLengthAndStrictUtf8BoundariesRefuse()
    {
        Assert.Equal("unsupported-diff", Assert.Throws<CoverageFanInEvidenceException>(
            () => CoverageFanInDiffParser.Path(new string('a', 4097))).Cause);
        Assert.Throws<EncoderFallbackException>(() => CoverageFanInDiffParser.Path("bad-\uD800.cs"));
    }

    [Theory]
    [InlineData(40)]
    [InlineData(64)]
    public void ModifiedHunksAcceptOmittedOneCountsAndNoNewlineMarker(int oidLength)
    {
        var old = new string('a', oidLength); var head = new string('b', oidLength);
        var raw = $":100644 100644 {old} {head} M\0src/F.cs\0";
        var patch = $"diff --git a/src/F.cs b/src/F.cs\nindex {old}..{head} 100644\n--- a/src/F.cs\n+++ b/src/F.cs\n@@ -1 +1 @@\n-old\n+new\n\\ No newline at end of file\n";
        var result = Assert.Single(CoverageFanInDiffParser.Parse(new(old, old, head, oidLength, Encoding.UTF8.GetBytes(patch), raw)));
        Assert.Equal("modified", result.Kind); Assert.Equal("src/F.cs", result.OldPath); Assert.Equal("src/F.cs", result.NewPath);
        Assert.Equal(new CoverageFanInAddedRange(1, 1), Assert.Single(result.AddedRanges));
    }

    [Fact]
    public void ExactEmptyNeedsBothViewsEmpty()
    {
        var id = new string('a', 40);
        Assert.Empty(CoverageFanInDiffParser.Parse(new(id, id, id, 40, [], "")));
        Assert.Throws<CoverageFanInEvidenceException>(() => CoverageFanInDiffParser.Parse(new(id, id, id, 40, [], $":100644 100644 {id} {new string('b', 40)} M\0F.cs\0")));
    }

    [Theory]
    [InlineData("rename")]
    [InlineData("mode")]
    [InlineData("deletion-only")]
    public void RecognizedNoAdditionsKeepExplicitChangeMetadata(string shape)
    {
        var old = new string('a', 40); var head = new string('b', 40);
        var raw = shape switch
        {
            "rename" => $":100644 100644 {old} {old} R100\0F.cs\0G.json\0",
            "mode" => $":100644 100755 {old} {old} M\0F.cs\0",
            _ => $":100644 100644 {old} {head} M\0F.cs\0"
        };
        var patch = shape switch
        {
            "rename" => "diff --git a/F.cs b/G.json\nsimilarity index 100%\nrename from F.cs\nrename to G.json\n",
            "mode" => "diff --git a/F.cs b/F.cs\nold mode 100644\nnew mode 100755\n",
            _ => $"diff --git a/F.cs b/F.cs\nindex {old}..{head} 100644\n--- a/F.cs\n+++ b/F.cs\n@@ -1 +0,0 @@\n-old\n"
        };
        var result = Assert.Single(CoverageFanInDiffParser.Parse(new(old, old, head, 40, Encoding.UTF8.GetBytes(patch), raw)));
        Assert.Empty(result.AddedRanges); Assert.NotNull(result.OldPath);
        Assert.Equal(shape == "rename" ? "renamed" : shape == "mode" ? "modeChanged" : "modified", result.Kind);
    }

    [Theory]
    [InlineData("count")]
    [InlineData("blob")]
    [InlineData("path")]
    [InlineData("binary")]
    public void MalformedOrContradictoryPatchCannotHideBehindRawInventory(string mutation)
    {
        var old = new string('a', 40); var head = new string('b', 40);
        var raw = $":100644 100644 {old} {head} M\0F.cs\0";
        var patch = $"diff --git a/F.cs b/F.cs\nindex {old}..{head} 100644\n--- a/F.cs\n+++ b/F.cs\n@@ -1 +1 @@\n-old\n+new\n";
        patch = mutation switch
        {
            "count" => patch.Replace("+1 @@", "+1,2 @@", StringComparison.Ordinal),
            "blob" => patch.Replace("index " + old, "index " + head, StringComparison.Ordinal),
            "path" => patch.Replace("+++ b/F.cs", "+++ b/G.cs", StringComparison.Ordinal),
            _ => $"diff --git a/F.cs b/F.cs\nindex {old}..{head} 100644\nBinary files a/F.cs and b/F.cs differ\n"
        };
        var expectedCause = mutation == "binary" ? "unsupported-diff" : "malformed-diff";
        Assert.Equal(expectedCause, Assert.Throws<CoverageFanInEvidenceException>(
            () => CoverageFanInDiffParser.Parse(new(old, old, head, 40, Encoding.UTF8.GetBytes(patch), raw))).Cause);
    }

    [Theory]
    [InlineData("50")]
    [InlineData("100")]
    public void RenameScoreBoundariesProduceTheSameSemanticRename(string score)
    {
        var old = new string('a', 40);
        var raw = $":100644 100644 {old} {old} R{score}\0old.cs\0new.cs\0";
        var patch = $"diff --git a/old.cs b/new.cs\nsimilarity index {score}%\nrename from old.cs\nrename to new.cs\n";

        var change = Assert.Single(CoverageFanInDiffParser.Parse(Input(raw, patch)));

        Assert.Equal("renamed", change.Kind);
        Assert.Equal("old.cs", change.OldPath);
        Assert.Equal("new.cs", change.NewPath);
        Assert.Equal(old, change.OldObject);
        Assert.Equal(old, change.NewObject);
        Assert.Empty(change.AddedRanges);
    }

    [Fact]
    public void AddedAndDeletedFilesMapNullSidesAndHunkRangesExactly()
    {
        var blob = new string('b', 40);
        var zeros = new string('0', 40);
        var addedRaw = $":000000 100644 {zeros} {blob} A\0src/new.cs\0";
        var addedPatch = $"diff --git a/src/new.cs b/src/new.cs\nnew file mode 100644\nindex {zeros}..{blob}\n--- /dev/null\n+++ b/src/new.cs\n@@ -0,0 +1,2 @@\n+first\n+second\n";
        var added = Assert.Single(CoverageFanInDiffParser.Parse(Input(addedRaw, addedPatch)));
        Assert.Equal("added", added.Kind);
        Assert.Null(added.OldPath);
        Assert.Equal("src/new.cs", added.NewPath);
        Assert.Equal(zeros, added.OldObject);
        Assert.Equal(blob, added.NewObject);
        Assert.Equal("000000", added.OldMode);
        Assert.Equal("100644", added.NewMode);
        Assert.Equal(new CoverageFanInAddedRange(1, 2), Assert.Single(added.AddedRanges));

        var old = new string('a', 40);
        var deletedRaw = $":100644 000000 {old} {zeros} D\0src/old.cs\0";
        var deletedPatch = $"diff --git a/src/old.cs b/src/old.cs\ndeleted file mode 100644\nindex {old}..{zeros}\n--- a/src/old.cs\n+++ /dev/null\n@@ -1 +0,0 @@\n-old\n";
        var deleted = Assert.Single(CoverageFanInDiffParser.Parse(Input(deletedRaw, deletedPatch)));
        Assert.Equal("deleted", deleted.Kind);
        Assert.Equal("src/old.cs", deleted.OldPath);
        Assert.Null(deleted.NewPath);
        Assert.Equal(old, deleted.OldObject);
        Assert.Equal(zeros, deleted.NewObject);
        Assert.Equal("100644", deleted.OldMode);
        Assert.Equal("000000", deleted.NewMode);
        Assert.Empty(deleted.AddedRanges);
    }

    [Fact]
    public void ContextAndSeparatedHunksReturnSortedCoalescedNewLineRanges()
    {
        var old = new string('a', 40);
        var head = new string('b', 40);
        var raw = $":100644 100644 {old} {head} M\0F.cs\0";
        var patch = $"diff --git a/F.cs b/F.cs\nindex {old}..{head} 100644\n--- a/F.cs\n+++ b/F.cs\n@@ -1,2 +1,3 @@\n one\n-two\n+two changed\n+inserted\n@@ -5,2 +6,2 @@\n five\n-six\n+six changed\n";

        var change = Assert.Single(CoverageFanInDiffParser.Parse(Input(raw, patch)));

        Assert.Equal("modified", change.Kind);
        Assert.Equal([new CoverageFanInAddedRange(2, 2), new CoverageFanInAddedRange(7, 1)], change.AddedRanges);
    }

    [Theory]
    [InlineData(0, "limit-exceeded")]
    [InlineData(1, "")]
    public void AddedLineExpansionHonorsTheExistingRecordBound(int maximumRecords, string expectedCause)
    {
        var old = new string('a', 40);
        var head = new string('b', 40);
        var raw = $":100644 100644 {old} {head} M\0F.cs\0";
        var patch = $"diff --git a/F.cs b/F.cs\nindex {old}..{head} 100644\n--- a/F.cs\n+++ b/F.cs\n@@ -1,0 +1,1 @@\n+new\n";
        var input = Input(raw, patch) with { MaximumRecords = maximumRecords };

        if (expectedCause.Length == 0)
            Assert.Equal(new CoverageFanInAddedRange(1, 1), Assert.Single(Assert.Single(CoverageFanInDiffParser.Parse(input)).AddedRanges));
        else
            Assert.Equal(expectedCause, Assert.Throws<CoverageFanInEvidenceException>(() => CoverageFanInDiffParser.Parse(input)).Cause);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(64)]
    public void RawViewsAcceptBothFullObjectIdFormatsAndPreserveModeChanges(int oidLength)
    {
        var blob = new string('a', oidLength);
        var raw = $":100644 100755 {blob} {blob} M\0script.sh\0";
        var change = Assert.Single(CoverageFanInDiffParser.ReadRaw(raw, oidLength));

        Assert.Equal(new CoverageFanInDiffChange("modeChanged", "script.sh", "script.sh", blob, blob, "100644", "100755", []), change);
    }

    [Theory]
    [InlineData(":\0")]
    [InlineData(":100644 100644 aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb M\0F.cs")]
    [InlineData(":100644 100644 aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb X\0F.cs\0")]
    [InlineData(":120000 100644 aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb M\0F.cs\0")]
    [InlineData(":100644 100644 aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa R50\0F.cs\0F.cs\0")]
    public void RawInventoryMalformedAndUnsupportedBoundariesKeepTheirRefusalCause(string raw)
    {
        var cause = raw.EndsWith("X\0F.cs\0", StringComparison.Ordinal) || raw.StartsWith(":120000", StringComparison.Ordinal)
            ? "unsupported-diff" : "malformed-diff";

        Assert.Equal(cause, Assert.Throws<CoverageFanInEvidenceException>(() => CoverageFanInDiffParser.ReadRaw(raw, 40)).Cause);
    }

    [Theory]
    [InlineData("extra-nul", "malformed-diff")]
    [InlineData("truncated-rename", "malformed-diff")]
    [InlineData("short-object-id", "malformed-diff")]
    [InlineData("uppercase-object-id", "malformed-diff")]
    [InlineData("zero-modified-object", "malformed-diff")]
    [InlineData("duplicate-path", "malformed-diff")]
    [InlineData("bad-rename-score", "unsupported-diff")]
    [InlineData("unsupported-mode", "unsupported-diff")]
    [InlineData("added-existing-old-side", "unsupported-diff")]
    [InlineData("nul-device-name", "unsupported-diff")]
    public void RawInventoryRejectsMalformedNulIdentityAndModeBoundaries(string mutation, string expectedCause)
    {
        var old = new string('a', 40);
        var head = new string('b', 40);
        var zeros = new string('0', 40);
        var regular = $":100644 100644 {old} {head} M\0F.cs\0";
        var raw = mutation switch
        {
            "extra-nul" => regular + "\0",
            "truncated-rename" => $":100644 100644 {old} {head} R100\0old.cs\0",
            "short-object-id" => $":100644 100644 {old[..^1]} {head} M\0F.cs\0",
            "uppercase-object-id" => $":100644 100644 {old[..^1]}A {head} M\0F.cs\0",
            "zero-modified-object" => $":100644 100644 {zeros} {zeros} M\0F.cs\0",
            "duplicate-path" => regular + regular,
            "bad-rename-score" => $":100644 100644 {old} {head} R49\0old.cs\0new.cs\0",
            "unsupported-mode" => $":100664 100644 {old} {head} M\0F.cs\0",
            "added-existing-old-side" => $":100644 100644 {old} {head} A\0F.cs\0",
            "nul-device-name" => $":100644 100644 {old} {head} M\0NUL.txt\0",
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };

        Assert.Equal(expectedCause, Assert.Throws<CoverageFanInEvidenceException>(
            () => CoverageFanInDiffParser.ReadRaw(raw, 40)).Cause);
    }

    [Theory]
    [InlineData("truncated", "malformed-diff")]
    [InlineData("missing-final-newline", "malformed-diff")]
    [InlineData("unexpected-section", "malformed-diff")]
    [InlineData("bad-hunk-number", "malformed-diff")]
    [InlineData("context", "")]
    [InlineData("unknown-extension", "unsupported-diff")]
    public void PatchFramingAndHunkShapesHaveExactSemanticResultsOrCauses(string shape, string expectedCause)
    {
        var old = new string('a', 40);
        var head = new string('b', 40);
        var raw = $":100644 100644 {old} {head} M\0F.cs\0";
        var valid = $"diff --git a/F.cs b/F.cs\nindex {old}..{head} 100644\n--- a/F.cs\n+++ b/F.cs\n@@ -1,1 +1,1 @@\n context\n";
        var patch = shape switch
        {
            "truncated" => valid.Replace(" context\n", "", StringComparison.Ordinal),
            "missing-final-newline" => valid[..^1],
            "unexpected-section" => valid + "diff --git a/G.cs b/G.cs\n",
            "bad-hunk-number" => valid.Replace("@@ -1,1 +1,1 @@", "@@ -x +1 @@", StringComparison.Ordinal),
            "unknown-extension" => $"diff --git a/F.cs b/F.cs\nGIT binary patch\n",
            _ => valid
        };

        if (expectedCause.Length == 0)
        {
            var change = Assert.Single(CoverageFanInDiffParser.Parse(Input(raw, patch)));
            Assert.Equal("modified", change.Kind);
            Assert.Empty(change.AddedRanges);
        }
        else Assert.Equal(expectedCause, Assert.Throws<CoverageFanInEvidenceException>(() => CoverageFanInDiffParser.Parse(Input(raw, patch))).Cause);
    }

    [Theory]
    [InlineData("index-duplicate", "malformed-diff")]
    [InlineData("header-order", "malformed-diff")]
    [InlineData("hunk-before-index", "malformed-diff")]
    [InlineData("rename-missing-origin", "malformed-diff")]
    [InlineData("invalid-header-escape", "malformed-diff")]
    [InlineData("unterminated-header-quote", "malformed-diff")]
    public void PatchMetadataContradictionsAreRejectedWithMalformedCause(string mutation, string expectedCause)
    {
        var old = new string('a', 40);
        var head = new string('b', 40);
        var raw = $":100644 100644 {old} {head} M\0F.cs\0";
        var patch = $"diff --git a/F.cs b/F.cs\nindex {old}..{head} 100644\n--- a/F.cs\n+++ b/F.cs\n@@ -1 +1 @@\n-old\n+new\n";
        patch = mutation switch
        {
            "index-duplicate" => patch.Replace("--- a/F.cs", $"index {old}..{head} 100644\n--- a/F.cs", StringComparison.Ordinal),
            "header-order" => patch.Replace($"--- a/F.cs\n+++ b/F.cs", $"+++ b/F.cs\n--- a/F.cs", StringComparison.Ordinal),
            "hunk-before-index" => patch.Replace($"index {old}..{head} 100644\n", string.Empty, StringComparison.Ordinal)
                .Replace("+new\n", $"+new\nindex {old}..{head} 100644\n", StringComparison.Ordinal),
            "rename-missing-origin" => patch,
            "invalid-header-escape" => patch.Replace("--- a/F.cs", "--- \"a/F\\777.cs\"", StringComparison.Ordinal),
            _ => patch.Replace("--- a/F.cs", "--- \"a/F.cs", StringComparison.Ordinal)
        };
        if (mutation == "rename-missing-origin")
            raw = raw.Replace("M\0F.cs", "R100\0F.cs\0G.cs", StringComparison.Ordinal);
        if (mutation == "rename-missing-origin")
            patch = $"diff --git a/F.cs b/G.cs\nsimilarity index 80%\nrename to G.cs\n";

        Assert.Equal(expectedCause, Assert.Throws<CoverageFanInEvidenceException>(() => CoverageFanInDiffParser.Parse(Input(raw, patch))).Cause);
    }

    [Theory]
    [InlineData("old-count-overrun")]
    [InlineData("new-count-overrun")]
    [InlineData("both-counts-zero")]
    [InlineData("old-start-zero")]
    [InlineData("new-start-zero")]
    [InlineData("old-hunk-overlap")]
    [InlineData("new-hunk-overlap")]
    [InlineData("changed-objects-without-index")]
    [InlineData("old-index-object-mismatch")]
    [InlineData("new-index-object-mismatch")]
    [InlineData("index-mode-mismatch")]
    [InlineData("mode-change-index")]
    [InlineData("mode-change-hunk")]
    [InlineData("no-newline-outside-hunk")]
    [InlineData("overflowing-hunk-number")]
    [InlineData("invalid-body-marker")]
    public void PatchHunkCountsOrderingAndObjectIdentityMustBeExact(string mutation)
    {
        var old = new string('a', 40);
        var head = new string('b', 40);
        var raw = $":100644 100644 {old} {head} M\0F.cs\0";
        var patch = $"diff --git a/F.cs b/F.cs\nindex {old}..{head} 100644\n--- a/F.cs\n+++ b/F.cs\n@@ -1 +1 @@\n-old\n+new\n";
        switch (mutation)
        {
            case "old-count-overrun":
                patch = patch.Replace("@@ -1 +1 @@", "@@ -1,2 +1 @@", StringComparison.Ordinal);
                break;
            case "new-count-overrun":
                patch = patch.Replace("@@ -1 +1 @@", "@@ -1 +1,2 @@", StringComparison.Ordinal);
                break;
            case "both-counts-zero":
                patch = patch.Replace("@@ -1 +1 @@", "@@ -1,0 +1,0 @@", StringComparison.Ordinal);
                break;
            case "old-start-zero":
                patch = patch.Replace("@@ -1 +1 @@", "@@ -0 +1 @@", StringComparison.Ordinal);
                break;
            case "new-start-zero":
                patch = patch.Replace("@@ -1 +1 @@", "@@ -1 +0 @@", StringComparison.Ordinal);
                break;
            case "old-hunk-overlap":
                patch = patch.Replace("@@ -1 +1 @@\n-old\n+new\n",
                    "@@ -1 +1 @@\n context\n@@ -1 +2 @@\n context\n", StringComparison.Ordinal);
                break;
            case "new-hunk-overlap":
                patch = patch.Replace("@@ -1 +1 @@\n-old\n+new\n",
                    "@@ -1 +1 @@\n context\n@@ -2 +1 @@\n context\n", StringComparison.Ordinal);
                break;
            case "changed-objects-without-index":
                patch = $"diff --git a/F.cs b/F.cs\n--- a/F.cs\n+++ b/F.cs\n";
                break;
            case "old-index-object-mismatch":
                patch = patch.Replace($"index {old}..{head}", $"index {head}..{head}", StringComparison.Ordinal);
                break;
            case "new-index-object-mismatch":
                patch = patch.Replace($"index {old}..{head}", $"index {old}..{old}", StringComparison.Ordinal);
                break;
            case "index-mode-mismatch":
                patch = patch.Replace($"index {old}..{head} 100644", $"index {old}..{head} 100755", StringComparison.Ordinal);
                break;
            case "mode-change-index":
                raw = $":100644 100755 {old} {old} M\0F.cs\0";
                patch = $"diff --git a/F.cs b/F.cs\nindex {old}..{old}\n";
                break;
            case "mode-change-hunk":
                raw = $":100644 100755 {old} {old} M\0F.cs\0";
                patch = $"diff --git a/F.cs b/F.cs\nindex {old}..{old}\n--- a/F.cs\n+++ b/F.cs\n@@ -1 +1 @@\n context\n";
                break;
            case "no-newline-outside-hunk":
                patch = "diff --git a/F.cs b/F.cs\n\\ No newline at end of file\n";
                break;
            case "overflowing-hunk-number":
                patch = patch.Replace("@@ -1 +1 @@", "@@ -2147483648 +1 @@", StringComparison.Ordinal);
                break;
            case "invalid-body-marker":
                patch = patch.Replace("-old\n+new\n", "?text\n", StringComparison.Ordinal);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        Assert.Equal("malformed-diff", Assert.Throws<CoverageFanInEvidenceException>(
            () => CoverageFanInDiffParser.Parse(Input(raw, patch))).Cause);
    }

    [Theory]
    [InlineData("similarity-low")]
    [InlineData("similarity-high")]
    [InlineData("similarity-missing-percent")]
    [InlineData("similarity-nonnumeric")]
    [InlineData("similarity-on-modification")]
    [InlineData("new-mode-wrong-kind")]
    [InlineData("new-file-mode-mismatch")]
    [InlineData("deleted-mode-wrong-kind")]
    [InlineData("deleted-file-mode-mismatch")]
    [InlineData("old-mode-same")]
    [InlineData("new-mode-same")]
    [InlineData("old-mode-mismatch")]
    [InlineData("new-mode-mismatch")]
    public void PatchModesAndRenameScoresMustMatchRawChange(string mutation)
    {
        var old = new string('a', 40);
        var head = new string('b', 40);
        var zeros = new string('0', 40);
        var raw = $":100644 100644 {old} {head} M\0F.cs\0";
        var patch = $"diff --git a/F.cs b/F.cs\nindex {old}..{head} 100644\n";
        switch (mutation)
        {
            case "similarity-low":
            case "similarity-high":
            case "similarity-missing-percent":
            case "similarity-nonnumeric":
                raw = $":100644 100644 {old} {old} R50\0old.cs\0new.cs\0";
                var score = mutation switch
                {
                    "similarity-low" => "49%",
                    "similarity-high" => "101%",
                    "similarity-missing-percent" => "50",
                    _ => "x%"
                };
                patch = $"diff --git a/old.cs b/new.cs\nsimilarity index {score}\nrename from old.cs\nrename to new.cs\n";
                break;
            case "similarity-on-modification":
                patch += "similarity index 50%\n";
                break;
            case "new-mode-wrong-kind":
            case "deleted-mode-wrong-kind":
                patch += mutation == "new-mode-wrong-kind" ? "new file mode 100644\n" : "deleted file mode 100644\n";
                break;
            case "new-file-mode-mismatch":
                raw = $":000000 100644 {zeros} {head} A\0F.cs\0";
                patch = $"diff --git a/F.cs b/F.cs\nindex {zeros}..{head}\nnew file mode 100755\n";
                break;
            case "deleted-file-mode-mismatch":
                raw = $":100644 000000 {old} {zeros} D\0F.cs\0";
                patch = $"diff --git a/F.cs b/F.cs\nindex {old}..{zeros}\ndeleted file mode 100755\n";
                break;
            case "old-mode-same":
                patch += "old mode 100644\n";
                break;
            case "new-mode-same":
                patch += "new mode 100644\n";
                break;
            case "old-mode-mismatch":
                raw = $":100644 100755 {old} {old} M\0F.cs\0";
                patch = $"diff --git a/F.cs b/F.cs\nold mode 100755\n";
                break;
            case "new-mode-mismatch":
                raw = $":100644 100755 {old} {old} M\0F.cs\0";
                patch = $"diff --git a/F.cs b/F.cs\nnew mode 100644\n";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        Assert.Equal("malformed-diff", Assert.Throws<CoverageFanInEvidenceException>(
            () => CoverageFanInDiffParser.Parse(Input(raw, patch))).Cause);
    }

    private static CoverageFanInDiffInput Input(string raw, string patch, int oidLength = 40)
    {
        var old = new string('a', oidLength);
        var head = new string('b', oidLength);
        return new(old, old, head, oidLength, Encoding.UTF8.GetBytes(patch), raw);
    }
}
