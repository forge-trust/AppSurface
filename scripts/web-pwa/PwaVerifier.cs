using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Cli;

// Implementation included by verify.cs. The public CLI owns all PWA expectations and produces the evidence.
internal static class PwaVerifier
{
    private const string IdentityPath = "/_appsurface/verify/instance";
    private const string IdentityVariable = "APPSURFACE_PWA_VERIFY_INSTANCE";
    private const int HarnessFailure = 1;
    private static readonly JsonSerializerOptions ReportOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        AllowDuplicateProperties = false
    };

    /// <summary>Builds the two checkout projects and publishes the public verifier's report for this child.</summary>
    public static async Task<int> RunAsync(
        string repositoryRoot, int port, string evidencePath, TimeSpan startupTimeout, CancellationToken cancellationToken)
    {
        var example = Path.Join(repositoryRoot, "examples", "web-pwa-install", "WebPwaInstallExample.csproj");
        var cli = Path.Join(repositoryRoot, "Cli", "ForgeTrust.AppSurface.Cli", "ForgeTrust.AppSurface.Cli.csproj");
        var instance = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var endpoint = new Uri($"http://127.0.0.1:{port}");
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        {
            BaseAddress = endpoint,
            Timeout = Timeout.InfiniteTimeSpan
        };
        var children = new List<OwnedProcess>();
        FileStream? report = null;
        string? temporary = null;
        var published = false;
        var result = HarnessFailure;
        var operation = "building the example";
        try
        {
            var exampleDll = await BuildAsync(example);
            operation = "building the public CLI";
            var cliDll = await BuildAsync(cli);
            operation = "starting the example";
            var hostInfo = Dotnet(Path.GetDirectoryName(example)!, [exampleDll, "--environment", "Development", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            hostInfo.Environment[IdentityVariable] = instance;
            var host = Start(hostInfo, Stream.Null);
            Console.Error.WriteLine($"PWA verifier starting example (PID {host.Id}).");
            await WaitForReadinessAsync(host, http, instance, startupTimeout, cancellationToken);

            operation = "creating the evidence file";
            var directory = Path.GetDirectoryName(evidencePath)!;
            Directory.CreateDirectory(directory);
            var candidate = Path.Join(directory, $".pwa-verify-{Guid.NewGuid():N}.tmp");
            report = new FileStream(candidate, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, true);
            temporary = candidate;
            operation = "running the public PWA CLI";
            var arguments = new[]
            {
                cliDll, "pwa", "verify", "--surface", "all", "--base-url", endpoint.GetLeftPart(UriPartial.Authority),
                "--entry-path", "/account/resume", "--expect-start-url", "/", "--expect-scope", "/",
                "--expect-display", "standalone", "--expect-theme-color", "#2563eb", "--expect-background-color", "#ffffff",
                "--expect-icon", "192x192", "--expect-icon", "512x512", "--expect-push", "enabled", "--json"
            };
            var verifier = Start(Dotnet(repositoryRoot, arguments), report);
            Console.Error.WriteLine($"PWA verifier running public CLI (PID {verifier.Id}).");
            var exit = await verifier.WaitAsync(cancellationToken);
            operation = "validating the public CLI report";
            report.Position = 0;
            await ValidateReportAsync(report, exit, cancellationToken);
            await report.DisposeAsync();
            report = null;

            operation = "confirming the example still owns the endpoint";
            using (var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                probe.CancelAfter(TimeSpan.FromSeconds(2));
                if (host.HasExited || !await HasIdentityAsync(http, instance, probe.Token) || host.HasExited)
                {
                    throw new VerificationFailure("The launched example no longer owns the requested port.");
                }
            }

            operation = "publishing the evidence file";
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, evidencePath, overwrite: true);
            temporary = null;
            published = true;
            Console.Error.WriteLine($"Published schema-v3 PWA evidence to {evidencePath}");
            result = exit;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine("PWA-CANCELLED: Verification cancelled.");
            result = 130;
        }
        catch (Exception ex) when (ex is VerificationFailure or Win32Exception or IOException or UnauthorizedAccessException
                                   or JsonException or HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            // Raw child output and exception messages can include the caller's configuration.
            var detail = ex is VerificationFailure ? ex.Message : ex.GetType().Name;
            Console.Error.WriteLine($"PWA-VERIFY: Failed while {operation}. {detail}");
        }
        finally
        {
            using var teardown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await Task.WhenAll(children.Select(child => child.StopAsync(teardown.Token))).WaitAsync(teardown.Token);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or OperationCanceledException)
            {
                Console.Error.WriteLine("PWA-CLEANUP: Could not confirm termination of every owned process within five seconds.");
                result = HarnessFailure;
            }
            finally
            {
                foreach (var child in children)
                {
                    child.Dispose();
                }
            }

            // Keep the output stream open until the child has stopped writing, including on cancellation.
            if (report is not null)
            {
                try
                {
                    await report.DisposeAsync();
                }
                catch (IOException)
                {
                    Console.Error.WriteLine("PWA-CLEANUP: Failed to close the temporary report.");
                    result = HarnessFailure;
                }
            }

            if (temporary is not null)
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"PWA-CLEANUP: Could not remove temporary evidence file {temporary}.");
                    result = HarnessFailure;
                }
            }

            if (!published)
            {
                Console.Error.WriteLine($"No evidence published by this run. Any existing report at {evidencePath} is from a previous run.");
            }
        }

        return result;

        OwnedProcess Start(ProcessStartInfo info, Stream stdout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var child = new OwnedProcess(info, stdout);
            children.Add(child);
            return child;
        }

        async Task<string> BuildAsync(string project)
        {
            // MSBuild prints only TargetPath on success; no build output can enter the public CLI's JSON stream.
            var path = new MemoryStream();
            var build = Start(Dotnet(repositoryRoot, ["build", project, "--disable-build-servers", "--nologo",
                "-p:UseSharedCompilation=false", "-t:Build", "-getProperty:TargetPath"]), path);
            var code = await build.WaitAsync(cancellationToken);
            var dll = Encoding.UTF8.GetString(path.ToArray()).Trim();
            path.Dispose();
            if (code != 0 || !Path.IsPathFullyQualified(dll) || !File.Exists(dll))
            {
                throw new VerificationFailure($"Build exited {code}. Run dotnet build on {project} to inspect the compiler diagnostics.");
            }

            return dll;
        }
    }

    private static ProcessStartInfo Dotnet(string cwd, IEnumerable<string> arguments)
    {
        // DOTNET_HOST_PATH is set by the SDK and preserves the muxer used for dotnet run --file.
        var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return info;
    }

    private static async Task WaitForReadinessAsync(OwnedProcess host, HttpClient http, string instance, TimeSpan timeout, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            token.ThrowIfCancellationRequested();
            if (host.HasExited)
            {
                throw new VerificationFailure($"Example PID {host.Id} exited {host.ExitCode} before readiness. Check whether the requested port is occupied.");
            }

            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
            var remaining = timeout - clock.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }
            attempt.CancelAfter(remaining < TimeSpan.FromSeconds(2) ? remaining : TimeSpan.FromSeconds(2));
            try
            {
                if (await HasIdentityAsync(http, instance, attempt.Token)
                    && await IsReadyAsync(http, "/manifest.webmanifest", attempt.Token)
                    && await IsReadyAsync(http, "/_appsurface/pwa/status.json", attempt.Token)
                    && !host.HasExited)
                {
                    return;
                }
            }
            catch (HttpRequestException) { /* The host may still be binding. */ }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { /* Attempt deadline. */ }
            var delay = timeout - clock.Elapsed;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay < TimeSpan.FromMilliseconds(150) ? delay : TimeSpan.FromMilliseconds(150), token);
            }
        }

        throw new VerificationFailure($"The example did not prove readiness on {http.BaseAddress} within {timeout.TotalSeconds:g} seconds.");
    }

    private static async Task<bool> IsReadyAsync(HttpClient http, string path, CancellationToken token)
    {
        using var response = await http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, token);
        return response.IsSuccessStatusCode;
    }

    private static async Task<bool> HasIdentityAsync(HttpClient http, string instance, CancellationToken token)
    {
        using var response = await http.GetAsync(IdentityPath, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return false;
        }

        // Read at most one extra byte, including for unknown-length responses from an unrelated listener.
        var bytes = new byte[instance.Length + 1];
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        var count = await stream.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, token);
        return count == instance.Length && instance == Encoding.ASCII.GetString(bytes, 0, count);
    }

    /// <summary>Checks the combined schema envelope and exit status while preserving the original report bytes.</summary>
    /// <exception cref="JsonException">The text is invalid JSON or does not match the CLI's report fields.</exception>
    /// <exception cref="VerificationFailure">The bytes are invalid UTF-8 or the report is inconsistent with a combined verification run.</exception>
    internal static async Task ValidateReportAsync(Stream report, int exitCode, CancellationToken token)
    {
        // Validate even the bytes in unrecognized fields before publishing the original file.
        using var reader = new StreamReader(report, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        string output;
        try
        {
            output = await reader.ReadToEndAsync(token);
        }
        catch (DecoderFallbackException)
        {
            throw new VerificationFailure("The public CLI returned invalid UTF-8.");
        }

        var evidence = JsonSerializer.Deserialize<PwaVerificationV3Report>(output, ReportOptions);
        if (evidence is not { SchemaVersion: 3, Surface: "all", Install: not null }
            || evidence.Passed != (exitCode == 0)
            || evidence.Install.Icons.Any(icon => icon is null)
            || evidence.Diagnostics.Any(diagnostic => diagnostic is null))
        {
            throw new VerificationFailure("The CLI output is not a complete combined schema-v3 report matching its exit code.");
        }
    }

    /// <summary>Records a verifier failure whose diagnostic can be shown without exposing raw child output.</summary>
    internal sealed class VerificationFailure(string message) : Exception(message);

    private sealed class OwnedProcess : IDisposable
    {
        private readonly Process _process;
        private readonly Task _output;
        private readonly Task _errors;

        internal OwnedProcess(ProcessStartInfo info, Stream stdout)
        {
            _process = Process.Start(info) ?? throw new VerificationFailure("Failed to start the dotnet host.");
            _output = _process.StandardOutput.BaseStream.CopyToAsync(stdout);
            _errors = _process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        }

        internal int Id => _process.Id;
        internal int ExitCode => _process.ExitCode;
        internal bool HasExited => _process.HasExited;

        internal async Task<int> WaitAsync(CancellationToken token)
        {
            // Observe pipe write failures before waiting for exit: a full disk can otherwise block the child on stdout.
            await _output.WaitAsync(token);
            await _errors.WaitAsync(token);
            await _process.WaitForExitAsync(token);
            return ExitCode;
        }

        internal async Task StopAsync(CancellationToken token)
        {
            if (!HasExited)
            {
                try
                {
                    _process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (HasExited)
                {
                    // The child exited between the first HasExited check and Kill. Its pipes still need draining below.
                }
            }

            await _process.WaitForExitAsync(token);
            await Task.WhenAll(_output, _errors).WaitAsync(token);
        }

        public void Dispose() => _process.Dispose();
    }
}
