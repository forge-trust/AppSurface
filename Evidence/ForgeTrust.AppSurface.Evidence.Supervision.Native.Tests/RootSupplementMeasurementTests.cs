using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests;

/// <summary>Detached receipt parser controls only; synthetic TRX data grants no measured coverage or native credit.</summary>
public sealed class RootSupplementMeasurementTests
{
    [Fact]
    public void ExactSeparateSupplementDataHasBothFixedClassesAndNoNativeAcceptance()
    {
        var bytes = Data();
        using var binding = Binding(bytes);
        RootSupplementMeasurement.Validate(binding.RootElement, bytes);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("foreign-class")]
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
        var rows = Enumerable.Range(0, 28).Select(index => new XElement(ns + "UnitTestResult",
            new XAttribute("testName", "ForgeTrust.AppSurface.Evidence.Supervision.Tests."
                + (index < 19 ? "LinuxOwnerGuardNativeTests" : "LinuxCustodySupplementNativeTests") + ".Data" + index),
            new XAttribute("outcome", "Passed"))).ToList();
        if (fault == "missing") rows.RemoveAt(27);
        if (fault == "duplicate") rows[27].SetAttributeValue("testName", rows[26].Attribute("testName")!.Value);
        if (fault == "foreign-class") rows[27].SetAttributeValue("testName", "ForgeTrust.AppSurface.Evidence.Supervision.Tests.Other.Data27");
        if (fault == "skipped") rows[27].SetAttributeValue("outcome", "NotExecuted");
        var root = new XElement(ns + "TestRun", new XElement(ns + "ResultSummary",
            new XElement(ns + "Counters", new XAttribute("total", 28), new XAttribute("executed", 28),
                new XAttribute("passed", fault == "counter" ? 27 : 28), new XAttribute("failed", 0),
                new XAttribute("error", 0), new XAttribute("notExecuted", 0), new XAttribute("warning", 0))),
            new XElement(ns + "Results", rows));
        var text = root.ToString(SaveOptions.DisableFormatting);
        if (fault == "doctype") text = "<!DOCTYPE TestRun SYSTEM 'https://example.invalid/no-fetch'>" + text;
        return Encoding.UTF8.GetBytes(text);
    }

    private static JsonDocument Binding(byte[] bytes, string? fault = null) => JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(new
    {
        trx_sha256 = fault == "digest" ? new string('0', 64) : Convert.ToHexStringLower(SHA256.HashData(bytes)),
        executed = fault == "executed" ? 27 : 28,
        passed = fault == "passed" ? 27 : 28,
        native_acceptance = fault == "acceptance",
    }));
}
