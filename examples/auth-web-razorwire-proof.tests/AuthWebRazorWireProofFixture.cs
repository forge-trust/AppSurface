using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using CliWrap;
using CliCommandResult = CliWrap.CommandResult;

namespace AuthWebRazorWireProofExample.Tests;

/// <summary>
/// xUnit collection used to share a single hosted proof app instance across the black-box sample tests.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AuthWebRazorWireProofCollection : ICollectionFixture<AuthWebRazorWireProofFixture>
{
    /// <summary>
    /// Stable xUnit collection name for tests that depend on the hosted proof app fixture.
    /// </summary>
    public const string Name = "AuthWebRazorWireProofCollection";
}

/// <summary>
/// Starts the Auth Web/RazorWire proof sample as an external ASP.NET Core process for black-box tests.
/// </summary>
/// <remarks>
/// The fixture intentionally exercises the sample through HTTP and rendered HTML rather than internal
/// members. Repository file helpers only accept repository-relative path segments and reject rooted or
/// escaping paths so docs contract checks cannot read outside the checkout.
/// </remarks>
public sealed partial class AuthWebRazorWireProofFixture : IAsyncLifetime
{
    private readonly object _logGate = new();
    private readonly StringBuilder _logs = new();
    private readonly ConcurrentQueue<string> _recentLogs = new();
    private readonly TaskCompletionSource<string> _boundBaseUrlSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CliWrapProcessLease? _appProcess;

    /// <summary>
    /// Base URL published by the hosted proof app after startup.
    /// </summary>
    public string BaseUrl { get; private set; } = string.Empty;

    /// <summary>
    /// Absolute repository root resolved from the test output directory.
    /// </summary>
    public string RepositoryRoot { get; private set; } = string.Empty;

    /// <summary>
    /// Starts the sample app and waits until the root proof console responds successfully.
    /// </summary>
    public async Task InitializeAsync()
    {
        RepositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
        var projectPath = ResolveRepositoryPath(
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
                ["ASPNETCORE_ENVIRONMENT"] = "Production",
                ["DOTNET_ENVIRONMENT"] = "Production"
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

    /// <summary>
    /// Stops the hosted proof app process and releases its cancellation source.
    /// </summary>
    public async Task DisposeAsync()
    {
        if (_appProcess is not null)
        {
            await _appProcess.DisposeAsync();
            _appProcess = null;
        }
    }

    /// <summary>
    /// Creates a non-redirecting HTTP client bound to <see cref="BaseUrl"/>.
    /// </summary>
    /// <returns>An HTTP client for direct API checks against the proof app.</returns>
    public HttpClient CreateClient()
    {
        return new HttpClient
        {
            BaseAddress = new Uri(BaseUrl)
        };
    }

    /// <summary>
    /// Creates a browser-like HTTP client bound to <see cref="BaseUrl"/>.
    /// </summary>
    /// <returns>An HTTP client configured to follow redirects for rendered-page checks.</returns>
    public HttpClient CreateBrowserClient()
    {
        return new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true
        })
        {
            BaseAddress = new Uri(BaseUrl)
        };
    }

    /// <summary>
    /// Reads a UTF-8/ASCII text file from the repository checkout.
    /// </summary>
    /// <param name="pathParts">Repository-relative path segments. Rooted or escaping paths are rejected.</param>
    /// <returns>The file contents.</returns>
    public string ReadRepositoryFile(params string[] pathParts)
    {
        return File.ReadAllText(ResolveRepositoryPath(pathParts));
    }

    /// <summary>
    /// Reads C# product source files under one or more repository-relative roots.
    /// </summary>
    /// <param name="roots">Repository-relative source roots to scan.</param>
    /// <returns>File contents for matching source files, excluding build output directories.</returns>
    public IEnumerable<string> ReadProductSourceFiles(params string[] roots)
    {
        foreach (var absoluteRoot in roots.Select(root => ResolveRepositoryPath(root)))
        {
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
            // Intentionally empty: trimming recent logs is done in the loop condition.
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
                // Expected while the proof app is still binding; retry until ready or timeout.
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
            if (File.Exists(Path.Join(directory.FullName, "ForgeTrust.AppSurface.slnx")))
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

    private string ResolveRepositoryPath(params string[] relativeParts)
    {
        foreach (var rootedPart in relativeParts.Where(Path.IsPathRooted))
        {
            throw new ArgumentException($"Repository-relative path segment must not be rooted: '{rootedPart}'.", nameof(relativeParts));
        }

        var path = RepositoryRoot;
        foreach (var part in relativeParts)
        {
            path = Path.Join(path, part);
        }

        var fullPath = Path.GetFullPath(path);
        var rootWithSeparator = RepositoryRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.Ordinal)
            && !string.Equals(fullPath, RepositoryRoot, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Repository-relative path escaped the repository root: '{fullPath}'.", nameof(relativeParts));
        }

        return fullPath;
    }

    [GeneratedRegex("Now listening on:\\s+(?<url>https?://\\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex ListeningUrlRegex();

    private sealed class CliWrapProcessLease : IAsyncDisposable
    {
        private readonly CancellationTokenSourceLease _cancellation = new();

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
            await using (_cancellation.ConfigureAwait(false))
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
                    System.Diagnostics.Trace.TraceWarning(
                        "Ignored expected proof app shutdown exception: {0}: {1}",
                        ex.GetType().Name,
                        ex.Message);
                }
            }
        }
    }

    private sealed class CancellationTokenSourceLease : IAsyncDisposable
    {
        private readonly CancellationTokenSource _source = new();

        public CancellationToken Token => _source.Token;

        public bool IsCancellationRequested => _source.IsCancellationRequested;

        public void Cancel()
        {
            _source.Cancel();
        }

        public ValueTask DisposeAsync()
        {
            _source.Dispose();

            return ValueTask.CompletedTask;
        }
    }
}
