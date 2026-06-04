using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.CoverageRunner.Tests;

internal sealed class TestRepo : IDisposable
{
    private readonly List<string> _projects = [];

    private TestRepo(string root)
    {
        Root = root;
        File.WriteAllText(Path.Join(root, "ForgeTrust.AppSurface.slnx"), "<Solution />");
    }

    public string Root { get; }

    public IReadOnlyList<string> Projects => _projects;

    public static TestRepo Create()
    {
        var root = Path.Join(Path.GetTempPath(), "coverage-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new TestRepo(root);
    }

    public void AddProject(string relativePath, string contents = "<Project />")
    {
        var fullPath = TestPathUtils.PathUnder(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, contents);
        _projects.Add(relativePath);
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
