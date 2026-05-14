using CliFx.Exceptions;

namespace ForgeTrust.RazorWire.Cli.Tests;

public class ExportSourceRequestFactoryTests
{
    private readonly ExportSourceRequestFactory _sut = new();

    [Fact]
    public void Create_Should_Throw_When_No_Source_Is_Provided()
    {
        var ex = Assert.Throws<CommandException>(() => _sut.Create(null, null, null, null, [], false));

        Assert.Contains("exactly one source", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("razorwire export --help", ex.Message, StringComparison.Ordinal);
        Assert.Contains("--project ./MyApp.csproj", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_Should_Throw_When_Multiple_Sources_Are_Provided()
    {
        var ex = Assert.Throws<CommandException>(() => _sut.Create("http://localhost:5000", "app.csproj", null, null, [], false));

        Assert.Contains("mutually exclusive", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Choose one source", ex.Message, StringComparison.Ordinal);
        Assert.Contains("razorwire export --help", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_Should_Throw_When_Url_Is_Invalid()
    {
        var ex = Assert.Throws<CommandException>(() => _sut.Create("not-a-url", null, null, null, [], false));
        Assert.Contains("--url", ex.Message, StringComparison.Ordinal);
        Assert.Contains("razorwire export --help", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_Should_Throw_When_Project_File_Is_Missing()
    {
        var ex = Assert.Throws<CommandException>(() => _sut.Create(null, "missing.csproj", null, null, [], false));

        Assert.Contains("--project file not found", ex.Message, StringComparison.Ordinal);
        Assert.Contains("razorwire export --help", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_Should_Throw_When_Dll_File_Is_Missing()
    {
        var ex = Assert.Throws<CommandException>(() => _sut.Create(null, null, "missing.dll", null, [], false));

        Assert.Contains("--dll file not found", ex.Message, StringComparison.Ordinal);
        Assert.Contains("razorwire export --help", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_Should_Throw_When_Project_File_Extension_Is_Invalid()
    {
        var ex = Assert.Throws<CommandException>(() => _sut.Create(null, "app.txt", null, null, [], false));
        Assert.Contains("--project must point to a .csproj file.", ex.Message, StringComparison.Ordinal);
        Assert.Contains("razorwire export --help", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_Should_Throw_When_Dll_File_Extension_Is_Invalid()
    {
        var ex = Assert.Throws<CommandException>(() => _sut.Create(null, null, "app.txt", null, [], false));
        Assert.Contains("--dll must point to a .dll file.", ex.Message, StringComparison.Ordinal);
        Assert.Contains("razorwire export --help", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_Should_Create_Project_Request_When_Project_Path_Exists()
    {
        var tempPath = Path.GetTempFileName();
        var projectPath = Path.ChangeExtension(tempPath, ".csproj");
        File.Move(tempPath, projectPath);
        try
        {
            var request = _sut.Create(null, projectPath, null, "net9.0", ["--foo", "bar"], true);

            Assert.Equal(ExportSourceKind.Project, request.SourceKind);
            Assert.Equal(Path.GetFullPath(projectPath), request.SourceValue);
            Assert.Equal("net9.0", request.Framework);
            Assert.Equal(["--foo", "bar"], request.AppArgs);
            Assert.True(request.NoBuild);
        }
        finally
        {
            if (File.Exists(projectPath))
            {
                File.Delete(projectPath);
            }
        }
    }

    [Fact]
    public void Create_Should_Create_Dll_Request_When_Dll_Path_Exists()
    {
        var tempPath = Path.GetTempFileName();
        var dllPath = Path.ChangeExtension(tempPath, ".dll");
        File.Move(tempPath, dllPath);
        try
        {
            var request = _sut.Create(null, null, dllPath, "net9.0", ["--foo", "bar"], false);

            Assert.Equal(ExportSourceKind.Dll, request.SourceKind);
            Assert.Equal(Path.GetFullPath(dllPath), request.SourceValue);
            Assert.Equal("net9.0", request.Framework);
            Assert.Equal(["--foo", "bar"], request.AppArgs);
            Assert.False(request.NoBuild);
        }
        finally
        {
            if (File.Exists(dllPath))
            {
                File.Delete(dllPath);
            }
        }
    }
}
