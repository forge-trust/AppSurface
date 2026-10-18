using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests;

/// <summary>Checks current-run native measurements before the standard Coverlet collector merge consumes them.</summary>
/// <remarks>Files are measurements, not admission, process ownership, a registered proof, or a gate capability.</remarks>
public sealed class NativeMeasurementTests
{
    private const string Module = "ForgeTrust.AppSurface.Evidence.Supervision.dll";
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
        var coverage = Read(directory, "native-supervision.json");
        RootFilesystemMeasurement.Validate(binding.RootElement.GetProperty("root_file_tests"),
            Read(directory, "root-files.trx"));
        Validate(binding.RootElement, head.Trim(), source, native, coverage);
    }

    private static byte[] Read(string directory, string name)
    {
        var path = Path.Join(directory, name);
        Assert.True(File.Exists(path), "Prepare the current source-bound Linux native measurement before running the complete gate.");
        using var stream = File.OpenRead(path);
        Assert.InRange(stream.Length, 1L, 8L * 1024 * 1024);
        var data = new byte[checked((int)stream.Length)];
        stream.ReadExactly(data);
        Assert.Equal(-1, stream.ReadByte());
        return data;
    }

    private static void Validate(JsonElement binding, string head, byte[] source, byte[] native, byte[] coverage)
    {
        Assert.Equal("issue779-native-instrumentation-v1", binding.GetProperty("schema").GetString());
        Assert.Equal(head, binding.GetProperty("source").GetString());
        Assert.Equal("coverlet.console/10.0.1", binding.GetProperty("collector").GetString());
        foreach (var item in new[] { ("source_capture", source), ("native_receipt", native), ("coverage", coverage) })
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(item.Item2)), binding.GetProperty(item.Item1 + "_sha256").GetString());
        var original = binding.GetProperty("original").GetProperty(Module).GetString();
        var instrumented = binding.GetProperty("instrumented").GetProperty(Module).GetString();
        Assert.Matches("^[0-9a-f]{64}$", original!);
        Assert.Matches("^[0-9a-f]{64}$", instrumented!);
        Assert.NotEqual(original, instrumented);
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
        using var report = JsonDocument.Parse(coverage);
        var module = Assert.Single(report.RootElement.EnumerateObject());
        Assert.Equal(Module, Path.GetFileName(module.Name));
        Assert.NotEmpty(module.Value.EnumerateObject());
        var observedNativeRoot = false;
        foreach (var document in module.Value.EnumerateObject())
        {
            if (Path.GetFileName(document.Name) != "LinuxEmptyObservationExecution.cs") continue;
            observedNativeRoot = document.Value.EnumerateObject()
                .SelectMany(static type => type.Value.EnumerateObject())
                .SelectMany(static method => method.Value.GetProperty("Lines").EnumerateObject())
                .Any(static line => line.Value.GetInt32() > 0);
        }
        Assert.True(observedNativeRoot, "The report must contain actual guarded native root execution hits.");
    }

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
