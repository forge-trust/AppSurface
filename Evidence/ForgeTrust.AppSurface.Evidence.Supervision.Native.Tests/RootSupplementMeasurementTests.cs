using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests;

/// <summary>Detached receipt parser controls only; synthetic TRX data grants no measured coverage or native credit.</summary>
public sealed class RootSupplementMeasurementTests
{
    [Fact]
    public void ExactSeparateSupplementDataHasAllFixedClassesAndNoNativeAcceptance()
    {
        var bytes = Data();
        using var binding = Binding(bytes);
        RootSupplementMeasurement.Validate(binding.RootElement, bytes);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("foreign-class")]
    [InlineData("missing-new-control")]
    [InlineData("missing-native-handshake")]
    [InlineData("skipped")]
    [InlineData("counter")]
    [InlineData("doctype")]
    public void IncompleteOrUnselectedResultDataCannotReplaceTheOriginalPassingRows(string fault)
    {
        var bytes = Data(fault);
        using var binding = Binding(bytes);
        Assert.ThrowsAny<Exception>(() => RootSupplementMeasurement.Validate(binding.RootElement, bytes));
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("executed")]
    [InlineData("passed")]
    [InlineData("acceptance")]
    public void EachBindingFieldMustDescribeTheExactInformationalSupplement(string fault)
    {
        var bytes = Data();
        using var binding = Binding(bytes, fault);
        Assert.ThrowsAny<Exception>(() => RootSupplementMeasurement.Validate(binding.RootElement, bytes));
    }

    private static byte[] Data(string? fault = null)
    {
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var rows = Enumerable.Range(0, 61).Select(index => new XElement(ns + "UnitTestResult",
            new XAttribute("testName", "ForgeTrust.AppSurface.Evidence.Supervision.Tests."
                + (index < 19 ? "LinuxOwnerGuardNativeTests" : index < 31 ? "LinuxCustodySupplementNativeTests" : index < 44 ? "LinuxProducerFileTreeNativeTests"
                    : index < 52 ? "LinuxWorkspaceSupplementNativeTests" : index == 52 ? "LinuxExecutionOutputNativeTests" : "LinuxWorkerHandshakeNativeTests") + ".Data" + index),
            new XAttribute("outcome", "Passed"))).ToList();
        var requiredNames = new[]
        {
            "NestedDirectorySymlinkCannotSelectAnOutsideReport",
            "ActualReportAtTheByteLimitReadsExactEofAndTheNextByteRejectsBeforeRetention",
            "ReplacedParentOfTheSelectedRootPoisonsTheRetainedSet",
        };
        for (var index = 0; index < requiredNames.Length; index++)
            rows[19 + index].SetAttributeValue("testName",
                "ForgeTrust.AppSurface.Evidence.Supervision.Tests.LinuxCustodySupplementNativeTests." + requiredNames[index]);
        rows[44].SetAttributeValue("testName", "ForgeTrust.AppSurface.Evidence.Supervision.Tests.LinuxWorkspaceSupplementNativeTests.ApplicationWorkspaceInventoryAcceptsTheActualRetainedReadOnlyBundleTree");
        rows[45].SetAttributeValue("testName", "ForgeTrust.AppSurface.Evidence.Supervision.Tests.LinuxWorkspaceSupplementNativeTests.RetainedBundleDescriptorDetectsSamePathReplacementAfterInventory");
        rows[52].SetAttributeValue("testName", "ForgeTrust.AppSurface.Evidence.Supervision.Tests.LinuxExecutionOutputNativeTests.ActualRootPipesDrainBothStreamsAfterSharedQuotaStopsExecution");
        if (fault == "missing-new-control") rows[19].SetAttributeValue("testName",
            "ForgeTrust.AppSurface.Evidence.Supervision.Tests.LinuxCustodySupplementNativeTests.UnselectedReplacement");
        if (fault == "missing-native-handshake") rows[60].SetAttributeValue("testName",
            "ForgeTrust.AppSurface.Evidence.Supervision.Tests.LinuxWorkspaceSupplementNativeTests.Extra");
        if (fault == "missing") rows.RemoveAt(40);
        if (fault == "duplicate") rows[40].SetAttributeValue("testName", rows[39].Attribute("testName")!.Value);
        if (fault == "foreign-class") rows[40].SetAttributeValue("testName", "ForgeTrust.AppSurface.Evidence.Supervision.Tests.Other.Data40");
        if (fault == "skipped") rows[40].SetAttributeValue("outcome", "NotExecuted");
        var root = new XElement(ns + "TestRun", new XElement(ns + "ResultSummary",
            new XElement(ns + "Counters", new XAttribute("total", 61), new XAttribute("executed", 61),
                new XAttribute("passed", fault == "counter" ? 60 : 61), new XAttribute("failed", 0),
                new XAttribute("error", 0), new XAttribute("notExecuted", 0), new XAttribute("warning", 0))),
            new XElement(ns + "Results", rows));
        var text = root.ToString(SaveOptions.DisableFormatting);
        if (fault == "doctype") text = "<!DOCTYPE TestRun SYSTEM 'https://example.invalid/no-fetch'>" + text;
        return Encoding.UTF8.GetBytes(text);
    }

    private static JsonDocument Binding(byte[] bytes, string? fault = null) => JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(new
    {
        trx_sha256 = fault == "digest" ? new string('0', 64) : Convert.ToHexStringLower(SHA256.HashData(bytes)),
        executed = fault == "executed" ? 60 : 61,
        passed = fault == "passed" ? 60 : 61,
        native_acceptance = fault == "acceptance",
    }));
}
