using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Existing private diagnostic encoding only; no root holder, filesystem retention or native authority.</summary>
public sealed class LinuxProducerFailureStateTests
{
    [Theory]
    [InlineData("binding")]
    [InlineData("results")]
    [InlineData("pipes")]
    [InlineData("backend")]
    [InlineData("start")]
    [InlineData("running")]
    [InlineData("identity")]
    [InlineData("normal-exit")]
    [InlineData("settlement")]
    public async Task DefinedStagePreservesMissingFactsWithoutInventingExitOrEchoingOutput(string stage)
    {
        var output = await CollectedOutput();
        var bytes = LinuxProducerFailureState.Encode(output, stage, null, null, Pending(), false, true, false, false, null, null);
        Assert.InRange(bytes.Length, 1, 4096);
        Assert.DoesNotContain("raw-output-canary", Encoding.UTF8.GetString(bytes));
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        Assert.Equal(22, root.EnumerateObject().Count());
        Assert.Equal("evidence-producer-failure-state-v1", root.GetProperty("schema").GetString());
        Assert.Equal(stage, root.GetProperty("stage").GetString());
        foreach (var field in new[] { "normal_code", "normal_status", "stopped_code", "stopped_status", "captured_pid", "last_main_pid", "last_exec_pid", "last_code", "last_status" })
            Assert.Equal(JsonValueKind.Null, root.GetProperty(field).ValueKind);
        Assert.Equal("unknown", root.GetProperty("last_active").GetString());
        Assert.Equal("unknown", root.GetProperty("last_substate").GetString());
        Assert.False(root.GetProperty("physically_settled").GetBoolean());
        Assert.True(root.GetProperty("cleanup_failed").GetBoolean());
        Assert.False(root.GetProperty("group_empty_twice").GetBoolean());
        Assert.False(root.GetProperty("pump_joined").GetBoolean());
        Assert.True(root.GetProperty("output_drained").GetBoolean()); // EOF data is not physical-exit proof.
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Running")]
    [InlineData("raw-stage-canary")]
    public async Task UndefinedStageRejectsFixedBeforeAnyNativeRetention(string? stage)
    {
        var output = await CollectedOutput();
        var error = Assert.Throws<EvidenceAdmissionException>(() => LinuxProducerFailureState.Encode(output, stage!, null, null, Pending(), false, true, false, false, null, null));
        Assert.Equal("ASEVD402", error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("raw-stage-canary", error.Message);
    }

    [Theory]
    [InlineData("active")]
    [InlineData("inactive")]
    [InlineData("deactivating")]
    [InlineData("failed")]
    [InlineData("running")]
    [InlineData("exited")]
    [InlineData("dead")]
    [InlineData("stop")]
    [InlineData("stop-sigterm")]
    [InlineData("stop-sigkill")]
    [InlineData("stop-post")]
    [InlineData("final-sigterm")]
    [InlineData("final-sigkill")]
    [InlineData("Active")]
    [InlineData("raw-state-canary")]
    public async Task StateVocabularyCopiesOnlyClosedTextAndRetainsNumericFailureFacts(string state)
    {
        var known = state is not ("Active" or "raw-state-canary");
        var sample = new LinuxUnitProperties("raw-unit-canary", "loaded", state, state, "/raw-cgroup-canary", 0, 1234, 1, 17, "65012", "65013", "exec", "control-group", true, 120_000_000, 5_000_000);
        var output = await CollectedOutput();
        var bytes = LinuxProducerFailureState.Encode(output, "settlement", sample with { ExecMainCode = 2, ExecMainStatus = 6 }, sample,
            Pending() with { FirstFailure = SupervisionPendingStartFailure.StopFailed }, true, false, true, true, 1234, sample);
        var text = Encoding.UTF8.GetString(bytes);
        foreach (var secret in new[] { "raw-unit-canary", "/raw-cgroup-canary", "raw-state-canary", "raw-output-canary" }) Assert.DoesNotContain(secret, text);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        Assert.Equal(known ? state : "unknown", root.GetProperty("last_active").GetString());
        Assert.Equal(known ? state : "unknown", root.GetProperty("last_substate").GetString());
        Assert.Equal(2, root.GetProperty("normal_code").GetInt32());
        Assert.Equal(6, root.GetProperty("normal_status").GetInt32());
        Assert.Equal(1, root.GetProperty("stopped_code").GetInt32());
        Assert.Equal(17, root.GetProperty("stopped_status").GetInt32());
        Assert.Equal(1234, root.GetProperty("captured_pid").GetInt32());
        Assert.Equal(0, root.GetProperty("last_main_pid").GetInt32());
        Assert.Equal(1234, root.GetProperty("last_exec_pid").GetInt32());
        Assert.Equal(1, root.GetProperty("last_code").GetInt32());
        Assert.Equal(17, root.GetProperty("last_status").GetInt32());
        Assert.Equal("StopFailed", root.GetProperty("pending_failure").GetString());
        Assert.True(root.GetProperty("start_reserved").GetBoolean());
        Assert.True(root.GetProperty("start_joined").GetBoolean());
        Assert.True(root.GetProperty("stop_joined").GetBoolean());
    }

    private static SupervisionPendingStartSnapshot Pending() => new(true, true, true, true, true, SupervisionPendingStartFailure.None);

    private static async Task<SupervisionOutputReceipt> CollectedOutput()
    {
        using var stdout = new MemoryStream("raw-output-canary"u8.ToArray());
        using var stderr = new MemoryStream();
        return await new SupervisionOutputCollector(128, 128).CollectAsync(stdout, stderr, CancellationToken.None);
    }
}
