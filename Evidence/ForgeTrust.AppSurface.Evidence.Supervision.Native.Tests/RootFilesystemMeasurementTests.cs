using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests;

/// <summary>Malformed measurement controls are data validation only, never root/native test evidence.</summary>
public sealed class RootFilesystemMeasurementTests
{
    [Fact]
    public void CompleteBoundedTrxMatchesItsBinding()
    {
        var bytes = Trx();
        using var binding = Binding(bytes);
        RootFilesystemMeasurement.Validate(binding.RootElement, bytes);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("authority")]
    [InlineData("binding-count")]
    [InlineData("total")]
    [InlineData("executed")]
    [InlineData("passed")]
    [InlineData("failed")]
    [InlineData("error")]
    [InlineData("notExecuted")]
    [InlineData("warning")]
    [InlineData("outcome")]
    [InlineData("name")]
    [InlineData("missing-class")]
    [InlineData("namespace")]
    [InlineData("entity")]
    [InlineData("oversize")]
    public void IncompleteOrForeignMeasurementsCannotValidate(string invalid)
    {
        var text = Encoding.UTF8.GetString(Trx());
        if (invalid is "total" or "executed" or "passed") text = text.Replace(invalid + "=\"2\"", invalid + "=\"1\"", StringComparison.Ordinal);
        if (invalid is "failed" or "error" or "notExecuted" or "warning") text = text.Replace(invalid + "=\"0\"", invalid + "=\"1\"", StringComparison.Ordinal);
        if (invalid == "outcome") text = text.Replace("outcome=\"Passed\"", "outcome=\"Skipped\"", StringComparison.Ordinal);
        if (invalid == "name") text = text.Replace("LinuxProtectedNodeNativeTests", "ForeignTests", StringComparison.Ordinal);
        if (invalid == "missing-class") text = text.Replace("LinuxRetainedReportNativeTests", "LinuxProtectedNodeNativeTests", StringComparison.Ordinal);
        if (invalid == "namespace") text = text.Replace("http://microsoft.com/schemas/VisualStudio/TeamTest/2010", "urn:foreign", StringComparison.Ordinal);
        if (invalid == "entity") text = "<!DOCTYPE TestRun [<!ENTITY external SYSTEM 'file:///unavailable'>]>" + text;
        var bytes = invalid == "oversize" ? new byte[1024 * 1024 + 1] : Encoding.UTF8.GetBytes(text);
        using var binding = Binding(bytes, invalid);
        Assert.ThrowsAny<Exception>(() => RootFilesystemMeasurement.Validate(binding.RootElement, bytes));
    }

    private static byte[] Trx() => Encoding.UTF8.GetBytes("""
        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><ResultSummary><Counters total="2" executed="2" passed="2" failed="0" error="0" notExecuted="0" warning="0" /></ResultSummary><Results><UnitTestResult testName="ForgeTrust.AppSurface.Evidence.Supervision.Tests.LinuxProtectedNodeNativeTests.Example" outcome="Passed" /><UnitTestResult testName="ForgeTrust.AppSurface.Evidence.Supervision.Tests.LinuxRetainedReportNativeTests.Example" outcome="Passed" /></Results></TestRun>
        """);

    private static JsonDocument Binding(byte[] bytes, string invalid = "") => JsonDocument.Parse(JsonSerializer.Serialize(new
    {
        trx_sha256 = invalid == "hash" ? new string('0', 64) : Convert.ToHexStringLower(SHA256.HashData(bytes)),
        executed = invalid == "binding-count" ? 3 : 2,
        passed = 2,
        native_acceptance = invalid == "authority",
    }));
}
