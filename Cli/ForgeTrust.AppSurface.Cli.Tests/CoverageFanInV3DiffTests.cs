using System.Text;
using System.Diagnostics;
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
        async Task<string> Git(params string[] args)
        {
            using var process = new Process { StartInfo = new("git") { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true } };
            foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, await stderr);
            return (await stdout).TrimEnd('\r', '\n');
        }
        await Git("init", "--object-format=" + format);
        await Git("config", "user.email", "fixture@example.invalid"); await Git("config", "user.name", "Fixture");
        var name = shape switch { "space" => "with space.cs", "unicode" => "café.cs", "quote" => "quote\".cs", "replacement" => "�.cs", _ => "file.cs" };
        File.WriteAllText(Path.Join(root, name), "before\n");
        await Git("add", "."); await Git("commit", "-m", "fixture baseline");
        var basis = await Git("rev-parse", "HEAD");
        if (shape == "rename") File.Move(Path.Join(root, name), Path.Join(root, "renamed.json"));
        else if (shape == "deletion") File.Delete(Path.Join(root, name));
        else if (shape != "empty") File.WriteAllText(Path.Join(root, name), "after\n");
        await Git("add", "-A"); await Git("commit", "--allow-empty", "-m", "fixture change");
        var head = await Git("rev-parse", "HEAD"); var tree = await Git("rev-parse", "HEAD^{tree}");
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
    [InlineData("git version 2.54.0 (Apple Git-157)", true)]
    [InlineData("git version 2.46.2.windows.1", true)]
    [InlineData("git version 2.45.9", false)]
    [InlineData("git version 2.43.0", false)]
    [InlineData("git version 2.99.0-unqualified", false)]
    [InlineData("git version 999999999999999999.1", false)]
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
    [InlineData("src/File.cs ")]
    [InlineData("src/with\nnewline.cs")]
    [InlineData("src//File.cs")]
    public void AliasesAndAmbiguousPathsRefuse(string path) => Assert.Equal("unsupported-diff",
        Assert.Throws<CoverageFanInEvidenceException>(() => CoverageFanInDiffParser.Path(path)).Cause);

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
        Assert.Throws<CoverageFanInEvidenceException>(() => CoverageFanInDiffParser.Parse(new(old, old, head, 40, Encoding.UTF8.GetBytes(patch), raw)));
    }
}
