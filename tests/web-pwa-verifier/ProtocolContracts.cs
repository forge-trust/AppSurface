using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class ProtocolContracts
{
    public static async Task RunAsync()
    {
        const string validSuccess = """
            {
              "schemaVersion": 3, "surface": "all", "passed": true,
              "origin": "http://localhost", "baseUrl": "http://localhost/", "entryPath": "/", "entryUrl": "http://localhost/",
              "install": {
                "manifestPath": "/manifest.webmanifest", "startUrl": "/", "scope": "/", "display": "standalone",
                "themeColor": null, "backgroundColor": null, "icons": []
              },
              "push": {
                "expected": "enabled", "enabled": true, "configurationStatus": "not-configured",
                "worker": {"path": "/sw.js", "scope": "/", "fetch": "ok", "contentType": "ok", "nosniff": "ok", "cacheControl": "ok"},
                "registrationHelper": {"path": null, "headReference": "absent", "fetch": "unknown", "contentType": "unknown", "nosniff": "unknown", "cacheControl": "unknown"},
                "vapid": {"activeKeyId": null, "publicKeyFingerprint": null}, "routeMapping": "unknown",
                "browserSupport": "unknown", "installation": "unknown", "permission": "unknown", "subscription": "unknown",
                "notificationDisplay": "unknown", "notificationClick": "unknown", "unsubscribe": "unknown", "delivery": "unknown"
              },
              "diagnostics": []
            }
            """;
        var validFailure = validSuccess.Replace("\"passed\": true", "\"passed\": false", StringComparison.Ordinal);
        await Accepts(validSuccess, 0);
        await Accepts(validFailure, 7);
        await Accepts(validSuccess.Insert(1, "\"extra\": true,"), 0);
        await Rejects<JsonException>(Encoding.UTF8.GetBytes(validSuccess.Insert(1, "\"passed\": false,")), 0);
        var pushOnly = JsonNode.Parse(validSuccess)!.AsObject();
        pushOnly["install"] = null;
        await Rejects<PwaVerifier.VerificationFailure>(Encoding.UTF8.GetBytes(pushOnly.ToJsonString()), 0);
        await Accepts(validSuccess.Replace("\"diagnostics\": []", """
            "diagnostics": [{"code":"ASPWA200","severity":"info","message":"café 🐾"}]
            """, StringComparison.Ordinal), 0);

        foreach (var invalid in new[] { "", "[]", "{}", validSuccess[..^1], validSuccess + "{}", validSuccess + " log" })
        {
            await Rejects<JsonException>(Encoding.UTF8.GetBytes(invalid), 0);
        }

        foreach (var (path, invalidValue) in new (string, JsonNode?)[]
        {
            ("schemaVersion", "3"), ("surface", 3), ("passed", "true"), ("origin", null), ("baseUrl", null),
            ("entryPath", null), ("entryUrl", null), ("install", new JsonObject()), ("install.icons", new JsonObject()),
            ("install.themeColor", false), ("push", new JsonObject()), ("push.worker", new JsonObject()),
            ("push.registrationHelper", new JsonObject()), ("push.vapid", null), ("diagnostics", new JsonObject())
        })
        {
            var mutated = JsonNode.Parse(validSuccess)!.AsObject();
            var fields = path.Split('.');
            var parent = fields.Length == 2 ? mutated[fields[0]]!.AsObject() : mutated;
            var field = fields[^1];
            parent[field] = invalidValue;
            await Rejects<JsonException>(Encoding.UTF8.GetBytes(mutated.ToJsonString()), 0);
            parent.Remove(field);
            await Rejects<JsonException>(Encoding.UTF8.GetBytes(mutated.ToJsonString()), 0);
        }

        foreach (var invalid in new[]
        {
            "null",
            validSuccess.Replace("\"schemaVersion\": 3", "\"schemaVersion\": 2", StringComparison.Ordinal),
            validSuccess.Replace("\"all\"", "\"install\"", StringComparison.Ordinal),
            validSuccess.Replace("\"icons\": []", "\"icons\": [null]", StringComparison.Ordinal),
            validSuccess.Replace("\"diagnostics\": []", "\"diagnostics\": [null]", StringComparison.Ordinal)
        })
        {
            await Rejects<PwaVerifier.VerificationFailure>(Encoding.UTF8.GetBytes(invalid), 0);
        }

        await Rejects<PwaVerifier.VerificationFailure>(Encoding.UTF8.GetBytes(validSuccess), 1);
        await Rejects<PwaVerifier.VerificationFailure>(Encoding.UTF8.GetBytes(validFailure), 0);
        var invalidUtf8 = Encoding.UTF8.GetBytes(validSuccess.Replace("\"diagnostics\": []", "\"extension\": \"X\", \"diagnostics\": []", StringComparison.Ordinal));
        invalidUtf8[Array.IndexOf(invalidUtf8, (byte)'X')] = 0xff;
        await Rejects<PwaVerifier.VerificationFailure>(invalidUtf8, 0);

        await PublishesValidFailureAndRefusesMalformedOutput(validFailure);
    }

    private static async Task Accepts(string json, int exitCode) => await AcceptBytes(Encoding.UTF8.GetBytes(json), exitCode);

    private static async Task AcceptBytes(byte[] bytes, int exitCode)
    {
        await using var report = new MemoryStream(bytes, writable: false);
        await PwaVerifier.ValidateReportAsync(report, exitCode, CancellationToken.None);
    }

    private static async Task Rejects<TException>(byte[] bytes, int exitCode) where TException : Exception
    {
        await using var report = new MemoryStream(bytes, writable: false);
        try
        {
            await PwaVerifier.ValidateReportAsync(report, exitCode, CancellationToken.None);
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name} for {Encoding.UTF8.GetString(bytes)}");
    }

    private static async Task PublishesValidFailureAndRefusesMalformedOutput(string failedReport)
    {
        using var checkout = new TempDirectory();
        var fixtureRoot = checkout.Path;
        await CreateFixtureCheckout(fixtureRoot);
        var evidenceDirectory = Path.Combine(fixtureRoot, "evidence");
        Directory.CreateDirectory(evidenceDirectory);
        var evidence = Path.Combine(evidenceDirectory, "report.json");

        var validPort = TestHost.FreePort();
        var valid = await RunFixtureRunner(fixtureRoot, validPort, evidence, failedReport, 7);
        if (valid.ExitCode != 7 || await File.ReadAllTextAsync(evidence) != failedReport)
        {
            throw new InvalidOperationException($"A valid failed report was not published with the CLI's nonzero exit code. Runner={valid.ExitCode}. {valid.StandardError}");
        }
        AssertNoTemporaryReports(evidenceDirectory);
        AssertPortReusable(validPort);

        const string previous = "previous-evidence-preserved";
        await File.WriteAllTextAsync(evidence, previous);
        var incomplete = JsonNode.Parse(failedReport)!.AsObject();
        incomplete.Remove("origin");
        foreach (var invalidReport in new[] { "not-json", incomplete.ToJsonString() })
        {
            var malformedPort = TestHost.FreePort();
            var malformed = await RunFixtureRunner(fixtureRoot, malformedPort, evidence, invalidReport, 7);
            if (malformed.ExitCode != 1 || await File.ReadAllTextAsync(evidence) != previous)
            {
                throw new InvalidOperationException("Malformed child output replaced prior evidence or returned the child exit code.");
            }
            if (!malformed.StandardError.Contains("validating the public CLI report", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The malformed-output path did not reach child readiness.");
            }
            AssertNoTemporaryReports(evidenceDirectory);
            AssertPortReusable(malformedPort);
        }

        // File.Move cannot replace a directory. This tests publication itself on every platform.
        var occupied = Directory.CreateDirectory(Path.Combine(evidenceDirectory, "occupied"));
        var sentinel = Path.Combine(occupied.FullName, "prior");
        await File.WriteAllTextAsync(sentinel, previous);
        var occupiedPort = TestHost.FreePort();
        var rejected = await RunFixtureRunner(fixtureRoot, occupiedPort, occupied.FullName, failedReport, 7);
        if (rejected.ExitCode != 1 || await File.ReadAllTextAsync(sentinel) != previous
            || !rejected.StandardError.Contains("publishing the evidence file", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unexpected result for an unreplaceable destination. {rejected.StandardError}");
        }
        AssertNoTemporaryReports(evidenceDirectory);
        AssertPortReusable(occupiedPort);

        const string previousIdentity = "prior-launch";
        var childDirectory = Path.Combine(fixtureRoot, "examples", "web-pwa-install");
        var substitute = Path.Combine(childDirectory, "substitute-identity");
        foreach (var replaceAfterReadiness in new[] { false, true })
        {
            if (replaceAfterReadiness)
                await File.WriteAllTextAsync(Path.Combine(fixtureRoot, "change-identity"), previousIdentity);
            else
                await File.WriteAllTextAsync(substitute, previousIdentity);

            var port = TestHost.FreePort();
            var outcome = await RunFixtureRunner(fixtureRoot, port, evidence, failedReport, 7, startupSeconds: 3);
            var ranCli = outcome.StandardError.Contains("running public CLI (PID", StringComparison.Ordinal);
            var expectedPhase = replaceAfterReadiness ? "no longer owns the requested port" : "did not prove readiness";
            if (outcome.ExitCode != 1 || ranCli != replaceAfterReadiness ||
                !outcome.StandardError.Contains(expectedPhase, StringComparison.Ordinal) ||
                await File.ReadAllTextAsync(evidence) != previous)
                throw new InvalidOperationException($"Accepted stale instance identity or failed in the wrong phase. {outcome.StandardError}");
            AssertNoTemporaryReports(evidenceDirectory);
            AssertPortReusable(port);
            File.Delete(substitute);
        }
    }

    private static async Task CreateFixtureCheckout(string root)
    {
        var exampleDirectory = Path.Combine(root, "examples", "web-pwa-install");
        var cliDirectory = Path.Combine(root, "Cli", "ForgeTrust.AppSurface.Cli");
        Directory.CreateDirectory(exampleDirectory);
        Directory.CreateDirectory(cliDirectory);
        await File.WriteAllTextAsync(Path.Combine(exampleDirectory, "WebPwaInstallExample.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(exampleDirectory, "Program.cs"), """
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Hosting;
            var portIndex = Array.IndexOf(args, "--port");
            var port = int.Parse(args[portIndex + 1], System.Globalization.CultureInfo.InvariantCulture);
            var app = WebApplication.CreateBuilder(Array.Empty<string>()).Build();
            app.Urls.Add($"http://127.0.0.1:{port}");
            app.MapGet("/_appsurface/verify/instance", () => Results.Text(File.Exists("substitute-identity")
                ? File.ReadAllText("substitute-identity") : Environment.GetEnvironmentVariable("APPSURFACE_PWA_VERIFY_INSTANCE")!));
            app.MapGet("/{**path}", () => Results.Ok());
            await app.RunAsync();
            """);
        await File.WriteAllTextAsync(Path.Combine(cliDirectory, "ForgeTrust.AppSurface.Cli.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(cliDirectory, "Program.cs"), """
            if (File.Exists("change-identity"))
                File.Copy("change-identity", Path.Combine("examples", "web-pwa-install", "substitute-identity"));
            Console.Write(File.ReadAllText("fixture-output.txt"));
            return int.Parse(File.ReadAllText("fixture-exit.txt"), System.Globalization.CultureInfo.InvariantCulture);
            """);
    }

    private static async Task<(int ExitCode, string StandardError)> RunFixtureRunner(
        string root, int port, string evidence, string output, int childExit, int startupSeconds = 20)
    {
        await File.WriteAllTextAsync(Path.Combine(root, "fixture-output.txt"), output);
        await File.WriteAllTextAsync(Path.Combine(root, "fixture-exit.txt"), childExit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var diagnostics = new StringWriter();
        var previousError = Console.Error;
        try
        {
            Console.SetError(diagnostics);
            var code = await PwaVerifier.RunAsync(root, port, evidence, TimeSpan.FromSeconds(startupSeconds), deadline.Token);
            return (code, diagnostics.ToString());
        }
        finally
        {
            Console.SetError(previousError);
        }
    }

    private static void AssertPortReusable(int port)
    {
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
    }

    private static void AssertNoTemporaryReports(string directory)
    {
        if (Directory.EnumerateFiles(directory, ".pwa-verify-*.tmp").Any())
        {
            throw new InvalidOperationException("The runner left a temporary evidence report behind.");
        }
    }
}
