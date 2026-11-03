using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests;

/// <summary>Detached data-only parser controls; synthetic TRX rows do not prove kernel or owner behavior.</summary>
public sealed class PeerCredentialsMeasurementTests
{
    private const string Prefix = "ForgeTrust.AppSurface.Evidence.Supervision.Tests.LinuxPeerCredentialsNativeTests.";
    private const string Mismatch = Prefix + "ActualSetprivPeerRejectsEachWrongExpectedCredential";
    private static readonly string[] Names =
    [
        Prefix + "ActualSetprivPeerMatchesItsKernelPidUidAndGid",
        Mismatch + "(field: \"gid\")",
        Mismatch + "(field: \"pid\")",
        Mismatch + "(field: \"uid\")",
    ];

    [Fact]
    public void ExactFourRowsAndDigestBoundNonAcceptanceBindingAreAccepted()
    {
        var bytes = Trx();
        using var binding = Binding(bytes);
        PeerCredentialsMeasurement.Validate(binding.RootElement, bytes);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("wrong-name")]
    [InlineData("wrong-theory-parameter")]
    [InlineData("not-passed")]
    [InlineData("total")]
    [InlineData("nonzero-skip")]
    [InlineData("extra-counter")]
    [InlineData("doctype")]
    public void ChangedRowsCountersOrXmlCannotSatisfyTheFixedMeasurement(string fault)
    {
        var bytes = Trx(fault);
        using var binding = Binding(bytes);
        Assert.ThrowsAny<Exception>(() => PeerCredentialsMeasurement.Validate(binding.RootElement, bytes));
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("executed")]
    [InlineData("passed")]
    [InlineData("native-acceptance")]
    public void BindingMustMatchTheFourPassingRowsWithoutAcceptanceClaim(string fault)
    {
        var bytes = Trx();
        using var binding = Binding(bytes, fault);
        Assert.ThrowsAny<Exception>(() => PeerCredentialsMeasurement.Validate(binding.RootElement, bytes));
    }

    [Fact]
    public void TrxMustFitTheFixedOneMiBBound()
    {
        var bytes = new byte[1024 * 1024 + 1];
        using var binding = Binding(bytes);
        Assert.ThrowsAny<Exception>(() => PeerCredentialsMeasurement.Validate(binding.RootElement, bytes));
    }

    private static byte[] Trx(string? fault = null)
    {
        if (fault == "doctype")
            return Encoding.UTF8.GetBytes("<!DOCTYPE TestRun [<!ENTITY x 'x'>]>" + Xml().ToString(SaveOptions.DisableFormatting));

        var document = Xml();
        var rows = document.Descendants(TrxNamespace + "UnitTestResult").ToList();
        var counters = document.Descendants(TrxNamespace + "Counters").Single();
        switch (fault)
        {
            case "missing": rows[0].Remove(); break;
            case "duplicate": rows[^1].SetAttributeValue("testName", Names[0]); break;
            case "wrong-name": rows[^1].SetAttributeValue("testName", Prefix + "Unselected"); break;
            case "wrong-theory-parameter": rows[1].SetAttributeValue("testName", Mismatch + "(field: \"process\")"); break;
            case "not-passed": rows[0].SetAttributeValue("outcome", "NotExecuted"); break;
            case "total": counters.SetAttributeValue("total", 5); break;
            case "nonzero-skip": counters.SetAttributeValue("notExecuted", 1); break;
            case "extra-counter": counters.SetAttributeValue("foreign", 0); break;
        }
        return Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting));
    }

    private static XNamespace TrxNamespace => "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    private static XDocument Xml() => new(new XElement(TrxNamespace + "TestRun",
        new XElement(TrxNamespace + "ResultSummary",
            new XElement(TrxNamespace + "Counters",
                new XAttribute("total", 4), new XAttribute("executed", 4), new XAttribute("passed", 4),
                new XAttribute("failed", 0), new XAttribute("error", 0), new XAttribute("notExecuted", 0),
                new XAttribute("warning", 0), new XAttribute("aborted", 0), new XAttribute("inconclusive", 0),
                new XAttribute("timeout", 0), new XAttribute("notRunnable", 0), new XAttribute("disconnected", 0),
                new XAttribute("completed", 0), new XAttribute("inProgress", 0), new XAttribute("pending", 0),
                new XAttribute("passedButRunAborted", 0))),
        new XElement(TrxNamespace + "Results", Names.Select(name => new XElement(TrxNamespace + "UnitTestResult",
            new XAttribute("testName", name), new XAttribute("outcome", "Passed"))))));

    private static JsonDocument Binding(byte[] bytes, string? fault = null) => JsonDocument.Parse(JsonSerializer.Serialize(new
    {
        trx_sha256 = fault == "digest" ? new string('0', 64) : Convert.ToHexStringLower(SHA256.HashData(bytes)),
        executed = fault == "executed" ? 3 : 4,
        passed = fault == "passed" ? 3 : 4,
        native_acceptance = fault == "native-acceptance",
    }));
}
