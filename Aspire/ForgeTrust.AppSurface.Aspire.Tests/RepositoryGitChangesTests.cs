using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Validation;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class RepositoryGitChangesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("repository-git-changes-").FullName;
    private readonly RepositoryGitChanges _collector = new();

    [Fact]
    public async Task CollectLocalAsync_CombinesCommittedStagedUnstagedAndUntrackedChanges()
    {
        Git("init", "-q"); Git("config", "user.email", "test@example.invalid"); Git("config", "user.name", "Test");
        Write("tracked.txt", "base\n"); Write("removed.txt", "remove\n"); Git("add", "."); Git("commit", "-qm", "base");
        var baseCommit = Git("rev-parse", "HEAD");
        Write("committed.txt", "commit\n"); Git("add", "committed.txt"); Git("commit", "-qm", "change");
        Write("staged.txt", "staged\n"); Git("add", "staged.txt");
        Write("tracked.txt", "base\nworktree\n");
        Git("rm", "-q", "removed.txt");
        Write("new.txt", "untracked\n"); Write("ignored.bin", "ignored\n"); Write(".gitignore", "ignored.bin\n");

        var result = await _collector.CollectLocalAsync(_root, baseCommit, CancellationToken.None);

        Assert.Equal(Git("rev-parse", "HEAD"), result.HeadCommit);
        Assert.Equal(baseCommit, result.BaseCommit);
        Assert.Contains(result.Paths, p => p.Path == "committed.txt");
        Assert.Contains(result.Paths, p => p.Path == "staged.txt");
        Assert.Contains(result.Paths, p => p.Path == "tracked.txt");
        Assert.Contains(result.Paths, p => p.Path == "removed.txt" && p.Kind == "deleted");
        Assert.Contains(result.Paths, p => p.Path == "new.txt");
        Assert.DoesNotContain(result.Paths, p => p.Path == "ignored.bin");
        var changedDigest = result.SourceDigest;
        Write("tracked.txt", "base\nsecond edit\n");
        Assert.NotEqual(changedDigest, (await _collector.CollectLocalAsync(_root, baseCommit, CancellationToken.None)).SourceDigest);
    }

    [Fact]
    public async Task WriteLocalPatchAsync_UsesCurrentTrackedAndUntrackedContents()
    {
        Initialize();
        Write("staged.txt", "baseline staged\n");
        Write("unstaged.txt", "baseline unstaged\n");
        var baseline = CommitAll("baseline");
        Write("staged.txt", "current staged content\n");
        Git("add", "staged.txt");
        Write("unstaged.txt", "current unstaged content\n");
        Write("unstaged.txt", "current unstaged file\n");
        Write("untracked.txt", "current untracked content\n");
        Write("empty.txt", "");
        var changes = await _collector.CollectLocalAsync(_root, baseline, CancellationToken.None);
        var output = Path.Join(_root, "local.patch");

        await _collector.WriteLocalPatchAsync(_root, baseline, changes.Paths, output, CancellationToken.None);

        var patch = await File.ReadAllTextAsync(output);
        Assert.Contains("+current staged content", patch);
        Assert.Contains("+current unstaged file", patch);
        Assert.Contains("+current unstaged file", patch);
        Assert.Contains("+current untracked content", patch);
        Assert.Contains("diff --git a/empty.txt b/empty.txt", patch);
        Assert.DoesNotContain("@@ -0,0 +1,1 @@\n+\n", patch);
    }

    [Theory]
    [InlineData("--assume-unchanged", false)]
    [InlineData("--assume-unchanged", true)]
    [InlineData("--skip-worktree", false)]
    [InlineData("--skip-worktree", true)]
    public async Task CollectLocalAsync_RejectsIndexFlagsThatCanHideTrackedSource(string flag, bool modified)
    {
        Initialize();
        Write("tracked.cs", "baseline source\n");
        var baseline = CommitAll("baseline");
        Git("update-index", flag, "--", "tracked.cs");
        if (modified) Write("tracked.cs", "hidden source edit\n");
        Assert.Empty(Git("diff", "--name-only"));
        var indexState = Git("ls-files", "-v", "--", "tracked.cs");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _collector.CollectLocalAsync(_root, baseline, CancellationToken.None));

        Assert.Contains("assume-unchanged or skip-worktree", exception.Message);
        Assert.Equal(indexState, Git("ls-files", "-v", "--", "tracked.cs"));
        Assert.Equal(modified ? "hidden source edit\n" : "baseline source\n", await File.ReadAllTextAsync(Path.Join(_root, "tracked.cs")));
        Git("update-index", flag == "--assume-unchanged" ? "--no-assume-unchanged" : "--no-skip-worktree", "--", "tracked.cs");
        var collected = await _collector.CollectLocalAsync(_root, baseline, CancellationToken.None);
        Assert.Equal(modified, collected.Paths.Any(path => path.Path == "tracked.cs"));
    }

    [Theory]
    [InlineData("diff.mnemonicPrefix", "true")]
    [InlineData("diff.noprefix", "true")]
    [InlineData("diff.srcPrefix", "before/")]
    [InlineData("diff.dstPrefix", "after/")]
    [InlineData("color.diff", "always")]
    public async Task WriteLocalPatchAsync_IgnoresUserDiffPresentationSettings(string setting, string value)
    {
        Initialize();
        Write("tracked.cs", "baseline\n");
        var baseline = CommitAll("baseline");
        Write("tracked.cs", "current tracked content\n");
        Write("new.cs", "current untracked content\n");
        var before = await _collector.CollectLocalAsync(_root, baseline, CancellationToken.None);
        Git("config", setting, value);
        var changes = await _collector.CollectLocalAsync(_root, baseline, CancellationToken.None);
        var output = Path.Join(_root, "local.patch");

        await _collector.WriteLocalPatchAsync(_root, baseline, changes.Paths, output, CancellationToken.None);

        var patch = await File.ReadAllTextAsync(output);
        Assert.Contains("--- a/tracked.cs\n+++ b/tracked.cs\n", patch);
        Assert.Contains("+current tracked content\n", patch);
        Assert.Contains("+++ b/new.cs\n", patch);
        Assert.DoesNotContain("\u001b", patch, StringComparison.Ordinal);
        Assert.Equal(before.SourceDigest, changes.SourceDigest);
        Assert.Equal(value, Git("config", "--get", setting));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CollectLocalAsync_BindsExecutableBitChangesEvenWhenGitIgnoresThem(bool initiallyExecutable)
    {
        if (OperatingSystem.IsWindows()) return;

        Initialize();
        const string relativePath = "tool.sh";
        const string contents = "#!/bin/sh\nexit 0\n";
        Write(relativePath, contents);
        var path = TestPathUtils.PathUnder(_root, relativePath);
        var initialMode = File.GetUnixFileMode(path) & ~UnixFileMode.UserExecute;
        if (initiallyExecutable) initialMode |= UnixFileMode.UserExecute;
        File.SetUnixFileMode(path, initialMode);
        Git("add", "--", relativePath);
        Git("update-index", initiallyExecutable ? "--chmod=+x" : "--chmod=-x", "--", relativePath);
        Git("commit", "-qm", "baseline");
        var baseline = Git("rev-parse", "HEAD");
        var before = await _collector.CollectLocalAsync(_root, baseline, CancellationToken.None);
        Assert.Empty(before.Paths);

        File.SetUnixFileMode(path, initialMode ^ UnixFileMode.UserExecute);
        Assert.Empty(Git("diff", "--name-only", "--", relativePath));
        var after = await _collector.CollectLocalAsync(_root, baseline, CancellationToken.None);
        var change = Assert.Single(after.Paths);
        Assert.Equal(relativePath, change.Path);
        Assert.Equal("modified", change.Kind);
        Assert.NotEqual(before.SourceDigest, after.SourceDigest);

        var output = Path.Join(_root, "mode.patch");
        await _collector.WriteLocalPatchAsync(_root, baseline, after.Paths, output, CancellationToken.None);
        var patch = await File.ReadAllTextAsync(output);
        Assert.Contains(initiallyExecutable ? "old mode 100755\nnew mode 100644" : "old mode 100644\nnew mode 100755", patch);
        Assert.DoesNotContain("@@", patch, StringComparison.Ordinal);
        Assert.Equal(contents, await File.ReadAllTextAsync(path));
        Assert.Equal("false", Git("config", "--get", "core.filemode"));
    }

    [Fact]
    public async Task WriteLocalPatchAsync_DescribesBinaryUntrackedFilesAndSkipsUntrackedSymlinks()
    {
        if (OperatingSystem.IsWindows()) return;
        Initialize();
        Write("baseline.txt", "baseline\n");
        var baseline = CommitAll("baseline");
        File.WriteAllBytes(Path.Join(_root, "binary.dat"), [0, 1, 255]);
        File.CreateSymbolicLink(Path.Join(_root, "external-link"), Path.Join(_root, "outside-target"));
        var changes = await _collector.CollectLocalAsync(_root, baseline, CancellationToken.None);
        var output = Path.Join(_root, "local.patch");

        await _collector.WriteLocalPatchAsync(_root, baseline, changes.Paths, output, CancellationToken.None);

        var patch = await File.ReadAllTextAsync(output);
        Assert.Contains("Binary files /dev/null and b/binary.dat differ", patch);
        Assert.DoesNotContain("external-link", patch);
    }

    [Fact]
    public async Task WriteLocalPatchAsync_RejectsStaleInventoryAndOversizedUntrackedInput()
    {
        Initialize();
        Write("baseline.txt", "baseline\n");
        var baseline = CommitAll("baseline");
        var initial = await _collector.CollectLocalAsync(_root, baseline, CancellationToken.None);
        Write("new.txt", "captured inventory\n");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _collector.WriteLocalPatchAsync(
            _root, baseline, initial.Paths, Path.Join(_root, "stale.patch"), CancellationToken.None));

        var oversizedPath = Path.Join(_root, "oversized.bin");
        await using (var stream = File.Create(oversizedPath))
        {
            stream.SetLength(16 * 1024 * 1024 + 1);
        }
        var withOversized = await _collector.CollectLocalAsync(_root, baseline, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _collector.WriteLocalPatchAsync(
            _root, baseline, withOversized.Paths, Path.Join(_root, "oversized.patch"), CancellationToken.None));
    }

    [Fact]
    public async Task CollectLocalAsync_TracksRenameAndPreservesEmptyInventory()
    {
        Git("init", "-q"); Git("config", "user.email", "test@example.invalid"); Git("config", "user.name", "Test");
        Write("old.txt", "same content\n"); Git("add", "."); Git("commit", "-qm", "base");
        var baseCommit = Git("rev-parse", "HEAD");
        Git("mv", "old.txt", "new.txt");
        var renamed = await _collector.CollectLocalAsync(_root, baseCommit, CancellationToken.None);
        Assert.Contains(renamed.Paths, p => p.Path == "new.txt" && p.PreviousPath == "old.txt");
        Git("commit", "-qm", "rename");
        var current = Git("rev-parse", "HEAD");
        Assert.Empty((await _collector.CollectLocalAsync(_root, current, CancellationToken.None)).Paths);
    }

    [Fact]
    public void ParseNameStatus_RejectsInvalidUtf8AndMalformedRecords()
    {
        Assert.Throws<FormatException>(() => RepositoryGitChanges.ParseNameStatus([0x4d, 0, 0xff, 0]));
        Assert.Throws<FormatException>(() => RepositoryGitChanges.ParseNameStatus(Encoding.UTF8.GetBytes("R100\0only-one\0")));
        Assert.Throws<FormatException>(() => RepositoryGitChanges.ParseNameStatus(Encoding.UTF8.GetBytes("M\0../escape\0")));
        Assert.Throws<FormatException>(() => RepositoryGitChanges.ParseNameStatus(Encoding.UTF8.GetBytes("U\0conflict.txt\0")));
    }

    [Fact]
    public async Task CollectEventAsync_DispatchReturnsFullCommittedInventoryAndRequiresCheckedOutHead()
    {
        Git("init", "-q"); Git("config", "user.email", "test@example.invalid"); Git("config", "user.name", "Test");
        Write("one.txt", "one\n"); Write("nested/two.txt", "two\n"); Git("add", "."); Git("commit", "-qm", "inventory");
        var head = Git("rev-parse", "HEAD");
        var payload = Path.Join(_root, "event.json"); await File.WriteAllTextAsync(payload, "{}");

        var result = await _collector.CollectEventAsync(_root, payload, "workflow_dispatch", head, CancellationToken.None);

        Assert.Equal(head, result.HeadCommit);
        Assert.Equal(head, result.BaseCommit);
        Assert.Equal(new[] { "nested/two.txt", "one.txt" }, result.Paths.Select(p => p.Path));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _collector.CollectEventAsync(_root, payload, "workflow_dispatch", new string('a', 40), CancellationToken.None));
    }

    [Fact]
    public async Task CollectLocalAsync_ObservesCancellationBeforeStartingGit()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _collector.CollectLocalAsync(_root, "main", cancellation.Token));
    }

    [Fact]
    public async Task PullRequest_UsesMergeBaseAndIncludesEveryBranchCommit()
    {
        Initialize();
        Write("base.txt", "common");
        var common = CommitAll("common");
        Git("branch", "-M", "main");
        Git("checkout", "-qb", "feature");
        Write("first.txt", "first feature commit");
        CommitAll("first");
        Write("second.txt", "second feature commit");
        var head = CommitAll("second");
        Git("checkout", "-q", "main");
        Write("base-advanced.txt", "only on main");
        var target = CommitAll("advance main");
        Git("checkout", "-q", "feature");
        var payload = await EventAsync(new { pull_request = new { @base = new { sha = target }, head = new { sha = head } } });

        var changes = await _collector.CollectEventAsync(_root, payload, "pull_request", target, CancellationToken.None);

        Assert.Equal(common, changes.BaseCommit);
        Assert.Equal(head, changes.HeadCommit);
        Assert.Equal(["first.txt", "second.txt"], changes.Paths.Select(path => path.Path));
    }

    [Fact]
    public async Task Push_CollectsBinaryModeRenameCopyAndDeletionWithoutContentParsing()
    {
        Initialize();
        Write("old.txt", "rename content");
        Write("delete.txt", "delete content");
        Write("copy-source.txt", "copy content");
        Write("mode.sh", "#!/bin/sh\nexit 0\n");
        File.WriteAllBytes(Path.Join(_root, "binary.bin"), [0, 1, 255]);
        var before = CommitAll("before");
        Git("mv", "old.txt", "new.txt");
        Git("rm", "-q", "delete.txt");
        Write("copy-target.txt", "copy content");
        File.WriteAllBytes(Path.Join(_root, "binary.bin"), [0, 2, 254]);
        Git("add", "-A");
        Git("update-index", "--chmod=+x", "mode.sh");
        Git("commit", "-qm", "after");
        var head = Git("rev-parse", "HEAD");
        var payload = await EventAsync(new { before, after = head });

        var changes = await _collector.CollectEventAsync(_root, payload, "push", head, CancellationToken.None);

        Assert.Contains(changes.Paths, path => path.Path == "binary.bin" && path.Kind == "modified");
        Assert.Contains(changes.Paths, path => path.Path == "mode.sh");
        Assert.Contains(changes.Paths, path => path.Path == "delete.txt" && path.Kind == "deleted");
        Assert.Contains(changes.Paths, path => path.Path == "new.txt" && path.PreviousPath == "old.txt");
        Assert.Contains(changes.Paths, path => path.Path == "copy-target.txt" && path.PreviousPath == "copy-source.txt" && path.Kind == "copied");
    }

    [Fact]
    public async Task InitialPushAndManualRun_UseTheCompleteTreeRatherThanOnlyTheLastCommit()
    {
        Initialize();
        Write("earlier.txt", "first");
        CommitAll("first");
        Write("latest.txt", "second");
        var head = CommitAll("second");
        var payload = await EventAsync(new { before = new string('0', 40), after = head });
        var initial = await _collector.CollectEventAsync(_root, payload, "push", head, CancellationToken.None);
        var manual = await _collector.CollectEventAsync(_root, payload, "workflow_dispatch", head, CancellationToken.None);
        Assert.Equal(["earlier.txt", "latest.txt"], initial.Paths.Select(path => path.Path));
        Assert.Equal(initial.Paths, manual.Paths);
    }

    [Fact]
    public async Task EventPinsMissingHistoryAndEmptyDiffsFailClosed()
    {
        Initialize();
        Write("file.txt", "first");
        var before = CommitAll("first");
        Write("file.txt", "second");
        var head = CommitAll("second");
        foreach (var pair in new[] { (Before: head, After: head), (Before: new string('f', 40), After: head), (Before: before, After: before) })
        {
            var payload = await EventAsync(new { before = pair.Before, after = pair.After });
            await Assert.ThrowsAsync<InvalidOperationException>(() => _collector.CollectEventAsync(_root, payload, "push", head, CancellationToken.None));
        }
        var invalid = await EventAsync(new { before, after = "HEAD" });
        await Assert.ThrowsAsync<FormatException>(() => _collector.CollectEventAsync(_root, invalid, "push", head, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _collector.CollectEventAsync(_root, invalid, "schedule", head, CancellationToken.None));
    }

    [Fact]
    public async Task IndexOnlyContentChange_ChangesDigestWhenWorkingFileBytesRemainTheSame()
    {
        Initialize();
        Write("file.txt", "baseline");
        var baseCommit = CommitAll("baseline");
        Write("file.txt", "staged version one");
        Git("add", "file.txt");
        Write("file.txt", "working version");
        var first = await _collector.CollectLocalAsync(_root, baseCommit, CancellationToken.None);
        Write("file.txt", "staged version two");
        Git("add", "file.txt");
        Write("file.txt", "working version");
        var second = await _collector.CollectLocalAsync(_root, baseCommit, CancellationToken.None);
        Assert.Equal(first.Paths, second.Paths);
        Assert.NotEqual(first.SourceDigest, second.SourceDigest);
    }

    [Fact]
    public async Task SymlinkTargets_AreHashedWithoutReadingExternalContents()
    {
        if (OperatingSystem.IsWindows()) return;
        Initialize();
        Write("baseline.txt", "baseline");
        var baseCommit = CommitAll("baseline");
        var outside = Directory.CreateTempSubdirectory("repository-git-external-").FullName;
        try
        {
            var externalFile = Path.Join(outside, "file.txt");
            await File.WriteAllTextAsync(externalFile, "external version one");
            var link = Path.Join(_root, "link.txt");
            File.CreateSymbolicLink(link, externalFile);
            var first = await _collector.CollectLocalAsync(_root, baseCommit, CancellationToken.None);
            await File.WriteAllTextAsync(externalFile, "external version two");
            var second = await _collector.CollectLocalAsync(_root, baseCommit, CancellationToken.None);
            Assert.Equal(first.SourceDigest, second.SourceDigest);
            File.Delete(link);
            File.CreateSymbolicLink(link, Path.Join(outside, "missing.txt"));
            var third = await _collector.CollectLocalAsync(_root, baseCommit, CancellationToken.None);
            Assert.NotEqual(second.SourceDigest, third.SourceDigest);
        }
        finally { Directory.Delete(outside, recursive: true); }
    }

    [Theory]
    [InlineData("--all")]
    [InlineData(" ")]
    [InlineData("bad\nref")]
    public async Task InvalidLocalBase_RejectsOptionsAndWhitespace(string revision) =>
        await Assert.ThrowsAsync<ArgumentException>(() => _collector.CollectLocalAsync(_root, revision, CancellationToken.None));

    [Theory]
    [InlineData("M\0path")]
    [InlineData("X\0path\0")]
    [InlineData("R101\0old\0new\0")]
    [InlineData("R\0old\0new\0")]
    [InlineData("M\0a//b\0")]
    [InlineData("M\0/absolute\0")]
    [InlineData("M\0 leading\0")]
    public void MalformedStatusesAndPaths_AreRejected(string input) =>
        Assert.Throws<FormatException>(() => RepositoryGitChanges.ParseNameStatus(Encoding.UTF8.GetBytes(input)));

    [Fact]
    public void FixtureCleanup_RemovesReadOnlyGitObjectsWithoutFollowingLinks()
    {
        var owned = Path.Join(_root, "cleanup-owned");
        var objects = Path.Join(owned, ".git", "objects", "ab");
        Directory.CreateDirectory(objects);
        var gitObject = Path.Join(objects, "read-only-object");
        File.WriteAllText(gitObject, "owned Git object");
        File.SetAttributes(gitObject, File.GetAttributes(gitObject) | FileAttributes.ReadOnly);
        var outside = Path.Join(_root, "cleanup-external");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Join(outside, "sentinel.txt");
        File.WriteAllText(sentinel, "preserve external bytes");
        File.SetAttributes(sentinel, File.GetAttributes(sentinel) | FileAttributes.ReadOnly);
        Directory.CreateSymbolicLink(Path.Join(owned, "directory-link"), outside);
        File.CreateSymbolicLink(Path.Join(owned, "file-link"), sentinel);

        NativeEvidenceTestDirectory.Delete(owned);

        Assert.False(Directory.Exists(owned));
        Assert.Equal("preserve external bytes", File.ReadAllText(sentinel));
        Assert.True(File.GetAttributes(sentinel).HasFlag(FileAttributes.ReadOnly));

        var rootLink = Path.Join(_root, "cleanup-root-link");
        Directory.CreateSymbolicLink(rootLink, outside);
        NativeEvidenceTestDirectory.Delete(rootLink);
        Assert.False(Directory.Exists(rootLink));
        Assert.Equal("preserve external bytes", File.ReadAllText(sentinel));
        Assert.True(File.GetAttributes(sentinel).HasFlag(FileAttributes.ReadOnly));
    }

    private void Initialize()
    {
        Git("init", "-q");
        Git("config", "user.email", "test@example.invalid");
        Git("config", "user.name", "Test");
        Git("config", "core.autocrlf", "false");
        Git("config", "core.filemode", "false");
    }

    private string CommitAll(string message)
    {
        Git("add", "-A");
        Git("commit", "-qm", message);
        return Git("rev-parse", "HEAD");
    }

    private async Task<string> EventAsync<T>(T value)
    {
        var path = Path.Join(_root, "event.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value));
        return path;
    }

    private void Write(string relative, string content)
    {
        var path = TestPathUtils.PathUnder(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string Git(params string[] arguments)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("git") { WorkingDirectory = _root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd(); process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
        return output.Trim();
    }

    public void Dispose() => NativeEvidenceTestDirectory.Delete(_root);
}
