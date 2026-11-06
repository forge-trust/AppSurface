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
        var task = CreateTask(engine, "paths");
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
    public void Execute_ReportsAspub003ForUnknownPhase()
    {
        var engine = new RecordingBuildEngine();
        var task = CreateTask(engine, "other");

        Assert.False(task.Execute());
        AssertCode(engine, "ASPUB003");
    }

    [Theory]
    [InlineData("0644", true)]
    [InlineData("0755", true)]
    [InlineData("0666", false)]
    [InlineData("0700", true)]
    [InlineData("2000", false)]
    [InlineData("4000", false)]
    [InlineData("1000", false)]
    public void Permissions_ValidatesFileModes(string mode, bool expected)
    {
        SkipWindows();
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

    [Fact]
    public void Permissions_ReportsAspub003WhenPublishDirectoryDoesNotExist()
    {
        SkipWindows();
        var engine = new RecordingBuildEngine();
        var task = CreateTask(engine, "permissions", CreateFindStub(""));
        task.PublishDirectory = Path.Join(_root, "absent");

        Assert.False(task.Execute());
        AssertCode(engine, "ASPUB003");
    }

    [Fact]
    public void Permissions_RejectsASymlinkAsTheOutputRoot()
    {
        SkipWindows();
        var realDirectory = NewPublishDirectory("real-root");
        var linkedDirectory = Path.Join(_root, "linked-root");
        Directory.CreateSymbolicLink(linkedDirectory, realDirectory);
        var engine = new RecordingBuildEngine();
        var task = CreateTask(engine, "permissions", CreateFindStub(""));
        task.PublishDirectory = linkedDirectory;

        Assert.False(task.Execute());
        AssertCode(engine, "ASPUB003");
    }

    [Fact]
    public void Permissions_RejectsNestedSymlinkRecord()
    {
        SkipWindows();
        var directory = NewPublishDirectory("nested-link");
        File.CreateSymbolicLink(Path.Join(directory, "link"), "missing-target");
        var engine = new RecordingBuildEngine();
        var task = CreateTask(engine, "permissions", CreateFindStub("d\0755\0\0l\0777\0link\0"));
        task.PublishDirectory = directory;

        Assert.False(task.Execute());
        AssertCode(engine, "ASPUB003");
        Assert.Contains(engine.Errors, error => error.Message?.Contains("unexpected file type", StringComparison.Ordinal) is true);
    }

    [Theory]
    [InlineData("d\0755\0\0f\0888\0bad\0", "invalid mode")]
    [InlineData("d\0755\0\0f\0644\0", "incomplete record")]
    [InlineData("d\0755\0\0", "no files")]
    [InlineData("f\0755\0root\0f\0644\0asset\0", "missing root record")]
    public void Permissions_ReportsAspub003ForInvalidOrIncompleteFindOutput(string output, string _)
    {
        SkipWindows();
        var directory = NewPublishDirectory("bad-output");
        var engine = new RecordingBuildEngine();
        var task = CreateTask(engine, "permissions", CreateFindStub(output));
        task.PublishDirectory = directory;

        Assert.False(task.Execute());
        AssertCode(engine, "ASPUB003");
    }

    [Fact]
    public void Permissions_ReportsAspub003WhenFindExitsUnsuccessfully()
    {
        SkipWindows();
        var directory = NewPublishDirectory("find-exit");
        var engine = new RecordingBuildEngine();
        var task = CreateTask(engine, "permissions", CreateFindStub("d\0755\0\0f\0644\0asset\0", exitCode: 2));
        task.PublishDirectory = directory;

        Assert.False(task.Execute());
        AssertCode(engine, "ASPUB003");
    }

    [Fact]
    public void Permissions_ReportsAspub003WhenFindWritesStderr()
    {
        SkipWindows();
        var directory = NewPublishDirectory("find-stderr");
        var engine = new RecordingBuildEngine();
        var task = CreateTask(engine, "permissions", CreateFindStub("d\0755\0\0f\0644\0asset\0", stderr: "synthetic failure"));
        task.PublishDirectory = directory;

        Assert.False(task.Execute());
        AssertCode(engine, "ASPUB003");
    }

    [Fact]
    public void Permissions_ReportsAspub003WhenFindCannotStart()
    {
        SkipWindows();
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

    private static void SkipWindows()
    {
        if (OperatingSystem.IsWindows())
            throw Xunit.Sdk.SkipException.ForSkip("The synthetic executable uses a Unix shell; path validation remains platform independent.");
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
