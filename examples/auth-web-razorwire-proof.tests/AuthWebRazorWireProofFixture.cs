using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using CliWrap;
using CliCommandResult = CliWrap.CommandResult;

namespace AuthWebRazorWireProofExample.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AuthWebRazorWireProofCollection : ICollectionFixture<AuthWebRazorWireProofFixture>
{
    public const string Name = "AuthWebRazorWireProofCollection";
}

public sealed partial class AuthWebRazorWireProofFixture : IAsyncLifetime
{
    private readonly object _logGate = new();
    private readonly StringBuilder _logs = new();
    private readonly ConcurrentQueue<string> _recentLogs = new();
    private readonly TaskCompletionSource<string> _boundBaseUrlSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CliWrapProcessLease? _appProcess;

    public string BaseUrl { get; private set; } = string.Empty;

    public string RepositoryRoot { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        RepositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
        var projectPath = Path.Combine(
            RepositoryRoot,
            "examples",
            "auth-web-razorwire-proof",
            "AuthWebRazorWireProofExample.csproj");

        if (!File.Exists(projectPath))
        {
            throw new FileNotFoundException("Could not find the AppSurface Auth Web/RazorWire proof project.", projectPath);
        }

        _appProcess = CliWrapProcessLease.Start(Cli.Wrap("dotnet")
            .WithArguments(["run", "--project", projectPath, "--no-launch-profile", "--configuration", ResolveCurrentConfiguration()])
            .WithWorkingDirectory(RepositoryRoot)
            .WithEnvironmentVariables(new Dictionary<string, string?>
            {
                ["ASPNETCORE_URLS"] = "http://127.0.0.1:0",
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["DOTNET_ENVIRONMENT"] = "Development"
            })
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.ToDelegate(CaptureAppLog))
            .WithStandardErrorPipe(PipeTarget.ToDelegate(CaptureAppLog)));

        _ = _appProcess.Completion.ContinueWith(
            task =>
            {
                if (_appProcess.IsCancellationRequested)
                {
                    return;
                }

                _boundBaseUrlSource.TrySetException(
                    new InvalidOperationException($"Auth Web/RazorWire proof {DescribeAppExit(task)} before publishing a listening URL.{Environment.NewLine}{GetRecentLogs()}"));
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        BaseUrl = await WaitForBoundBaseUrlAsync(TimeSpan.FromSeconds(60));
        await WaitForAppReadyAsync(TimeSpan.FromSeconds(60));
    }

    public async Task DisposeAsync()
    {
        if (_appProcess is not null)
        {
            await _appProcess.DisposeAsync();
            _appProcess = null;
        }
    }

    public HttpClient CreateClient()
    {
        return new HttpClient
        {
            BaseAddress = new Uri(BaseUrl)
        };
    }

    public HttpClient CreateClientWithCookies()
    {
        return new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            CookieContainer = new CookieContainer()
        })
        {
            BaseAddress = new Uri(BaseUrl)
        };
    }

    public string ReadRepositoryFile(params string[] pathParts)
    {
        var parts = new string[pathParts.Length + 1];
        parts[0] = RepositoryRoot;
        Array.Copy(pathParts, 0, parts, 1, pathParts.Length);

        return File.ReadAllText(Path.Combine(parts));
    }

    public IEnumerable<string> ReadProductSourceFiles(params string[] roots)
    {
        foreach (var root in roots)
        {
            var absoluteRoot = Path.Combine(RepositoryRoot, root);
            foreach (var file in Directory.EnumerateFiles(absoluteRoot, "*.cs", SearchOption.AllDirectories)
                         .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                             && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
            {
                yield return File.ReadAllText(file);
            }
        }
    }

    private void CaptureAppLog(string line)
    {
        lock (_logGate)
        {
            _logs.AppendLine(line);
        }

        _recentLogs.Enqueue(line);
        while (_recentLogs.Count > 80 && _recentLogs.TryDequeue(out _))
        {
        }

        var match = ListeningUrlRegex().Match(line);
        if (!match.Success)
        {
            return;
        }

        var url = match.Groups["url"].Value.TrimEnd('/');
        _boundBaseUrlSource.TrySetResult(ReplaceLoopbackIpHostWithLocalhost(url));
    }

    private async Task<string> WaitForBoundBaseUrlAsync(TimeSpan timeout)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var registration = timeoutCts.Token.Register(
            () => _boundBaseUrlSource.TrySetException(
                new TimeoutException($"Auth Web/RazorWire proof did not publish a listening URL within {timeout.TotalSeconds} seconds.{Environment.NewLine}{GetRecentLogs()}")));

        return await _boundBaseUrlSource.Task;
    }

    private async Task WaitForAppReadyAsync(TimeSpan timeout)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var client = CreateClient();

        while (!timeoutCts.IsCancellationRequested)
        {
            try
            {
                using var response = await client.GetAsync("/", timeoutCts.Token);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), timeoutCts.Token);
        }

        throw new TimeoutException($"Auth Web/RazorWire proof was not ready within {timeout.TotalSeconds} seconds.{Environment.NewLine}{GetRecentLogs()}");
    }

    private string GetRecentLogs()
    {
        var recent = _recentLogs.ToArray();
        if (recent.Length == 0)
        {
            lock (_logGate)
            {
                return _logs.ToString();
            }
        }

        return string.Join(Environment.NewLine, recent);
    }

    private static string FindRepositoryRoot(string startPath)
    {
        var directory = new DirectoryInfo(startPath);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ForgeTrust.AppSurface.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Could not find repository root above '{startPath}'.");
    }

    private static string ResolveCurrentConfiguration()
    {
        var baseDirectoryParts = AppContext.BaseDirectory.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        for (var index = 0; index < baseDirectoryParts.Length - 1; index++)
        {
            if (string.Equals(baseDirectoryParts[index], "bin", StringComparison.OrdinalIgnoreCase))
            {
                return baseDirectoryParts[index + 1];
            }
        }

        return "Debug";
    }

    private static string ReplaceLoopbackIpHostWithLocalhost(string baseUrl)
    {
        var uri = new Uri(baseUrl, UriKind.Absolute);
        if (!IPAddress.TryParse(uri.Host, out var address) || !IPAddress.IsLoopback(address))
        {
            return baseUrl;
        }

        var builder = new UriBuilder(uri)
        {
            Host = "localhost"
        };

        return builder.Uri.ToString().TrimEnd('/');
    }

    private static string DescribeAppExit(Task<CliCommandResult> task)
    {
        if (task.IsFaulted)
        {
            return $"failed with {task.Exception.GetBaseException().Message}";
        }

        if (task.IsCanceled)
        {
            return "was canceled";
        }

        return $"exited with code {task.Result.ExitCode}";
    }

    [GeneratedRegex("Now listening on:\\s+(?<url>https?://\\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex ListeningUrlRegex();

    private sealed class CliWrapProcessLease : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();

        private CliWrapProcessLease(CliWrap.Command command)
        {
            Completion = command.ExecuteAsync(_cancellation.Token).Task;
        }

        public Task<CliCommandResult> Completion { get; }

        public bool IsCancellationRequested => _cancellation.IsCancellationRequested;

        public static CliWrapProcessLease Start(CliWrap.Command command)
        {
            return new CliWrapProcessLease(command);
        }

        public async ValueTask DisposeAsync()
        {
            if (!_cancellation.IsCancellationRequested)
            {
                _cancellation.Cancel();
            }

            try
            {
                await Completion.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex) when (ex is OperationCanceledException or TaskCanceledException or TimeoutException)
            {
            }
            finally
            {
                _cancellation.Dispose();
            }
        }
    }
}
