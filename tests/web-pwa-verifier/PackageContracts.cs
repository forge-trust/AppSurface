using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Testing;

internal static class PackageContracts
{
    private const string SyntheticSecret = "synthetic-file-based-proof-only";
    private const string SecretResolutionMarker = "Synthetic typed secret matched in memory.";
    private const string HelpSecretFailureMarker = "HELP_RESOLVED_SECRET_PROVIDER";

    public static async Task RunAsync()
    {
        using var temporary = new TempDirectory();
        var consumer = Path.GetFullPath(temporary.Path);
        var example = TestPathUtils.PathUnder(TestHost.Root, "examples", "file-based-console");
        var globalJson = Path.Combine(TestHost.Root, "global.json");
        using var sdkSettings = JsonDocument.Parse(await File.ReadAllTextAsync(globalJson));
        var pinnedSdkVersion = sdkSettings.RootElement.GetProperty("sdk").GetProperty("version").GetString()
            ?? throw new InvalidOperationException("The pinned SDK version is missing from global.json.");

        await File.WriteAllTextAsync(
            Path.Combine(consumer, "NuGet.Config"),
            """
            <configuration>
              <packageSources>
                <clear />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
            </configuration>
            """);

        var programSource = NormalizeNewlines(await File.ReadAllTextAsync(Path.Combine(example, "Program.cs")));
        const string resolveMethod =
            "    public ConfigSecretProviderResolution Resolve(ConfigSecretReference reference, ConfigSecretResolutionContext context) =>\n"
            + "        ConfigSecretProviderResolution.Resolved(SyntheticCredential.Value, ConfigSecretSourceMetadata.Create(Id));";
        const string throwingResolveMethod =
            "    public ConfigSecretProviderResolution Resolve(ConfigSecretReference reference, ConfigSecretResolutionContext context) =>\n"
            + "        throw new InvalidOperationException(\"HELP_RESOLVED_SECRET_PROVIDER\");";
        var resolutionOccurrences = programSource.Split(resolveMethod, StringSplitOptions.None).Length - 1;
        TestHost.Check(
            resolutionOccurrences == 1,
            "Could not instrument the copied synthetic secret provider for the help check.");

        var program = Path.Combine(consumer, "Program.cs");
        await File.WriteAllTextAsync(program, programSource);
        File.Copy(Path.Combine(example, "appsettings.json"), Path.Combine(consumer, "appsettings.json"));
        File.Copy(globalJson, Path.Combine(consumer, "global.json"));

        var helpDirectory = Path.Combine(consumer, "help");
        Directory.CreateDirectory(helpDirectory);
        var helpProgram = Path.Combine(helpDirectory, "Program.cs");
        await File.WriteAllTextAsync(
            helpProgram,
            programSource.Replace(resolveMethod, throwingResolveMethod, StringComparison.Ordinal));
        File.Copy(Path.Combine(example, "appsettings.json"), Path.Combine(helpDirectory, "appsettings.json"));

        var caller = Path.Combine(consumer, "caller");
        Directory.CreateDirectory(caller);
        await File.WriteAllTextAsync(
            Path.Combine(caller, "appsettings.json"),
            """
            {
              "Demo": {
                "Port": 4444,
                "Region": "caller-cwd",
                "Endpoint": "https://caller.example.test",
                "EvidencePath": "caller.json",
                "Credentials": {
                  "ApiKey": {
                    "key": "synthetic",
                    "version": "1",
                    "provider": "file-based-sample"
                  }
                }
              }
            }
            """);

        var sdk = await TestHost.RunAsync(
            TestHost.Command(TestHost.Dotnet, caller, "--version"),
            TimeSpan.FromSeconds(30));
        TestHost.Check(sdk.ExitCode == 0, $"Could not query the .NET SDK.\n{sdk.StandardError}");
        TestHost.Check(
            sdk.StandardOutput.Trim() == pinnedSdkVersion,
            $"Expected .NET SDK {pinnedSdkVersion}, got {sdk.StandardOutput.Trim()}.");

        var artifacts = Path.Combine(consumer, ".artifacts");
        var helpResult = await RunConsumerAsync(
            helpProgram,
            ["--help"],
            Path.Combine(consumer, ".artifacts-help"));
        AssertSuccess(helpResult, "help", "inspect");
        TestHost.Check(
            !OutputText(helpResult).Contains(HelpSecretFailureMarker, StringComparison.Ordinal),
            "Help invoked the secret provider.");

        var defaultResult = await RunConsumerAsync(program, ["inspect"]);
        AssertSuccess(
            defaultResult,
            "default settings",
            $"Effective port: 5454. Effective region: us-east-1. {SecretResolutionMarker}");

        var explicitResult = await RunConsumerAsync(
            program,
            ["inspect", "--port", "5055", "--region", "ap-southeast-2"]);
        AssertSuccess(
            explicitResult,
            "explicit arguments",
            $"Effective port: 5055. Effective region: ap-southeast-2. {SecretResolutionMarker}");

        var malformedSettings = Path.Combine(consumer, "malformed-settings");
        Directory.CreateDirectory(malformedSettings);
        File.Copy(program, Path.Combine(malformedSettings, "Program.cs"));
        var malformedSettingsJson = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(example, "appsettings.json")))!
            .AsObject();
        malformedSettingsJson["Demo"]!["Port"] = "invalid-port";
        await File.WriteAllTextAsync(
            Path.Combine(malformedSettings, "appsettings.json"),
            malformedSettingsJson.ToJsonString());
        var malformedJsonResult = await RunConsumerAsync(
            Path.Combine(malformedSettings, "Program.cs"),
            ["inspect", "--port", "5056"],
            Path.Combine(malformedSettings, ".artifacts"));
        AssertSuccess(
            malformedJsonResult,
            "explicit port over malformed JSON value",
            $"Effective port: 5056. Effective region: us-east-1. {SecretResolutionMarker}");

        var malformedEnvironmentResult = await RunConsumerAsync(
            program,
            ["inspect", "--port", "5057"],
            extraEnvironment: new Dictionary<string, string> { ["DEMO__PORT"] = "not-a-port" });
        AssertSuccess(
            malformedEnvironmentResult,
            "explicit port over malformed environment value",
            $"Effective port: 5057. Effective region: us-east-1. {SecretResolutionMarker}");

        var malformedDefaultResult = await RunConsumerAsync(
            Path.Combine(malformedSettings, "Program.cs"),
            ["inspect"],
            Path.Combine(malformedSettings, ".artifacts"));
        TestHost.Check(
            malformedDefaultResult.ExitCode != 0,
            $"Malformed configured port unexpectedly succeeded without an explicit switch.\n{SafeOutput(malformedDefaultResult)}");
        AssertSecretRedacted(malformedDefaultResult, "malformed configured port");

        var invalidPortResult = await RunConsumerAsync(
            program,
            ["inspect", "--port", "70000"]);
        TestHost.Check(
            invalidPortResult.ExitCode != 0,
            $"Out-of-range port unexpectedly succeeded.\n{SafeOutput(invalidPortResult)}");
        TestHost.Check(
            OutputText(invalidPortResult).Contains("between 1 and 65535", StringComparison.Ordinal),
            $"Out-of-range port did not report range validation.\n{SafeOutput(invalidPortResult)}");
        TestHost.Check(
            !OutputText(invalidPortResult).Contains(SecretResolutionMarker, StringComparison.Ordinal),
            "Invalid settings reached the successful command output.");
        AssertSecretRedacted(invalidPortResult, "invalid port");

        Console.WriteLine($"File-based package-consumer smoke checks passed with .NET SDK {pinnedSdkVersion}.");

        async Task<CommandResult> RunConsumerAsync(
            string source,
            string[] arguments,
            string? artifactsPath = null,
            Dictionary<string, string>? extraEnvironment = null)
        {
            artifactsPath ??= artifacts;
            var commandArguments = new[]
            {
                "run", "--file", source, "--artifacts-path", artifactsPath, "--"
            }.Concat(arguments).ToArray();
            var start = TestHost.Command(TestHost.Dotnet, caller, commandArguments);
            start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            start.Environment["DOTNET_NOLOGO"] = "1";
            start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";

            var isolatedNuGetDirectory = Path.Combine(consumer, "nuget");
            start.Environment["NUGET_PACKAGES"] = Path.Combine(isolatedNuGetDirectory, "packages");
            start.Environment["NUGET_HTTP_CACHE_PATH"] = Path.Combine(isolatedNuGetDirectory, "http");
            start.Environment["NUGET_PLUGINS_CACHE_PATH"] = Path.Combine(isolatedNuGetDirectory, "plugins");
            start.Environment["NUGET_SCRATCH"] = Path.Combine(isolatedNuGetDirectory, "scratch");

            foreach (var key in start.Environment.Keys
                .Where(key => key.StartsWith("DEMO__", StringComparison.OrdinalIgnoreCase))
                .ToArray())
            {
                start.Environment.Remove(key);
            }

            if (extraEnvironment is not null)
            {
                foreach (var (key, value) in extraEnvironment)
                {
                    start.Environment[key] = value;
                }
            }

            return await TestHost.RunAsync(start, TimeSpan.FromSeconds(180));
        }

        void AssertSuccess(CommandResult result, string description, string expectedOutput)
        {
            TestHost.Check(
                result.ExitCode == 0,
                $"{description} exited {result.ExitCode}.\n{SafeOutput(result)}");
            TestHost.Check(
                OutputText(result).Contains(expectedOutput, StringComparison.Ordinal),
                $"{description} missed {expectedOutput}.\n{SafeOutput(result)}");
            AssertSecretRedacted(result, description);
        }

        void AssertSecretRedacted(CommandResult result, string description)
        {
            TestHost.Check(
                !OutputText(result).Contains(SyntheticSecret, StringComparison.Ordinal),
                $"{description} printed the synthetic secret.\n{SafeOutput(result)}");
        }

        string OutputText(CommandResult result) => $"{result.StandardOutput}\n{result.StandardError}";

        string SafeOutput(CommandResult result) =>
            OutputText(result).Replace(SyntheticSecret, "[redacted synthetic secret]", StringComparison.Ordinal);
    }

    private static string NormalizeNewlines(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
}
