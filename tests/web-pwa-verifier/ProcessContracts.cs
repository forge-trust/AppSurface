using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

internal static class ProcessContracts
{
    private const string PriorReport = """{"schemaVersion":3,"passed":false,"marker":"previous-run"}""";
    private const string Starting = "PWA verifier starting example";
    private const string Verifying = "PWA verifier running public CLI";
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(4);

    public static async Task RunAsync()
    {
        using var temporary = new TempDirectory();
        var checkout = Path.Combine(temporary.Path, "source");
        await TestHost.CopyCheckoutAsync(checkout);
        var output = Environment.GetEnvironmentVariable("PWA_VERIFY_EVIDENCE_DIR") ?? temporary.Path;
        var reports = Directory.CreateDirectory(Path.Combine(output, $"run-{Guid.NewGuid():N}")).FullName;

        // A real successful run must replace the previous file at the caller's destination.
        var success = Path.Combine(reports, "success.json");
        await File.WriteAllTextAsync(success, PriorReport);
        var port = TestHost.FreePort();
        var result = await TestHost.RunAsync(Verifier(TestHost.Root, port, success), RunTimeout);
        var report = await CheckReportAsync(success, result, expectedExit: 0);
        TestHost.Check(!report.ContainsKey("marker"), "Successful verification retained stale evidence.");
        await CheckTeardownAsync(result.StandardError, port, Verifying);

        var icon = Path.Combine(checkout, "examples", "web-pwa-install", "wwwroot", "icons", "app-192.svg");
        var iconBytes = await File.ReadAllBytesAsync(icon);
        File.Delete(icon);
        port = TestHost.FreePort();
        var failed = Path.Combine(reports, "missing-icon.json");
        result = await TestHost.RunAsync(Verifier(checkout, port, failed), RunTimeout);
        report = await CheckReportAsync(failed, result, expectedExit: 1);
        var expected = JsonNode.Parse("""
            {"code":"ASPWA212","severity":"error","message":"Icon /icons/app-192.svg returned HTTP 404.",
             "subject":"manifest.icons[].src","expected":"2xx","actual":"HTTP 404",
             "fix":"Publish the icon file at the declared manifest path.","docsUrl":null}
            """);
        var icons = report["diagnostics"]!.AsArray().Where(d => (string?)d?["code"] == "ASPWA212").ToArray();
        TestHost.Check(icons.Length == 1 && JsonNode.DeepEquals(icons[0], expected), $"Unexpected icon diagnostic: {report}");
        await CheckTeardownAsync(result.StandardError, port, Verifying);
        await File.WriteAllBytesAsync(icon, iconBytes);

        var evidence = Path.Combine(temporary.Path, "preserved.json");
        await File.WriteAllTextAsync(evidence, PriorReport);
        var program = Path.Combine(checkout, "examples", "web-pwa-install", "Program.cs");
        var source = (await File.ReadAllTextAsync(program)).ReplaceLineEndings("\n");
        const string entry = "await WebApp<WebPwaInstallModule>.RunAsync(";
        await ReplaceSourceAsync(program, source, entry, "await Task.Delay(TimeSpan.FromSeconds(20));\n" + entry);

        port = TestHost.FreePort();
        result = await TestHost.RunAsync(Verifier(checkout, port, evidence, timeoutSeconds: 1), TimeSpan.FromSeconds(30));
        TestHost.Check(result.ExitCode != 0 && result.StandardError.Contains("did not prove readiness"),
            $"Expected startup timeout. {result.StandardError}");
        await CheckPriorReportAsync(evidence);
        await CheckTeardownAsync(result.StandardError, port, Starting);
        await CheckCancellationAsync(checkout, evidence, Starting);

        // Keep the example ready while the verification requests are in flight.
        const string middleware = "public void ConfigureEndpointAwareMiddleware(StartupContext context, IApplicationBuilder app)\n    {";
        await ReplaceSourceAsync(program, source, middleware, middleware + "\n" + """
            app.Use(async (http, next) =>
            {
                if (http.Request.Path != "/manifest.webmanifest" && http.Request.Path != "/_appsurface/pwa/status.json"
                    && http.Request.Path != "/_appsurface/verify/instance")
                    await Task.Delay(TimeSpan.FromSeconds(5), http.RequestAborted);
                await next();
            });
            """);
        await CheckCancellationAsync(checkout, evidence, Verifying);
        await CheckForeignListenerAsync(evidence);
    }

    private static ProcessStartInfo Verifier(string root, int port, string evidence, int timeoutSeconds = 60)
    {
        var start = TestHost.Command(TestHost.Dotnet, root, "run", "--file", Path.Combine(root, "scripts", "web-pwa", "verify.cs"),
            "--", "--port", port.ToString(CultureInfo.InvariantCulture), "--evidence-path", evidence,
            "--startup-timeout-seconds", timeoutSeconds.ToString(CultureInfo.InvariantCulture));
        start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        start.Environment["UseSharedCompilation"] = "false";
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        return start;
    }

    private static async Task CheckCancellationAsync(string checkout, string evidence, string phase)
    {
        var port = TestHost.FreePort();
        using var process = NativeConsole.Start(Verifier(checkout, port, evidence));
        var output = process.StandardOutput.ReadToEndAsync();
        var phaseReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var diagnostics = ReadDiagnosticsAsync();
        try
        {
            await phaseReached.Task.WaitAsync(RunTimeout);
            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await NativeConsole.SendCtrlCAsync(process).WaitAsync(watchdog.Token);
            await process.WaitForExitAsync(watchdog.Token);
            var stderr = await diagnostics.WaitAsync(watchdog.Token);
            await output.WaitAsync(watchdog.Token);
            TestHost.Check(process.ExitCode == 130, $"Ctrl+C during {phase} returned {process.ExitCode}.\n{stderr}");
            await CheckPriorReportAsync(evidence);
            await CheckTeardownAsync(stderr, port, phase);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }

        async Task<string> ReadDiagnosticsAsync()
        {
            var text = new StringBuilder();
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                text.AppendLine(line);
                if (line.Contains(phase, StringComparison.Ordinal))
                {
                    phaseReached.TrySetResult();
                }
            }
            phaseReached.TrySetException(new InvalidOperationException($"Verifier exited before {phase}.\n{text}"));
            return text.ToString();
        }
    }

    private static async Task<JsonObject> CheckReportAsync(string path, CommandResult result, int expectedExit)
    {
        TestHost.Check(result.ExitCode == expectedExit, $"Expected exit {expectedExit}, got {result.ExitCode}.\n{result.StandardError}");
        var report = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        TestHost.Check((int?)report["schemaVersion"] == 3 && (string?)report["surface"] == "all"
            && (bool?)report["passed"] == (expectedExit == 0) && report["install"] is JsonObject
            && report["push"] is JsonObject && report["diagnostics"] is JsonArray, $"Unexpected report: {report}");
        return report;
    }

    private static async Task CheckPriorReportAsync(string path) =>
        TestHost.Check(await File.ReadAllTextAsync(path) == PriorReport, $"{path} changed after an unsuccessful launch.");

    private static async Task CheckTeardownAsync(string stderr, int port, string lastPhase)
    {
        foreach (var phase in lastPhase == Verifying ? new[] { Starting, Verifying } : new[] { Starting })
        {
            var match = Regex.Match(stderr, Regex.Escape(phase) + @" \(PID (\d+)\)\.");
            TestHost.Check(match.Success, $"Missing child PID for {phase}.\n{stderr}");
            var pid = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            try
            {
                using var child = Process.GetProcessById(pid);
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (ArgumentException)
            {
                // The child exited before the PID lookup.
            }
        }
        using var portProbe = new TcpListener(IPAddress.Loopback, port);
        portProbe.Start();
    }

    private static async Task ReplaceSourceAsync(string path, string source, string marker, string replacement)
    {
        TestHost.Check(source.Contains(marker, StringComparison.Ordinal), $"Missing fixture marker in {path}: {marker}");
        await File.WriteAllTextAsync(path, source.Replace(marker, replacement, StringComparison.Ordinal));
    }

    private static async Task CheckForeignListenerAsync(string evidence)
    {
        const string manifest = """{"name":"Foreign listener","start_url":"/","scope":"/","display":"standalone"}""";
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var foreign = builder.Build();
        foreign.MapGet("/manifest.webmanifest", () => Results.Text(manifest, "application/manifest+json"));
        foreign.MapGet("/_appsurface/pwa/status.json", () => Results.Json(new { }));
        await foreign.StartAsync();
        try
        {
            var origin = new Uri(foreign.Urls.Single());
            var result = await TestHost.RunAsync(Verifier(TestHost.Root, origin.Port, evidence, timeoutSeconds: 4),
                TimeSpan.FromSeconds(30));
            TestHost.Check(result.ExitCode != 0 && result.StandardError.Contains("No evidence published by this run"),
                $"Expected refusal of the unrelated listener.\n{result.StandardError}");
            await CheckPriorReportAsync(evidence);
            using var http = new HttpClient { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(3) };
            TestHost.Check(await http.GetStringAsync("/manifest.webmanifest") == manifest,
                "The unrelated listener did not survive verification.");
        }
        finally
        {
            await foreign.StopAsync();
        }
    }
}
