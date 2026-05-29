using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace ForgeTrust.RazorWire.Cli.Tests;

// Raw Process instances are intentional here because TargetAppProcess is the production process wrapper under test.
public class TargetAppProcessTests
{
    [Fact]
    public void Constructor_Should_Throw_For_Null_Spec()
    {
        Assert.Throws<ArgumentNullException>(() => new TargetAppProcess(null!));
    }

    [Fact]
    public void HasExited_Should_Be_True_Before_Start()
    {
        var process = new TargetAppProcess(new ProcessLaunchSpec
        {
            FileName = "dotnet",
            Arguments = ["--version"],
            WorkingDirectory = Directory.GetCurrentDirectory()
        });

        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task Start_Should_Throw_When_Called_Twice()
    {
        await using var process = new TargetAppProcess(
            new ProcessLaunchSpec
            {
                FileName = "dotnet",
                Arguments = ["--version"],
                WorkingDirectory = Directory.GetCurrentDirectory()
            },
            new TargetAppProcessHooks
            {
                StartOverride = _ => { },
                HasExitedOverride = _ => true
            },
            process: new Process(),
            started: false);

        process.Start();

        var ex = Assert.Throws<InvalidOperationException>(process.Start);
        Assert.Contains("already been started", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Start_Should_Throw_After_Dispose()
    {
        var process = new TargetAppProcess(new ProcessLaunchSpec
        {
            FileName = "dotnet",
            Arguments = ["--version"],
            WorkingDirectory = Directory.GetCurrentDirectory()
        });
        await process.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(process.Start);
    }

    [Fact]
    public void Constructor_Should_Inherit_Parent_Environment_And_Apply_Overrides()
    {
        const string inheritedKey = "PATH";
        const string overriddenKey = "DOTNET_ENVIRONMENT";
        var inheritedValue = Environment.GetEnvironmentVariable(inheritedKey);
        Assert.False(string.IsNullOrWhiteSpace(inheritedValue));
        var rawProcess = new Process();

        _ = new TargetAppProcess(
            new ProcessLaunchSpec
            {
                FileName = "dotnet",
                Arguments = ["--version"],
                WorkingDirectory = Directory.GetCurrentDirectory(),
                EnvironmentOverrides = new Dictionary<string, string>
                {
                    [overriddenKey] = "Production"
                }
            },
            hooks: null,
            process: rawProcess,
            started: false);

        Assert.Equal(inheritedValue, rawProcess.StartInfo.Environment[inheritedKey]);
        Assert.Equal("Production", rawProcess.StartInfo.Environment[overriddenKey]);
    }

    [Fact]
    public async Task Start_And_DisposeAsync_Should_Work_For_Real_Process()
    {
        var outputLines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var errorLines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var outputReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exitedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var process = new TargetAppProcess(new ProcessLaunchSpec
        {
            FileName = "dotnet",
            Arguments = ["--version"],
            WorkingDirectory = Directory.GetCurrentDirectory()
        });

        process.OutputLineReceived += line =>
        {
            outputLines.Enqueue(line);
            outputReceived.TrySetResult(line);
        };
        process.ErrorLineReceived += line => errorLines.Enqueue(line);
        process.Exited += () =>
        {
            exitedSignal.TrySetResult();
        };

        process.Start();

        var timeout = Task.Delay(TimeSpan.FromSeconds(10));
        var firstSignal = await Task.WhenAny(outputReceived.Task, exitedSignal.Task, timeout);
        Assert.NotSame(timeout, firstSignal);

        if (firstSignal == exitedSignal.Task && !outputReceived.Task.IsCompleted)
        {
            await Task.WhenAny(outputReceived.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        }

        if (!exitedSignal.Task.IsCompleted)
        {
            var exitTimeout = Task.Delay(TimeSpan.FromSeconds(10));
            var exitSignal = await Task.WhenAny(exitedSignal.Task, exitTimeout);
            Assert.NotSame(exitTimeout, exitSignal);
        }

        await process.DisposeAsync();

        Assert.True(exitedSignal.Task.IsCompleted);
        Assert.Empty(errorLines);
        Assert.True(outputReceived.Task.IsCompleted, "Expected at least one stdout line from 'dotnet --version'.");
        Assert.NotEmpty(outputLines);
    }

    [Fact]
    public async Task Start_Should_Surface_StartupFailure_As_ErrorLine_Then_Exited()
    {
        var errorReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exitedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var process = new TargetAppProcess(new ProcessLaunchSpec
        {
            FileName = "definitely-missing-razorwire-target-app",
            Arguments = [],
            WorkingDirectory = Directory.GetCurrentDirectory()
        });

        process.ErrorLineReceived += line => errorReceived.TrySetResult(line);
        process.Exited += () => exitedSignal.TrySetResult();

        process.Start();

        var timeout = Task.Delay(TimeSpan.FromSeconds(5));
        var errorSignal = await Task.WhenAny(errorReceived.Task, timeout);
        Assert.NotSame(timeout, errorSignal);
        var exitSignal = await Task.WhenAny(exitedSignal.Task, timeout);
        Assert.NotSame(timeout, exitSignal);

        Assert.Contains("failed", await errorReceived.Task, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DisposeAsync_Should_Cancel_Running_CliWrap_Process()
    {
        using var project = TestConsoleProject.Create(
            """
            Console.WriteLine("ready");
            await Task.Delay(TimeSpan.FromSeconds(30));
            """);
        var outputReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exitedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var process = new TargetAppProcess(new ProcessLaunchSpec
        {
            FileName = "dotnet",
            Arguments = ["run", "--project", project.ProjectPath],
            WorkingDirectory = project.DirectoryPath
        });
        process.OutputLineReceived += line => outputReceived.TrySetResult(line);
        process.Exited += () => exitedSignal.TrySetResult();
        process.Start();

        var startupSignal = await Task.WhenAny(outputReceived.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(outputReceived.Task, startupSignal);

        var exception = await Record.ExceptionAsync(async () => await process.DisposeAsync());

        Assert.Null(exception);
        var exitSignal = await Task.WhenAny(exitedSignal.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(exitedSignal.Task, exitSignal);
        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task ReadProcessLinesAsync_Should_Handle_Line_Endings_And_Final_Unterminated_Line()
    {
        var lines = new List<string>();
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("alpha\r\nbeta\n   \nfinal"));

        await TargetAppProcess.ReadProcessLinesAsync(stream, lines.Add, CancellationToken.None);

        Assert.Equal(["alpha", "beta", "final"], lines);
    }

    [Fact]
    public async Task Start_ShouldHandle_ExitBeforeOutput_Deterministically()
    {
        var outputLines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var errorLines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var outputReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exitedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var process = new TargetAppProcess(
            new ProcessLaunchSpec
            {
                FileName = "dotnet",
                Arguments = ["--version"],
                WorkingDirectory = Directory.GetCurrentDirectory()
            },
            new TargetAppProcessHooks
            {
                StartOverride = target =>
                {
                    target.RaiseExitedForTesting();
                    Task.Run(
                        async () =>
                        {
                            await Task.Yield();
                            target.RaiseOutputLineForTesting("9.9.9-test");
                        });
                },
                HasExitedOverride = _ => true
            },
            process: new Process(),
            started: false);

        process.OutputLineReceived += line =>
        {
            outputLines.Enqueue(line);
            outputReceived.TrySetResult(line);
        };
        process.ErrorLineReceived += line => errorLines.Enqueue(line);
        process.Exited += () => exitedSignal.TrySetResult();

        process.Start();

        var timeout = Task.Delay(TimeSpan.FromSeconds(2));
        var firstSignal = await Task.WhenAny(exitedSignal.Task, outputReceived.Task, timeout);
        Assert.NotSame(timeout, firstSignal);
        Assert.Same(exitedSignal.Task, firstSignal);

        var outputTimeout = Task.Delay(TimeSpan.FromSeconds(2));
        var outputSignal = await Task.WhenAny(outputReceived.Task, outputTimeout);
        Assert.NotSame(outputTimeout, outputSignal);

        await process.DisposeAsync();

        Assert.True(exitedSignal.Task.IsCompleted);
        Assert.Empty(errorLines);
        Assert.Equal("9.9.9-test", await outputReceived.Task);
        Assert.NotEmpty(outputLines);
    }

    [Fact]
    public async Task DisposeAsync_Should_Not_Throw_When_Not_Started()
    {
        await using var process = new TargetAppProcess(new ProcessLaunchSpec
        {
            FileName = "dotnet",
            Arguments = ["--version"],
            WorkingDirectory = Directory.GetCurrentDirectory()
        });

        await process.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_Should_Swallow_Kill_Failures_During_BestEffort_Cleanup()
    {
        var waitForExitAsyncCallCount = 0;
        var waitForExitCallCount = 0;

        await using var process = new TargetAppProcess(
            new ProcessLaunchSpec
            {
                FileName = "dotnet",
                Arguments = ["--version"],
                WorkingDirectory = Directory.GetCurrentDirectory()
            },
            new TargetAppProcessHooks
            {
                HasExitedOverride = _ => false,
                KillProcessOverride = _ => throw new Win32Exception("simulated kill failure"),
                WaitForExitAsyncOverride = (_, _) =>
                {
                    waitForExitAsyncCallCount++;
                    return Task.CompletedTask;
                },
                WaitForExitOverride = _ => waitForExitCallCount++
            },
            process: new Process(),
            started: true);

        var exception = await Record.ExceptionAsync(async () => await process.DisposeAsync());

        Assert.Null(exception);
        Assert.Equal(1, waitForExitAsyncCallCount);
        Assert.Equal(1, waitForExitCallCount);
    }

    [Fact]
    public async Task DisposeAsync_Should_UseWaitHooks_WhenCleanupObservesExit()
    {
        var waitForExitAsyncCallCount = 0;
        var waitForExitCallCount = 0;

        await using var process = new TargetAppProcess(
            new ProcessLaunchSpec
            {
                FileName = "dotnet",
                Arguments = ["--version"],
                WorkingDirectory = Directory.GetCurrentDirectory()
            },
            new TargetAppProcessHooks
            {
                HasExitedOverride = _ => false,
                KillProcessOverride = _ => { },
                WaitForExitAsyncOverride = (_, cancellationToken) =>
                {
                    waitForExitAsyncCallCount++;
                    Assert.False(cancellationToken.IsCancellationRequested);
                    return Task.CompletedTask;
                },
                WaitForExitOverride = _ => waitForExitCallCount++
            },
            process: new Process(),
            started: true);

        var exception = await Record.ExceptionAsync(async () => await process.DisposeAsync());

        Assert.Null(exception);
        Assert.Equal(1, waitForExitAsyncCallCount);
        Assert.Equal(1, waitForExitCallCount);
    }

    [Fact]
    public async Task DisposeAsync_Should_Swallow_Timeout_And_Skip_Final_Flush()
    {
        var waitForExitAsyncCallCount = 0;
        var waitForExitCallCount = 0;

        await using var process = new TargetAppProcess(
            new ProcessLaunchSpec
            {
                FileName = "dotnet",
                Arguments = ["--version"],
                WorkingDirectory = Directory.GetCurrentDirectory()
            },
            new TargetAppProcessHooks
            {
                HasExitedOverride = _ => false,
                KillProcessOverride = _ => { },
                WaitForExitAsyncOverride = (_, cancellationToken) =>
                {
                    waitForExitAsyncCallCount++;
                    throw new OperationCanceledException(cancellationToken);
                },
                WaitForExitOverride = _ => waitForExitCallCount++
            },
            process: new Process(),
            started: true);

        var exception = await Record.ExceptionAsync(async () => await process.DisposeAsync());

        Assert.Null(exception);
        Assert.Equal(1, waitForExitAsyncCallCount);
        Assert.Equal(0, waitForExitCallCount);
    }

    [Fact]
    public async Task DisposeAsync_ShouldTreat_ObjectDisposedExitProbe_As_ObservedExit()
    {
        var waitForExitAsyncCallCount = 0;
        var waitForExitCallCount = 0;

        await using var process = new TargetAppProcess(
            new ProcessLaunchSpec
            {
                FileName = "dotnet",
                Arguments = ["--version"],
                WorkingDirectory = Directory.GetCurrentDirectory()
            },
            new TargetAppProcessHooks
            {
                HasExitedOverride = _ => throw new ObjectDisposedException(nameof(Process)),
                WaitForExitAsyncOverride = (_, _) =>
                {
                    waitForExitAsyncCallCount++;
                    return Task.CompletedTask;
                },
                WaitForExitOverride = _ => waitForExitCallCount++
            },
            process: new Process(),
            started: true);

        var exception = await Record.ExceptionAsync(async () => await process.DisposeAsync());

        Assert.Null(exception);
        Assert.Equal(0, waitForExitAsyncCallCount);
        Assert.Equal(1, waitForExitCallCount);
    }

    [Fact]
    public async Task Constructor_ShouldAllow_InjectedStartedProcess_WithoutMutatingLaunchConfiguration()
    {
        var spec = new ProcessLaunchSpec
        {
            FileName = "dotnet",
            Arguments = ["--info"],
            WorkingDirectory = Directory.GetCurrentDirectory()
        };

        using var associatedProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = Directory.GetCurrentDirectory(),
                UseShellExecute = false
            }
        };
        associatedProcess.StartInfo.ArgumentList.Add("--version");
        associatedProcess.Start();

        await using var process = new TargetAppProcess(
            spec,
            hooks: null,
            process: associatedProcess,
            started: true);

        Assert.Contains("--version", associatedProcess.StartInfo.ArgumentList);
        Assert.DoesNotContain("--info", associatedProcess.StartInfo.ArgumentList);
        Assert.Equal(["--info"], spec.Arguments);

        var exception = await Record.ExceptionAsync(async () => await process.DisposeAsync());

        Assert.Null(exception);
    }

    [Fact]
    public void Factory_Should_Create_TargetProcess()
    {
        var factory = new TargetAppProcessFactory();
        var process = factory.Create(new ProcessLaunchSpec
        {
            FileName = "dotnet",
            Arguments = ["--version"],
            WorkingDirectory = Directory.GetCurrentDirectory()
        });

        Assert.IsType<TargetAppProcess>(process);
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
            var directory = Path.Combine(Path.GetTempPath(), $"razorwire-target-app-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var projectPath = Path.Combine(directory, "TargetHelper.csproj");
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
