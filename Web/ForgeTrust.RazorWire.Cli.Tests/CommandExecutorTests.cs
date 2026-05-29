using FakeItEasy;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.RazorWire.Cli.Tests;

public class CommandExecutorTests
{
    [Fact]
    public async Task ExecuteCommandAsync_Should_Return_Captured_Output_For_Success()
    {
        var result = await CreateExecutor().ExecuteCommandAsync(
            "dotnet",
            ["--version"],
            Directory.GetCurrentDirectory(),
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Stdout));
    }

    [Fact]
    public async Task ExecuteCommandAsync_Should_Return_NonZero_Result_Without_Throwing()
    {
        var result = await CreateExecutor().ExecuteCommandAsync(
            "dotnet",
            ["definitely-not-a-dotnet-command-for-razorwire-tests"],
            Directory.GetCurrentDirectory(),
            CancellationToken.None);

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Stderr));
    }

    [Fact]
    public async Task ExecuteCommandAsync_Should_Return_Result_When_Executable_Is_Missing()
    {
        var result = await CreateExecutor().ExecuteCommandAsync(
            "definitely-missing-razorwire-test-executable",
            [],
            Directory.GetCurrentDirectory(),
            CancellationToken.None);

        Assert.Equal(-1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("Failed to start process", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteCommandAsync_Should_Return_Result_When_WorkingDirectory_Is_Missing()
    {
        var missingDirectory = Path.Combine(Path.GetTempPath(), $"razorwire-missing-{Guid.NewGuid():N}");

        var result = await CreateExecutor().ExecuteCommandAsync(
            "dotnet",
            ["--version"],
            missingDirectory,
            CancellationToken.None);

        Assert.Equal(-1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("directory", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteCommandAsync_Should_Preserve_Tokenized_Arguments()
    {
        using var project = TestConsoleProject.Create(
            """
            Console.WriteLine(string.Join("|", args));
            """);

        var result = await CreateExecutor().ExecuteCommandAsync(
            "dotnet",
            ["run", "--project", project.ProjectPath, "--", "hello world", "semi;colon"],
            project.DirectoryPath,
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello world|semi;colon", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteCommandAsync_Should_Inherit_Parent_Environment()
    {
        var variableName = $"RAZORWIRE_COMMAND_EXECUTOR_{Guid.NewGuid():N}";
        var variableValue = $"value-{Guid.NewGuid():N}";
        using var project = TestConsoleProject.Create(
            $$"""
            Console.WriteLine(Environment.GetEnvironmentVariable("{{variableName}}") ?? "missing");
            """);

        var originalValue = Environment.GetEnvironmentVariable(variableName);
        Environment.SetEnvironmentVariable(variableName, variableValue);
        try
        {
            var result = await CreateExecutor().ExecuteCommandAsync(
                "dotnet",
                ["run", "--project", project.ProjectPath],
                project.DirectoryPath,
                CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains(variableValue, result.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, originalValue);
        }
    }

    [Fact]
    public async Task ExecuteCommandAsync_Should_Propagate_Cancellation()
    {
        using var project = TestConsoleProject.Create(
            """
            await Task.Delay(TimeSpan.FromSeconds(30));
            """);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await CreateExecutor().ExecuteCommandAsync(
                "dotnet",
                ["run", "--project", project.ProjectPath],
                project.DirectoryPath,
                cancellation.Token));
    }

    private static CommandExecutor CreateExecutor()
    {
        return new CommandExecutor(A.Fake<ILogger<CommandExecutor>>());
    }

    private sealed class TestConsoleProject : IDisposable
    {
        private TestConsoleProject(string directoryPath, string projectPath)
        {
            DirectoryPath = directoryPath;
            ProjectPath = projectPath;
        }

        public string DirectoryPath { get; }

        public string ProjectPath { get; }

        public static TestConsoleProject Create(string programBody)
        {
            var directory = Path.Combine(Path.GetTempPath(), $"razorwire-cli-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var projectPath = Path.Combine(directory, "Helper.csproj");
            File.WriteAllText(
                projectPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(directory, "Program.cs"), programBody);

            return new TestConsoleProject(directory, projectPath);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
