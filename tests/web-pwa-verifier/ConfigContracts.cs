using System.Text.Json;

internal static class ConfigContracts
{
    public static async Task RunAsync()
    {
        using var temporary = new TempDirectory();
        var checkout = Path.Combine(temporary.Path, "src");
        await TestHost.CopyCheckoutAsync(checkout);

        var caller = Path.Combine(temporary.Path, "caller with spaces");
        Directory.CreateDirectory(caller);
        File.Copy(Path.Combine(TestHost.Root, "global.json"), Path.Combine(caller, "global.json"));

        var script = Path.Combine(checkout, "scripts", "web-pwa", "verify.cs");
        var production = Path.Combine(Path.GetDirectoryName(script)!, "appsettings.json");
        var development = Path.Combine(Path.GetDirectoryName(script)!, "appsettings.Development.json");
        const string decoy = "{\"PwaVerify\":{\"Port\":0,\"StartupTimeoutSeconds\":0}}";
        await File.WriteAllTextAsync(Path.Combine(caller, "appsettings.json"), decoy);

        var defaultOutput = Path.Combine(checkout, "examples", "web-pwa-install", "pwa-verify-v3.json");
        await AssertSuccessAsync(defaultOutput, 5055);
        File.Delete(defaultOutput);

        var port = TestHost.FreePort();
        await File.WriteAllTextAsync(production, decoy);
        await File.WriteAllTextAsync(
            development,
            JsonSerializer.Serialize(new
            {
                PwaVerify = new { Port = port, EvidencePath = "json proof.json" }
            }));
        var developmentEnvironment = new Dictionary<string, string>
        {
            ["DOTNET_ENVIRONMENT"] = "Development"
        };
        await AssertSuccessAsync(Path.Combine(caller, "json proof.json"), port, environment: developmentEnvironment);

        port = TestHost.FreePort();
        var mappedEnvironment = new Dictionary<string, string>(developmentEnvironment)
        {
            ["APP_SURFACE_WEB_PWA_PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["APP_SURFACE_WEB_PWA_EVIDENCE"] = "env proof.json"
        };
        await AssertSuccessAsync(Path.Combine(caller, "env proof.json"), port, environment: mappedEnvironment);
        await AssertSuccessAsync(
            Path.Combine(caller, "explicit proof.json"),
            5055,
            ["--port", "5055", "--evidence-path", "explicit proof.json"],
            mappedEnvironment);

        var priorEvidence = Path.Combine(caller, "previous.json");
        var priorContents = "previous report"u8.ToArray();
        await File.WriteAllBytesAsync(priorEvidence, priorContents);
        await File.WriteAllTextAsync(production, "{\"PwaVerify\":{\"Port\":5055}}");

        var invalidCases = new (string[] Arguments, Dictionary<string, string> Environment)[]
        {
            (["--port", "0"], new()),
            (["--port", "65536"], new()),
            (["--port", "bogus"], new()),
            (["--startup-timeout-seconds", "0"], new()),
            ([], new() { ["APP_SURFACE_WEB_PWA_PORT"] = "bad-port" }),
            ([], new() { ["PWAVERIFY__STARTUPTIMEOUTSECONDS"] = "0" }),
            (["--evidence-path", ""], new()),
            (["--evidence-path", caller], new())
        };

        foreach (var (arguments, overrides) in invalidCases)
        {
            var environment = new Dictionary<string, string>(overrides)
            {
                ["APP_SURFACE_WEB_PWA_EVIDENCE"] = Path.GetFileName(priorEvidence)
            };
            var result = await RunAsync(arguments, environment);
            var output = $"{result.StandardOutput}\n{result.StandardError}";
            TestHost.Check(result.ExitCode != 0, $"Invalid arguments unexpectedly succeeded: {string.Join(' ', arguments)}\n{output}");
            TestHost.Check(
                !result.StandardError.Contains("PWA verifier starting example", StringComparison.Ordinal),
                $"Invalid configuration launched the example: {output}");
            TestHost.Check(
                (await File.ReadAllBytesAsync(priorEvidence)).AsSpan().SequenceEqual(priorContents),
                $"Invalid configuration replaced prior evidence: {output}");
        }

        Console.WriteLine("File-app configuration contracts passed.");

        async Task AssertSuccessAsync(
            string evidencePath,
            int expectedPort,
            string[]? arguments = null,
            Dictionary<string, string>? environment = null)
        {
            TestHost.Check(!File.Exists(evidencePath), $"Evidence already exists before the run: {evidencePath}");
            var result = await RunAsync(arguments ?? [], environment);
            TestHost.Check(
                result.ExitCode == 0,
                $"Expected successful verification on port {expectedPort}, got {result.ExitCode}.\n{result.StandardOutput}\n{result.StandardError}");

            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(evidencePath));
            var root = report.RootElement;
            TestHost.Check(
                root.GetProperty("origin").GetString() == $"http://127.0.0.1:{expectedPort}"
                    && root.GetProperty("schemaVersion").GetInt32() == 3
                    && root.GetProperty("surface").GetString() == "all"
                    && root.GetProperty("passed").GetBoolean(),
                $"Evidence did not reflect the expected successful run: {await File.ReadAllTextAsync(evidencePath)}");
        }

        async Task<CommandResult> RunAsync(string[] arguments, Dictionary<string, string>? environment = null)
        {
            var start = TestHost.Command(
                TestHost.Dotnet,
                caller,
                ["run", "--file", script, "--", .. arguments]);
            foreach (var key in start.Environment.Keys.Where(IsControlledEnvironmentVariable).ToArray())
            {
                start.Environment.Remove(key);
            }

            if (environment is not null)
            {
                foreach (var (key, value) in environment)
                {
                    start.Environment[key] = value;
                }
            }

            return await TestHost.RunAsync(start, TimeSpan.FromSeconds(300));
        }
    }

    private static bool IsControlledEnvironmentVariable(string key) =>
        key.Equals("ASPNETCORE_ENVIRONMENT", StringComparison.OrdinalIgnoreCase)
        || key.Equals("DOTNET_ENVIRONMENT", StringComparison.OrdinalIgnoreCase)
        || key.Equals("APP_SURFACE_WEB_PWA_PORT", StringComparison.OrdinalIgnoreCase)
        || key.Equals("APP_SURFACE_WEB_PWA_EVIDENCE", StringComparison.OrdinalIgnoreCase)
        || key.Equals("PWAVERIFY__STARTUPTIMEOUTSECONDS", StringComparison.OrdinalIgnoreCase);
}
