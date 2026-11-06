using System.Collections;
using System.Diagnostics;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace ForgeTrust.AppSurface.Web.Tailwind.Tests;

/// <summary>Exercises the publish preflight MSBuild task through its public Execute API.</summary>
public sealed class PublishPreflightTaskTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "publish-preflight-tests-" + Guid.NewGuid().ToString("N"));

    public PublishPreflightTaskTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Paths_AllowsDistinctDestinationsAndSameSourceDuplicate()
    {
        var engine = new RecordingBuildEngine();
        var task = new AppSurfacePublishPreflightTask { BuildEngine = engine, Phase = "paths" };
        task.OrdinaryFiles = [Item("src/a.css", "wwwroot/a.css"), Item("src/a.css", "wwwroot/a.css")];
        task.StaticFiles = [Item("src/b.css", "wwwroot/b.css", "TargetPath")];

        Assert.True(task.Execute());
        Assert.Empty(engine.Errors);
    }

    [Theory]
    [InlineData("../outside", "invalid relative destination")]
    [InlineData("/absolute/file", "invalid relative destination")]
    [InlineData("C:/absolute/file", "invalid relative destination")]
    [InlineData("folder/./file", "invalid relative destination")]
    [InlineData("folder/", "invalid relative destination")]
    public void Paths_ReportsAspub003ForInvalidDestination(string destination, string _)
    {
        var engine = new RecordingBuildEngine();
        var task = CreateTask(engine, "paths");
        task.OrdinaryFiles = [Item("src/file", destination)];

        Assert.False(task.Execute());
        AssertCode(engine, "ASPUB003");
    }

    [Fact]
    public void Paths_ReportsAspub003ForAnEmptyOrdinaryCopyList()
    {
        var engine = new RecordingBuildEngine();
        var task = CreateTask(engine, "paths");

        Assert.False(task.Execute());
        AssertCode(engine, "ASPUB003");
    }

    [Theory]
    [InlineData("wwwroot/Readme.txt", "wwwroot/readme.txt")]
    [InlineData("wwwroot/item", "wwwroot/item/child.txt")]
    [InlineData("wwwroot/item/child.txt", "wwwroot/item")]
    [InlineData("wwwroot/same.txt", "wwwroot/same.txt")]
    public void Paths_ReportsAspub001ForConflictingDestinations(string first, string second)
    {
        var engine = new RecordingBuildEngine();
        var task = CreateTask(engine, "paths");
        task.OrdinaryFiles = [Item("src/first", first), Item("src/second", second)];

        Assert.False(task.Execute());
        AssertCode(engine, "ASPUB001");
    }

    [Fact]
    public void Paths_ReportsRelativeSourcesAndRedactsExternalPathsAndControlCharacters()
    {
        var engine = new RecordingBuildEngine();
        var repository = Path.Join(_root, "checkout");
        var external = Path.Join(_root, "external", "sensitive-fixture.txt");
        var task = CreateTask(engine, "paths");
        task.RepositoryDirectory = repository;
        task.ProjectDirectory = Path.Join(repository, "sample");
        task.OrdinaryFiles =
        [
            Item(Path.Join(repository, "fixture-inputs", "first.txt"), "content/Badge\t1.txt"),
            Item(external, "Content/badge\t1.txt")
        ];

        Assert.False(task.Execute());
        var error = Assert.Single(engine.Errors);
        Assert.Equal("ASPUB001", error.Code);
        Assert.Contains("'content/Badge?1.txt' ('fixture-inputs/first.txt')", error.Message, StringComparison.Ordinal);
        Assert.Contains("'Content/badge?1.txt' (SDK input outside the project)", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-fixture.txt", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\t", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_ReportsAspub003ForUnknownPhase()
    {
        var engine = new RecordingBuildEngine();
        var task = CreateTask(engine, "other");

        Assert.False(task.Execute());
        AssertCode(engine, "ASPUB003");
    }

    [UnixTheory]
    [InlineData("0644", true)]
    [InlineData("0755", true)]
    [InlineData("0666", false)]
    [InlineData("0700", true)]
    [InlineData("2000", false)]
    [InlineData("4000", false)]
    [InlineData("1000", false)]
    public void Permissions_ValidatesFileModes(string mode, bool expected)
    {
        var directory = NewPublishDirectory("mode");
        var script = CreateFindStub("d\0755\0\0f\0" + mode + "\0asset.bin\0");
        var engine = new RecordingBuildEngine();
        var task = CreateTask(engine, "permissions", script);
        task.PublishDirectory = directory;

        Assert.Equal(expected, task.Execute());
        if (expected)
            Assert.Empty(engine.Errors);
        else
            AssertCode(engine, "ASPUB002");
    }

    [Fact]
    public void Permissions_ReportsAspub003WhenPublishDirectoryIsMissing()
    {
        var engine = new RecordingBuildEngine();
        var task = CreateTask(engine, "permissions", CreateFindStub(""));

        Assert.False(task.Execute());
        AssertCode(engine, "ASPUB003");
    }

    [UnixFact]
    public void Permissions_ReportsAspub003WhenPublishDirectoryDoesNotExist()
    {
        var engine = new RecordingBuildEngine();
        var task = CreateTask(engine, "permissions", CreateFindStub(""));
        task.PublishDirectory = Path.Join(_root, "absent");

        Assert.False(task.Execute());
        AssertCode(engine, "ASPUB003");
    }

    [UnixFact]
    public void Permissions_RejectsASymlinkAsTheOutputRoot()
    {
        var realDirectory = NewPublishDirectory("real-root");
        var linkedDirectory = Path.Join(_root, "linked-root");
        Directory.CreateSymbolicLink(linkedDirectory, realDirectory);
        var engine = new RecordingBuildEngine();
        var task = CreateTask(engine, "permissions", CreateFindStub(""));
        task.PublishDirectory = linkedDirectory;

        Assert.False(task.Execute());
        AssertCode(engine, "ASPUB003");
    }

    [UnixFact]
    public void Permissions_RejectsNestedSymlinkRecord()
    {
        var directory = NewPublishDirectory("nested-link");
        File.CreateSymbolicLink(Path.Join(directory, "link"), "missing-target");
        var engine = new RecordingBuildEngine();
        var task = CreateTask(engine, "permissions", CreateFindStub("d\0755\0\0l\0777\0link\0"));
        task.PublishDirectory = directory;

        Assert.False(task.Execute());
        AssertCode(engine, "ASPUB003");
        Assert.Contains(engine.Errors, error => error.Message?.Contains("unexpected file type", StringComparison.Ordinal) is true);
    }

    [UnixTheory]
    [InlineData("d\0755\0\0f\0888\0bad\0", "invalid mode")]
    [InlineData("d\0755\0\0f\0/644\0bad\0", "invalid octal digit")]
    [InlineData("d\0755\0\0f\0\0bad\0", "missing mode")]
    [InlineData("d\0755\0\0f\000644\0bad\0", "oversized mode")]
    [InlineData("d\0755\0\0f\0", "truncated mode")]
    [InlineData("d\0755\0\0f\0644\0", "incomplete record")]
    [InlineData("d\0755\0\0", "no files")]
    [InlineData("f\0755\0root\0f\0644\0asset\0", "missing root record")]
    public void Permissions_ReportsAspub003ForInvalidOrIncompleteFindOutput(string output, string _)
    {
        var directory = NewPublishDirectory("bad-output");
        var engine = new RecordingBuildEngine();
        var task = CreateTask(engine, "permissions", CreateFindStub(output));
        task.PublishDirectory = directory;

        Assert.False(task.Execute());
        AssertCode(engine, "ASPUB003");
    }

    [UnixFact]
    public void Permissions_ReportsAspub003WhenFindExitsUnsuccessfully()
    {
        var directory = NewPublishDirectory("find-exit");
        var engine = new RecordingBuildEngine();
        var task = CreateTask(engine, "permissions", CreateFindStub("d\0755\0\0f\0644\0asset\0", exitCode: 2));
        task.PublishDirectory = directory;

        Assert.False(task.Execute());
        AssertCode(engine, "ASPUB003");
    }

    [UnixFact]
    public void Permissions_ReportsAspub003WhenFindWritesStderr()
    {
        var directory = NewPublishDirectory("find-stderr");
        var engine = new RecordingBuildEngine();
        var task = CreateTask(engine, "permissions", CreateFindStub("d\0755\0\0f\0644\0asset\0", stderr: "synthetic failure"));
        task.PublishDirectory = directory;

        Assert.False(task.Execute());
        AssertCode(engine, "ASPUB003");
    }

    [UnixFact]
    public void Permissions_ReportsAspub003WhenFindCannotStart()
    {
        var directory = NewPublishDirectory("find-missing");
        var engine = new RecordingBuildEngine();
        var task = CreateTask(engine, "permissions", Path.Join(_root, "missing-find"));
        task.PublishDirectory = directory;

        Assert.False(task.Execute());
        AssertCode(engine, "ASPUB003");
    }

    private AppSurfacePublishPreflightTask CreateTask(RecordingBuildEngine engine, string phase, string findExecutable = "find") =>
        new(findExecutable)
        {
            BuildEngine = engine,
            Phase = phase,
            RepositoryDirectory = _root,
            ProjectDirectory = _root
        };

    private static TaskItem Item(string source, string destination, string metadata = "RelativePath")
    {
        var item = new TaskItem(Path.GetFullPath(source));
        item.SetMetadata(metadata, destination);
        return item;
    }

    private string NewPublishDirectory(string name)
    {
        var directory = Path.Join(_root, name);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private string CreateFindStub(string records, int exitCode = 0, string stderr = "")
    {
        var script = Path.Join(_root, "fake-find-" + Guid.NewGuid().ToString("N"));
        var output = ToShellOctal(records);
        var error = ToShellOctal(stderr);
        File.WriteAllText(script, $"#!/bin/sh\nprintf '{output}'\nprintf '{error}' >&2\nexit {exitCode}\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    private static string ToShellOctal(string value) => string.Concat(value.Select(character => character == '\0' ? "\\000" : character.ToString()));

    private static void AssertCode(RecordingBuildEngine engine, string code) =>
        Assert.Contains(engine.Errors, error => error.Code == code);

    // xUnit v2 discovers Skip on the attribute; it does not support exception-based skips.
    public sealed class UnixFactAttribute : FactAttribute
    {
        public UnixFactAttribute()
        {
            if (OperatingSystem.IsWindows())
                Skip = "Requires a Unix shell or symbolic links.";
        }
    }

    public sealed class UnixTheoryAttribute : TheoryAttribute
    {
        public UnixTheoryAttribute()
        {
            if (OperatingSystem.IsWindows())
                Skip = "Requires a Unix shell.";
        }
    }

    private sealed class RecordingBuildEngine : IBuildEngine
    {
        public List<BuildErrorEventArgs> Errors { get; } = [];
        public List<BuildWarningEventArgs> Warnings { get; } = [];
        public List<BuildMessageEventArgs> Messages { get; } = [];
        public bool ContinueOnError => false;
        public int LineNumberOfTaskNode => 0;
        public int ColumnNumberOfTaskNode => 0;
        public string ProjectFileOfTaskNode => string.Empty;
        public void LogErrorEvent(BuildErrorEventArgs e) => Errors.Add(e);
        public void LogWarningEvent(BuildWarningEventArgs e) => Warnings.Add(e);
        public void LogMessageEvent(BuildMessageEventArgs e) => Messages.Add(e);
        public void LogCustomEvent(CustomBuildEventArgs e) { }
        public bool BuildProjectFile(string projectFileName, string[] targetNames, IDictionary globalProperties, IDictionary targetOutputs) => true;
    }
}
