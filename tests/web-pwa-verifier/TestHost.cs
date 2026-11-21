using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using ForgeTrust.AppSurface.Testing;

internal sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);

internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = CanonicalPath(Directory.CreateTempSubdirectory("pwa-contract-"));

    // File-based restores can lose transitive project references if the SDK sees both
    // a symlinked temp directory and its physical path (for example /var on macOS).
    private static string CanonicalPath(DirectoryInfo directory)
    {
        if (directory.ResolveLinkTarget(returnFinalTarget: true) is DirectoryInfo target)
        {
            return CanonicalPath(target);
        }
        return directory.Parent is { } parent
            ? TestPathUtils.PathUnder(CanonicalPath(parent), directory.Name)
            : directory.FullName;
    }

    public void Dispose() => Directory.Delete(Path, recursive: true);
}

internal static class TestHost
{
    public static string Root { get; } = GetRepositoryRoot();
    public static string Dotnet { get; } = Environment.GetEnvironmentVariable("DOTNET_EXE")
        ?? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";

    private static string GetRepositoryRoot([CallerFilePath] string source = "")
    {
        var directory = AppContext.GetData("EntryPointFileDirectoryPath") as string ?? Path.GetDirectoryName(source)!;
        return TestPathUtils.FindRepoRoot(directory);
    }

    public static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    // Use the checkout's inputs, including pending edits. Ignored build outputs can contain
    // references to the original worktree and would invalidate the isolation checks.
    public static async Task CopyCheckoutAsync(string destination)
    {
        var listFiles = Command("git", Root, "ls-files", "--cached", "--others", "--exclude-standard", "-z");
        listFiles.StandardOutputEncoding = Encoding.UTF8;
        var listed = await RunAsync(listFiles, TimeSpan.FromMinutes(1));
        Check(listed.ExitCode == 0, $"git ls-files failed: {listed.StandardError}");
        foreach (var relative in listed.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var source = TestPathUtils.PathUnder(Root, relative);
            if (File.Exists(source))
            {
                var target = TestPathUtils.PathUnder(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, overwrite: true);
            }
        }
    }

    public static ProcessStartInfo Command(string executable, string cwd, params string[] args)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in args)
        {
            start.ArgumentList.Add(argument);
        }
        if (Path.IsPathFullyQualified(Dotnet))
        {
            // The command under test also launches dotnet by name.
            start.Environment["PATH"] = Path.GetDirectoryName(Dotnet) + Path.PathSeparator + start.Environment["PATH"];
        }
        return start;
    }

    public static async Task<CommandResult> RunAsync(ProcessStartInfo start, TimeSpan timeout)
    {
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {start.FileName}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(), output, error).WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            throw new TimeoutException($"{start.FileName} exceeded {timeout} in {start.WorkingDirectory}.");
        }
        return new(process.ExitCode, await output, await error);
    }
}
