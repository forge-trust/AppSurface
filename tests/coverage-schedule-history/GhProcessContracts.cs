using System.Diagnostics;

/// <summary>Checks the process adapter using this same test executable as a controlled child.</summary>
internal static class GhProcessContracts
{
    private const string Endpoint = "repos/owner/repo/actions/workflows/build.yml/runs?branch=release%2Fnext&event=push";
    private static readonly byte[] Response = [0, 13, 10, 128, 255];

    internal static async Task RunAsync()
    {
        var directory = Directory.CreateTempSubdirectory("coverage-gh-");
        try
        {
            static Process Child(string behavior)
            {
                var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
                {
                    ArgumentList =
                    {
                        typeof(GhProcessContracts).Assembly.Location, "--gh-fixture", behavior
                    }
                };
                start.Environment.Remove("GH_TOKEN");
                start.Environment.Remove("GITHUB_TOKEN");
                return new Process { StartInfo = start };
            }

            // Writing more than a pipe buffer to stderr before stdout verifies concurrent draining.
            var normalTimeout = TimeSpan.FromSeconds(20);
            if (!OperatingSystem.IsWindows())
            {
                await CheckPathLookupAsync(directory.FullName, normalTimeout);
            }
            using var binary = Child("binary");
            var bytes = await CoverageHistoryGh.RequestAsync(binary, Endpoint, Response.Length, normalTimeout);
            if (!bytes.SequenceEqual(Response))
            {
                throw new InvalidDataException("Binary gh stdout changed at the exact byte limit.");
            }

            using var nonzero = Child("nonzero");
            await ExpectFailureAsync(nonzero, Response.Length, normalTimeout, "gh api request failed");
            foreach (var (behavior, limit, timeout, reason) in new[]
            {
                ("over-limit", Response.Length - 1, normalTimeout, "size limit"),
                ("timeout", Response.Length, TimeSpan.FromSeconds(3), "time budget")
            })
            {
                using var child = Child(behavior);
                await ExpectFailureAsync(child, limit, timeout, reason);
                if (!child.HasExited)
                {
                    throw new InvalidDataException($"The {behavior} fixture was still running after its request returned.");
                }
            }

            using var missing = new Process
            {
                StartInfo = new ProcessStartInfo(Path.Join(directory.FullName, "absent-gh"))
            };
            await ExpectFailureAsync(missing, Response.Length, normalTimeout, "start gh");
            await ExpectFailureAsync(missing, Response.Length, TimeSpan.Zero, "time budget");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static async Task CheckPathLookupAsync(string root, TimeSpan timeout)
    {
        var pathDirectory = Directory.CreateDirectory(Path.Join(root, "path"));
        File.CreateSymbolicLink(Path.Join(pathDirectory.FullName, "gh"), "/usr/bin/true");
        File.CreateSymbolicLink(Path.Join(root, "gh"), "/usr/bin/false");
        var previousPath = Environment.GetEnvironmentVariable("PATH");
        var previousDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = root;
            Environment.SetEnvironmentVariable("PATH", pathDirectory.FullName);
            await CoverageHistoryGh.RequestAsync(Endpoint, 0, timeout);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            Environment.CurrentDirectory = previousDirectory;
        }
    }

    private static async Task ExpectFailureAsync(Process process, int limit, TimeSpan timeout, string reason)
    {
        try
        {
            await CoverageHistoryGh.RequestAsync(process, Endpoint, limit, timeout);
        }
        catch (IOException error) when (error.Message.Contains(reason, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidDataException($"Expected the gh process adapter to report '{reason}'.");
    }

    /// <summary>Runs a benign child fixture. Only the test entry point dispatches this argument.</summary>
    internal static async Task<int> RunFixtureAsync(string[] args)
    {
        if (args is not [var behavior, "api", "--method", "GET", Endpoint])
        {
            return 2;
        }

        switch (behavior)
        {
            case "binary":
                await Console.OpenStandardError().WriteAsync(new byte[256 * 1024]);
                await Console.OpenStandardOutput().WriteAsync(Response);
                return 0;
            case "nonzero":
                Console.Error.WriteLine("private fixture diagnostic");
                return 47;
            case "over-limit":
                await Console.OpenStandardOutput().WriteAsync(Response);
                goto case "timeout";
            case "timeout":
                await Task.Delay(TimeSpan.FromMinutes(1));
                return 0;
            default:
                return 2;
        }
    }
}
