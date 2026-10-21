using System.Diagnostics;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactOperationLeaseProcessTests
{
    [Fact]
    public async Task NativeLeases_CoordinateProcessesAndReleaseOnDisposeOrTermination()
    {
        using var tree = LeaseTree.Create();

        await using var firstShared = await LeaseProcess.StartHolderAsync(tree.Output, "shared");
        await using var secondShared = await LeaseProcess.StartHolderAsync(tree.Output, "shared");

        Assert.Equal("busy", await LeaseProcess.TryAcquireAsync(tree.Output, "exclusive"));
        await firstShared.ReleaseAsync();
        Assert.Equal("busy", await LeaseProcess.TryAcquireAsync(tree.Output, "exclusive"));

        await secondShared.TerminateAsync();
        Assert.Equal("acquired", await LeaseProcess.TryAcquireAsync(tree.Output, "exclusive"));

        await using var exclusive = await LeaseProcess.StartHolderAsync(tree.Output, "exclusive");
        Assert.Equal("busy", await LeaseProcess.TryAcquireAsync(tree.Output, "shared"));

        await exclusive.TerminateAsync();
        Assert.Equal("acquired", await LeaseProcess.TryAcquireAsync(tree.Output, "shared"));
    }

    private sealed class LeaseTree : IDisposable
    {
        private LeaseTree(string root) => Root = root;

        internal string Root { get; }
        internal string Output => Path.Join(Root, "output");

        internal static LeaseTree Create()
        {
            var root = Path.Join(Path.GetTempPath(), "coverage-operation-lease-process-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Join(root, "output"));
            return new LeaseTree(root);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class LeaseProcess : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _standardError;

        private LeaseProcess(Process process)
        {
            _process = process;
            _standardError = process.StandardError.ReadToEndAsync();
        }

        internal static async Task<LeaseProcess> StartHolderAsync(string directory, string mode)
        {
            var holder = new LeaseProcess(Start("hold", directory, mode));
            try
            {
                await holder.WaitUntilAcquiredAsync();
                return holder;
            }
            catch
            {
                await holder.DisposeAsync();
                throw;
            }
        }

        internal async Task WaitUntilAcquiredAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var line = await _process.StandardOutput.ReadLineAsync(timeout.Token);
            if (line != "acquired")
            {
                var details = _process.HasExited ? await _standardError : "holder has not exited";
                Assert.Fail($"Lease probe did not acquire the lease: {line ?? "process exited"}. {details}");
            }
        }

        internal async Task ReleaseAsync()
        {
            await _process.StandardInput.WriteLineAsync("release");
            await WaitForExitAsync();
        }

        internal async Task TerminateAsync()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }

            await WaitForExitAsync(expectSuccess: false);
        }

        internal static async Task<string> TryAcquireAsync(string directory, string mode)
        {
            using var process = Start("try", directory, mode);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var error = await process.StandardError.ReadToEndAsync(timeout.Token);
            Assert.True(process.ExitCode is 0 or 2, $"Lease probe failed with exit code {process.ExitCode}: {error}");
            Assert.Equal(process.ExitCode == 0 ? "acquired" : "busy", output.Trim());
            return output.Trim();
        }

        private async Task WaitForExitAsync(bool expectSuccess = true)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await _process.WaitForExitAsync(timeout.Token);
            var error = await _standardError.WaitAsync(timeout.Token);
            if (expectSuccess)
            {
                Assert.True(_process.ExitCode == 0, $"Lease holder failed with exit code {_process.ExitCode}: {error}");
            }
        }

        private static Process Start(string command, string directory, string mode)
        {
            var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .Parent?.Name ?? "Debug";
            var probeAssembly = Path.Join(
                FindRepositoryRoot(),
                "tests",
                "ForgeTrust.AppSurface.CoverageLeaseProbe",
                "bin",
                configuration,
                "net10.0",
                "ForgeTrust.AppSurface.CoverageLeaseProbe.dll");
            Assert.True(File.Exists(probeAssembly), $"The coverage lease probe was not built: {probeAssembly}");

            var startInfo = new ProcessStartInfo("dotnet")
            {
                RedirectStandardInput = command == "hold",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add(probeAssembly);
            startInfo.ArgumentList.Add(command);
            startInfo.ArgumentList.Add(directory);
            startInfo.ArgumentList.Add(mode);
            return Process.Start(startInfo)
                ?? throw new InvalidOperationException("The coverage lease probe process could not start.");
        }

        private static string FindRepositoryRoot()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Join(directory.FullName, "Cli", "ForgeTrust.AppSurface.Cli.Tests", "ForgeTrust.AppSurface.Cli.Tests.csproj")))
                {
                    return directory.FullName;
                }
            }

            throw new DirectoryNotFoundException("Could not locate the repository root for the coverage lease probe.");
        }

        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await WaitForExitAsync(expectSuccess: false);
            }

            _process.Dispose();
        }
    }
}
