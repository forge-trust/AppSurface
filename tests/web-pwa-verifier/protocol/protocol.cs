#!/usr/bin/env dotnet
#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property ImplicitUsings=enable
#:property Nullable=enable
#:property PublishAot=false
#:include ../../../scripts/web-pwa/PwaVerifier.cs

using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

var validSuccess = """{"schemaVersion":3,"surface":"all","passed":true,"install":{"manifestPath":"/manifest.webmanifest","icons":[]},"push":{"worker":{},"registrationHelper":{}},"diagnostics":[]}""";
var validFailure = validSuccess.Replace("true", "false", StringComparison.Ordinal);
await Accepts(validSuccess, 0);
await Accepts(validFailure, 7);
await Accepts(validSuccess.Replace("\"diagnostics\":[]", "\"diagnostics\":[{\"message\":\"café 🐾\"}]", StringComparison.Ordinal), 0);

foreach (var (property, invalid) in new (string, JsonNode?)[]
{
    ("schemaVersion", 2), ("schemaVersion", "3"), ("surface", 3), ("surface", "install"),
    ("passed", "true"), ("install", new JsonObject()), ("push", new JsonObject()), ("diagnostics", new JsonObject())
})
{
    var mutated = JsonNode.Parse(validSuccess)!.AsObject();
    mutated[property] = invalid;
    await Rejects(Encoding.UTF8.GetBytes(mutated.ToJsonString()), 0);
    mutated.Remove(property);
    await Rejects(Encoding.UTF8.GetBytes(mutated.ToJsonString()), 0);
}

foreach (var invalid in new[] { "", "null", "[]", "{}", validSuccess[..^1], validSuccess + "{}", validSuccess + " log" })
{
    await Rejects(Encoding.UTF8.GetBytes(invalid), 0);
}

await Rejects(Encoding.UTF8.GetBytes(validSuccess), 1);
await Rejects(Encoding.UTF8.GetBytes(validFailure), 0);
var invalidUtf8 = Encoding.UTF8.GetBytes(validSuccess.Replace("\"diagnostics\":[]", "\"diagnostics\":[\"X\"]", StringComparison.Ordinal));
invalidUtf8[Array.IndexOf(invalidUtf8, (byte)'X')] = 0xff;
await Rejects(invalidUtf8, 0);

await PublishesValidFailureAndRefusesMalformedOutput(validFailure);

Console.WriteLine("PWA report protocol checks passed.");

static async Task Accepts(string json, int exitCode) => await AcceptBytes(Encoding.UTF8.GetBytes(json), exitCode);

static async Task AcceptBytes(byte[] bytes, int exitCode)
{
    await using var report = new MemoryStream(bytes, writable: false);
    await PwaVerifier.ValidateReportAsync(report, exitCode, CancellationToken.None);
}

static async Task Rejects(byte[] bytes, int exitCode)
{
    await using var report = new MemoryStream(bytes, writable: false);
    try
    {
        await PwaVerifier.ValidateReportAsync(report, exitCode, CancellationToken.None);
    }
    catch (Exception)
    {
        return;
    }

    throw new InvalidOperationException($"Expected report rejection for {Encoding.UTF8.GetString(bytes)}");
}

static async Task PublishesValidFailureAndRefusesMalformedOutput(string failedReport)
{
    var fixtureRoot = Path.Combine(Path.GetTempPath(), $"pwa-protocol-{Guid.NewGuid():N}");
    Directory.CreateDirectory(fixtureRoot);
    try
    {
        await CreateFixtureCheckout(fixtureRoot);
        var evidenceDirectory = Path.Combine(fixtureRoot, "evidence");
        Directory.CreateDirectory(evidenceDirectory);
        var evidence = Path.Combine(evidenceDirectory, "report.json");

        var validPort = FreePort();
        var valid = await RunFixtureRunner(fixtureRoot, validPort, evidence, failedReport, 7);
        if (valid.ExitCode != 7 || await File.ReadAllTextAsync(evidence) != failedReport)
        {
            throw new InvalidOperationException($"A valid failed report was not published with the CLI's nonzero exit code. Runner={valid.ExitCode}. {valid.StandardError}");
        }
        AssertNoTemporaryReports(evidenceDirectory);
        AssertPortReusable(validPort);

        const string previous = "previous-evidence-preserved";
        await File.WriteAllTextAsync(evidence, previous);
        var malformedPort = FreePort();
        var malformed = await RunFixtureRunner(fixtureRoot, malformedPort, evidence, "not-json", 7);
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

        // File.Move cannot replace a directory. This tests publication itself on every platform.
        var occupied = Directory.CreateDirectory(Path.Combine(evidenceDirectory, "occupied"));
        var sentinel = Path.Combine(occupied.FullName, "prior");
        await File.WriteAllTextAsync(sentinel, previous);
        var occupiedPort = FreePort();
        var rejected = await RunFixtureRunner(fixtureRoot, occupiedPort, occupied.FullName, failedReport, 7);
        if (rejected.ExitCode != 1 || await File.ReadAllTextAsync(sentinel) != previous
            || !rejected.StandardError.Contains("publishing the evidence file", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unexpected result for an unreplaceable destination. {rejected.StandardError}");
        }
        AssertNoTemporaryReports(evidenceDirectory);
        AssertPortReusable(occupiedPort);
    }
    finally
    {
        Directory.Delete(fixtureRoot, recursive: true);
    }
}

static async Task CreateFixtureCheckout(string root)
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
        app.MapGet("/_appsurface/verify/instance", () => Results.Text(Environment.GetEnvironmentVariable("APPSURFACE_PWA_VERIFY_INSTANCE")!));
        app.MapGet("/{**path}", () => Results.Ok());
        await app.RunAsync();
        """);
    await File.WriteAllTextAsync(Path.Combine(cliDirectory, "ForgeTrust.AppSurface.Cli.csproj"), """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
        </Project>
        """);
    await File.WriteAllTextAsync(Path.Combine(cliDirectory, "Program.cs"), """
        Console.Write(File.ReadAllText("fixture-output.txt"));
        return int.Parse(File.ReadAllText("fixture-exit.txt"), System.Globalization.CultureInfo.InvariantCulture);
        """);
}

static async Task<(int ExitCode, string StandardError)> RunFixtureRunner(
    string root, int port, string evidence, string output, int childExit)
{
    await File.WriteAllTextAsync(Path.Combine(root, "fixture-output.txt"), output);
    await File.WriteAllTextAsync(Path.Combine(root, "fixture-exit.txt"), childExit.ToString(System.Globalization.CultureInfo.InvariantCulture));
    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    using var diagnostics = new StringWriter();
    var previousError = Console.Error;
    try
    {
        Console.SetError(diagnostics);
        var code = await PwaVerifier.RunAsync(root, port, evidence, TimeSpan.FromSeconds(20), deadline.Token);
        return (code, diagnostics.ToString());
    }
    finally
    {
        Console.SetError(previousError);
    }
}

static int FreePort()
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    return ((IPEndPoint)listener.LocalEndpoint).Port;
}

static void AssertPortReusable(int port)
{
    using var listener = new TcpListener(IPAddress.Loopback, port);
    listener.Start();
}

static void AssertNoTemporaryReports(string directory)
{
    if (Directory.EnumerateFiles(directory, ".pwa-verify-*.tmp").Any())
    {
        throw new InvalidOperationException("The runner left a temporary evidence report behind.");
    }
}
