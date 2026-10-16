using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Supervision;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Real stream counters projected as diagnostic data, with no native ownership claim.</summary>
public sealed class LinuxCoverageOutputObservationTests
{
    [Theory]
    [InlineData(3, false)]
    [InlineData(4, true)]
    public async Task Encode_PreservesActualJoinedCountersWithoutRawPrefixes(int count, bool overflow)
    {
        using var budget = new SupervisionRunOutputBudget(3);
        using var output = new MemoryStream(Encoding.UTF8.GetBytes("private-output-canary")[..count]);
        using var error = new MemoryStream();
        await budget.CollectAsync(new SupervisionOutputCollector(3, 3), output, error);
        var receipt = await budget.CompleteAsync();
        var encoded = LinuxCoverageOutputObservation.Encode(receipt);
        Assert.DoesNotContain("private-output-canary", Encoding.UTF8.GetString(encoded));
        Assert.True(encoded.Length < 4096);
        using var document = JsonDocument.Parse(encoded);
        var root = document.RootElement;
        Assert.Equal("evidence-coverage-output-v1", root.GetProperty("schema").GetString());
        Assert.Equal(count, root.GetProperty("received_bytes").GetInt64());
        Assert.Equal(3, root.GetProperty("received_byte_limit").GetInt64());
        Assert.Equal(overflow, root.GetProperty("quota_exceeded").GetBoolean());
        Assert.Equal(1, root.GetProperty("registered_pairs").GetInt32());
        var pair = root.GetProperty("pairs")[0];
        Assert.Equal(count, pair.GetProperty("stdout_bytes").GetInt64());
        Assert.Equal(0, pair.GetProperty("stderr_bytes").GetInt64());
        Assert.Equal(overflow, pair.GetProperty("quota_exceeded").GetBoolean());
        Assert.Equal(receipt.Pairs[0].Stdout.EndOfStream, pair.GetProperty("stdout_eof").GetBoolean());
        Assert.Equal(receipt.Pairs[0].Stderr.EndOfStream, pair.GetProperty("stderr_eof").GetBoolean());
    }
}
