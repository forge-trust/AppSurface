using System.Text.Json;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Checks the closed JSON wire shapes produced by the root control reply codec.</summary>
public sealed class SupervisionControlReplyDataTests
{
    [Fact]
    public void ApplicationStartReplyRetainsTheExistingReceiptFieldsAndFixedFlags()
    {
        using var document = JsonDocument.Parse(SupervisionControlReplyData.ApplicationStart(
            "0123456789abcdef0123456789abcdef", 4312, 61001, 61002,
            "/system.slice/issue779-app-0123456789abcdef0123456789abcdef.service"));
        var root = document.RootElement;

        AssertProperties(root, "ok", "lease_id", "apphost_pid", "application_uid", "application_gid",
            "cgroup", "owned");
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal("0123456789abcdef0123456789abcdef", root.GetProperty("lease_id").GetString());
        Assert.Equal(4312, root.GetProperty("apphost_pid").GetInt32());
        Assert.Equal(61001u, root.GetProperty("application_uid").GetUInt32());
        Assert.Equal(61002u, root.GetProperty("application_gid").GetUInt32());
        Assert.Equal("/system.slice/issue779-app-0123456789abcdef0123456789abcdef.service",
            root.GetProperty("cgroup").GetString());
        Assert.True(root.GetProperty("owned").GetBoolean());
    }

    [Fact]
    public void ApplicationReadinessReplyRetainsTheExactRootObservedReceiptFields()
    {
        using var document = JsonDocument.Parse(SupervisionControlReplyData.ApplicationReadiness(
            "0123456789abcdef0123456789abcdef", "healthz", 61001,
            "/system.slice/issue779-app-0123456789abcdef0123456789abcdef.service", 200, 4096));
        var root = document.RootElement;

        AssertProperties(root, "ok", "lease_id", "resource_id", "application_uid", "cgroup",
            "kernel_peer_checked", "http_status", "healthy", "received_bytes");
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal("0123456789abcdef0123456789abcdef", root.GetProperty("lease_id").GetString());
        Assert.Equal("healthz", root.GetProperty("resource_id").GetString());
        Assert.Equal(61001u, root.GetProperty("application_uid").GetUInt32());
        Assert.Equal(200, root.GetProperty("http_status").GetInt32());
        Assert.True(root.GetProperty("kernel_peer_checked").GetBoolean());
        Assert.True(root.GetProperty("healthy").GetBoolean());
        Assert.Equal(4096, root.GetProperty("received_bytes").GetInt64());
    }

    [Fact]
    public void RunReplyPreservesUnicodeAndEscapedOutputWithoutChangingValues()
    {
        const string stdout = "stdout π\nsecond line";
        const string stderr = "quote: \"π\"; slash: \\";
        using var document = JsonDocument.Parse(SupervisionControlReplyData.Run(7, stdout, stderr, true, 8123));
        var root = document.RootElement;

        AssertProperties(root, "ok", "exit_code", "stdout", "stderr", "output_truncated", "received_bytes");
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal(7, root.GetProperty("exit_code").GetInt32());
        Assert.Equal(stdout, root.GetProperty("stdout").GetString());
        Assert.Equal(stderr, root.GetProperty("stderr").GetString());
        Assert.True(root.GetProperty("output_truncated").GetBoolean());
        Assert.Equal(8123, root.GetProperty("received_bytes").GetInt64());
    }

    [Fact]
    public void InventoryReplyPreservesOrderedPathAndLengthProjection()
    {
        IReadOnlyList<LinuxRetainedReportSet.ReportInfo> reports =
        [
            new("reports/π.trx", 23, new string('a', 64)),
            new("nested/result.json", 0, new string('b', 64)),
        ];
        using var document = JsonDocument.Parse(SupervisionControlReplyData.Inventory(reports));
        var root = document.RootElement;
        var artifacts = root.GetProperty("artifacts").EnumerateArray().ToArray();

        AssertProperties(root, "ok", "artifacts");
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal(2, artifacts.Length);
        AssertProperties(artifacts[0], "path", "length_bytes");
        Assert.Equal("reports/π.trx", artifacts[0].GetProperty("path").GetString());
        Assert.Equal(23, artifacts[0].GetProperty("length_bytes").GetInt64());
        Assert.Equal("nested/result.json", artifacts[1].GetProperty("path").GetString());
        Assert.Equal(0, artifacts[1].GetProperty("length_bytes").GetInt64());
    }

    [Fact]
    public void EmptyInventoryReplyRetainsAnEmptyArtifactArray()
    {
        using var document = JsonDocument.Parse(SupervisionControlReplyData.Inventory(
            Array.Empty<LinuxRetainedReportSet.ReportInfo>()));

        AssertProperties(document.RootElement, "ok", "artifacts");
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(0, document.RootElement.GetProperty("artifacts").GetArrayLength());
    }

    [Fact]
    public void ArtifactReplyPreservesExactBytesAsBase64AndTheExistingEndFlag()
    {
        byte[] bytes = [0, 0x20, 0x80, 0xff];
        var bytesBase64 = SupervisionControlReplyData.EncodeArtifactBytes(bytes);
        using var document = JsonDocument.Parse(SupervisionControlReplyData.Artifact(bytesBase64, false));
        var root = document.RootElement;

        AssertProperties(root, "ok", "bytes_base64", "end");
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal(Convert.ToBase64String(bytes), root.GetProperty("bytes_base64").GetString());
        Assert.False(root.GetProperty("end").GetBoolean());
    }

    [Fact]
    public void EmptyArtifactChunkRetainsEmptyBase64AndTheExistingEndFlag()
    {
        var bytesBase64 = SupervisionControlReplyData.EncodeArtifactBytes([]);
        using var document = JsonDocument.Parse(SupervisionControlReplyData.Artifact(bytesBase64, true));
        var root = document.RootElement;

        AssertProperties(root, "ok", "bytes_base64", "end");
        Assert.Equal(string.Empty, root.GetProperty("bytes_base64").GetString());
        Assert.True(root.GetProperty("end").GetBoolean());
    }

    private static void AssertProperties(JsonElement element, params string[] expected)
    {
        var actual = element.EnumerateObject().Select(static property => property.Name).ToArray();
        Assert.Equal(expected.Length, actual.Length);
        Assert.Equal(expected.OrderBy(static name => name, StringComparer.Ordinal),
            actual.OrderBy(static name => name, StringComparer.Ordinal));
    }
}
