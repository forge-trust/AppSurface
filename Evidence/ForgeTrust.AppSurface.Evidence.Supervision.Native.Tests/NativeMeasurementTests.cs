using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests;

/// <summary>Checks current-run native measurements before the standard Coverlet collector merge consumes them.</summary>
/// <remarks>Files are measurements, not admission, process ownership, a registered proof, or a gate capability.</remarks>
public sealed class NativeMeasurementTests
{
    private const string Module = "ForgeTrust.AppSurface.Evidence.Supervision.dll";
    private static readonly string[] Modules = ["ForgeTrust.AppSurface.Cli.dll", "ForgeTrust.AppSurface.Evidence.Contracts.dll", "ForgeTrust.AppSurface.Evidence.Planner.dll",
        "ForgeTrust.AppSurface.Evidence.Coverage.dll", "ForgeTrust.AppSurface.Evidence.Aspire.dll", Module];
    private static readonly string[] Cases = ["normal", "sdk-normal", "readiness", "factory-stall", "cancel", "stuck-descendant", "disposal-failure"];

    [LinuxMeasurementFact]
    public async Task LinuxNativeMeasurementsMatchTheCurrentSourceAndAllSevenPhysicalControls()
    {
        var repo = FindRepository();
        var directory = Path.Join(repo, "TestResults", "issue779-native-measurement");
        var info = new ProcessStartInfo("git") { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("rev-parse"); info.ArgumentList.Add("HEAD");
        using var process = Process.Start(info)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        string head;
        try
        {
            head = await process.StandardOutput.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
        Assert.Equal(0, process.ExitCode);
        using var binding = JsonDocument.Parse(Read(directory, "measurement-binding.json"));
        var source = Read(directory, "source.json");
        var native = Read(directory, "native-receipt.json");
        var coverage = Read(directory, "native-supervision.json", 32L * 1024 * 1024);
        var sdkCoverage = Read(directory, "native-sdk.json", 32L * 1024 * 1024);
        RootFilesystemMeasurement.Validate(binding.RootElement.GetProperty("root_file_tests"),
            Read(directory, "root-files.trx"));
        Validate(binding.RootElement, head.Trim(), source, native, coverage, sdkCoverage);
    }

    private static byte[] Read(string directory, string name, long maximum = 8L * 1024 * 1024)
    {
        var path = Path.Join(directory, name);
        Assert.True(File.Exists(path), "Prepare the current source-bound Linux native measurement before running the complete gate.");
        using var stream = File.OpenRead(path);
        Assert.InRange(stream.Length, 1L, maximum);
        var data = new byte[checked((int)stream.Length)];
        stream.ReadExactly(data);
        Assert.Equal(-1, stream.ReadByte());
        return data;
    }

    private static void Validate(JsonElement binding, string head, byte[] source, byte[] native, byte[] coverage, byte[] sdkCoverage)
    {
        Assert.Equal("issue779-native-instrumentation-v1", binding.GetProperty("schema").GetString());
        Assert.Equal(head, binding.GetProperty("source").GetString());
        Assert.Equal("coverlet.console/10.0.1", binding.GetProperty("collector").GetString());
        foreach (var item in new[] { ("source_capture", source), ("native_receipt", native), ("coverage", coverage), ("sdk_coverage", sdkCoverage) })
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(item.Item2)), binding.GetProperty(item.Item1 + "_sha256").GetString());
        foreach (var prefix in new[] { string.Empty, "sdk_" })
        foreach (var name in Modules)
        {
            var original = binding.GetProperty(prefix + "original").GetProperty(name).GetString();
            var instrumented = binding.GetProperty(prefix + "instrumented").GetProperty(name).GetString();
            Assert.Matches("^[0-9a-f]{64}$", original!);
            Assert.Matches("^[0-9a-f]{64}$", instrumented!);
            Assert.NotEqual(original, instrumented);
            var pdb = Path.ChangeExtension(name, ".pdb");
            Assert.Matches("^[0-9a-f]{64}$", binding.GetProperty(prefix + "original").GetProperty(pdb).GetString()!);
            Assert.Matches("^[0-9a-f]{64}$", binding.GetProperty(prefix + "instrumented").GetProperty(pdb).GetString()!);
        }
        using var sourceDocument = JsonDocument.Parse(source);
        Assert.Equal(head, sourceDocument.RootElement.GetProperty("source").GetString());
        using var actual = JsonDocument.Parse(native);
        var root = actual.RootElement;
        Assert.Equal("issue779-csharp-checkpoint3-native-v1", root.GetProperty("schema").GetString());
        Assert.Equal(head, root.GetProperty("source").GetString());
        Assert.Equal(0, root.GetProperty("exit").GetInt32());
        Assert.False(root.GetProperty("trusted_eligibility").GetBoolean());
        var cases = root.GetProperty("cases").EnumerateArray().ToArray();
        Assert.Equal(Cases, cases.Select(static row => row.GetProperty("case").GetString()));
        foreach (var row in cases)
        {
            Assert.True(row.GetProperty("passed").GetBoolean());
            Assert.True(row.GetProperty("groups_empty").GetBoolean());
            Assert.True(row.GetProperty("final_groups_empty").GetBoolean());
            var settled = Assert.Single(row.GetProperty("application_observations").EnumerateArray(),
                static value => value.GetProperty("phase").GetString() == "settled");
            foreach (var key in new[] { "group_empty_twice", "pumps_joined", "pending_start_joined" })
                Assert.True(settled.GetProperty(key).GetBoolean());
        }
        WorkspaceGuardMeasurement.Validate(root);
        var worker = Assert.Single(cases[0].GetProperty("worker_measurements").EnumerateArray());
        Assert.Equal("evidence-worker-measurement-v1", worker.GetProperty("schema").GetString());
        Assert.True(worker.GetProperty("original_pidfd_exit").GetBoolean());
        Assert.False(worker.GetProperty("native_acceptance").GetBoolean());
        Assert.True(worker.GetProperty("pid").GetUInt32() > 0);
        Assert.True(worker.GetProperty("start_time").GetUInt64() > 0);
        var workerModules = worker.GetProperty("modules").EnumerateArray().ToArray();
        Assert.Equal(Modules.Order(StringComparer.Ordinal), workerModules.Select(static row => row.GetProperty("module").GetString()).Order(StringComparer.Ordinal));
        foreach (var name in new[] { "ForgeTrust.AppSurface.Cli.dll", "ForgeTrust.AppSurface.Evidence.Contracts.dll", "ForgeTrust.AppSurface.Evidence.Coverage.dll" })
        {
            var row = Assert.Single(workerModules, value => value.GetProperty("module").GetString() == name);
            Assert.Equal("unchanged-worker-handoff", row.GetProperty("outcome").GetString());
            Assert.Equal(binding.GetProperty("instrumented").GetProperty(name).GetString(), row.GetProperty("module_sha256").GetString());
            Assert.Matches("^[0-9a-f]{64}$", row.GetProperty("worker_sha256").GetString()!);
            Assert.Equal(row.GetProperty("worker_sha256").GetString(), row.GetProperty("host_sha256").GetString());
        }
        var sdkWorker = Assert.Single(cases[1].GetProperty("worker_measurements").EnumerateArray());
        Assert.Equal("evidence-worker-measurement-v1", sdkWorker.GetProperty("schema").GetString());
        Assert.True(sdkWorker.GetProperty("original_pidfd_exit").GetBoolean());
        Assert.False(sdkWorker.GetProperty("native_acceptance").GetBoolean());
        var sdkModules = sdkWorker.GetProperty("modules").EnumerateArray().ToArray();
        Assert.Equal(Modules.Order(StringComparer.Ordinal), sdkModules.Select(static row => row.GetProperty("module").GetString()).Order(StringComparer.Ordinal));
        foreach (var name in new[] { "ForgeTrust.AppSurface.Cli.dll", "ForgeTrust.AppSurface.Evidence.Contracts.dll", "ForgeTrust.AppSurface.Evidence.Coverage.dll", "ForgeTrust.AppSurface.Evidence.Aspire.dll" })
        {
            var row = Assert.Single(sdkModules, value => value.GetProperty("module").GetString() == name);
            Assert.Equal("unchanged-worker-handoff", row.GetProperty("outcome").GetString());
            Assert.Equal(binding.GetProperty("sdk_instrumented").GetProperty(name).GetString(), row.GetProperty("module_sha256").GetString());
            Assert.Matches("^[0-9a-f]{64}$", row.GetProperty("worker_sha256").GetString()!);
            Assert.Equal(row.GetProperty("worker_sha256").GetString(), row.GetProperty("host_sha256").GetString());
        }
        using var sdkReport = JsonDocument.Parse(sdkCoverage);
        Assert.Equal(Modules.Order(StringComparer.Ordinal), sdkReport.RootElement.EnumerateObject()
            .Select(static value => Path.GetFileName(value.Name)).Order(StringComparer.Ordinal));
        foreach (var name in new[] { "EvidenceHostBootstrap.cs", "EvidenceRestrictedAspireApplication.cs" })
            Assert.Contains(sdkReport.RootElement.EnumerateObject().SelectMany(static module => module.Value.EnumerateObject()),
                document => Path.GetFileName(document.Name) == name && HasHits(document.Value));
        using var report = JsonDocument.Parse(coverage);
        var modules = report.RootElement.EnumerateObject().ToArray();
        Assert.Equal(Modules.Order(StringComparer.Ordinal), modules.Select(static value => Path.GetFileName(value.Name)).Order(StringComparer.Ordinal));
        var observedNativeRoot = false;
        var observedNativeWorker = false;
        foreach (var module in modules)
        foreach (var document in module.Value.EnumerateObject())
        {
            if (Path.GetFileName(document.Name) == "LinuxEmptyObservationExecution.cs")
                observedNativeRoot |= HasHits(document.Value);
            if (Path.GetFileName(document.Name) == "EvidenceLinuxWorkerSupervisor.cs")
                observedNativeWorker |= document.Value.EnumerateObject().SelectMany(static type => type.Value.EnumerateObject())
                    .Where(static method => method.Name.Contains("ConnectAsync", StringComparison.Ordinal))
                    .SelectMany(static method => method.Value.GetProperty("Lines").EnumerateObject())
                    .Any(static line => line.Value.GetInt32() > 0);
        }
        Assert.True(observedNativeRoot, "The report must contain actual guarded native root execution hits.");
        Assert.True(observedNativeWorker, "The report must contain the actual authenticated worker connection, not presumed visibility across PrivateTmp.");
    }

    private static bool HasHits(JsonElement document) => document.EnumerateObject()
        .SelectMany(static type => type.Value.EnumerateObject())
        .SelectMany(static method => method.Value.GetProperty("Lines").EnumerateObject())
        .Any(static line => line.Value.GetInt32() > 0);

    private static string FindRepository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "ForgeTrust.AppSurface.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("The native measurement fixture requires its source checkout.");
    }
}

/// <summary>Native measurements require the prepared Linux lane; ordinary runs report an explicit skip.</summary>
public sealed class LinuxMeasurementFactAttribute : FactAttribute
{
    public LinuxMeasurementFactAttribute()
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("EVIDENCEHOST_NATIVE_MEASUREMENT") != "1")
            Skip = "The source-bound root/systemd measurement requires the prepared Linux runtime workflow.";
    }
}
