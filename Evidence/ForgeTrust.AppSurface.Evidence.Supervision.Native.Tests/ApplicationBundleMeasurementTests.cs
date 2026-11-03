using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests;

/// <summary>Detached data-only controls for the separate application-bundle TRX binding.</summary>
/// <remarks>Synthetic XML tests validate the parser contract; they do not claim root execution or native acceptance.</remarks>
public sealed class ApplicationBundleMeasurementTests
{
    private const string AuditTestPrefix = "ForgeTrust.AppSurface.Evidence.Supervision.Tests.LinuxApplicationBundleAuditNativeTests.";
    private const string DiscoveryTestPrefix = "ForgeTrust.AppSurface.Evidence.Supervision.Tests.LinuxProducerReportDiscoveryNativeTests.";
    private const string ResultsRootPrefix = "ForgeTrust.AppSurface.Evidence.Supervision.Tests.LinuxSelectedProducerResultsRootNativeTests.";
    private static readonly string[] TestNames =
    [
        AuditTestPrefix + "ActualRootTreeInventoryAcceptsSelectedBytesAndRetainsOnlyBorrowedNodes",
        AuditTestPrefix + "ActualExtraFileIsRejectedAfterItsDescriptorJoinsTheCallersQuarantineSet",
        AuditTestPrefix + "ChangedDeclaredBytesAreRejectedAndTheActualTreeRemainsUnmodifiedByAudit",
        AuditTestPrefix + "PreCancelledAuditStopsBeforeOpeningChildrenAndLeavesRootAndSourceBytesIntact",
        AuditTestPrefix + "InvalidNameAndSpecialFilesystemChildrenAreRejectedWithoutChangingTheirSentinel",
        AuditTestPrefix + "ActualThirtySecondDirectoryLevelIsAcceptedAndThirtyThirdIsRejectedAfterRetention",
        DiscoveryTestPrefix + "ActualResultsDescriptorDiscoversOnlySortedCanonicalReportPaths",
        DiscoveryTestPrefix + "NonReportEntriesAndEmptyNestedDirectoriesReject(kind: \"non-report\")",
        DiscoveryTestPrefix + "NonReportEntriesAndEmptyNestedDirectoriesReject(kind: \"empty-nested-directory\")",
        DiscoveryTestPrefix + "SpecialOrUnsafeActualEntriesRejectWithoutChangingReportBytes(kind: \"symlink\")",
        DiscoveryTestPrefix + "SpecialOrUnsafeActualEntriesRejectWithoutChangingReportBytes(kind: \"hardlink\")",
        DiscoveryTestPrefix + "SpecialOrUnsafeActualEntriesRejectWithoutChangingReportBytes(kind: \"fifo\")",
        DiscoveryTestPrefix + "SpecialOrUnsafeActualEntriesRejectWithoutChangingReportBytes(kind: \"wrong-mode\")",
        DiscoveryTestPrefix + "SpecialOrUnsafeActualEntriesRejectWithoutChangingReportBytes(kind: \"wrong-owner\")",
        DiscoveryTestPrefix + "SixtyFourthReportIsAcceptedAndTheNextActualReportIsRejected",
        DiscoveryTestPrefix + "ThirtySecondPathComponentIsAcceptedAndThirtyThirdIsRejected",
        DiscoveryTestPrefix + "PreCancelledDiscoveryStopsBeforeNativeEnumeration",
        ResultsRootPrefix + "ActualSelectedDirectoryRechecksAndRepeatedDisposeClosesTheRetainedDescriptor",
        ResultsRootPrefix + "ASelectedRootWithAnotherKernelInodeRejectsBeforeAdoptingTheActualDirectory",
        ResultsRootPrefix + "SelectedDirectoryRequiresExactPrivateModeEvenWhenTheSampleMatchesTheKernel",
        ResultsRootPrefix + "SelectedDirectoryRequiresTheOriginalSubjectOwner",
        ResultsRootPrefix + "ResultsParentRequiresTheExactRootOwnedSearchableMode",
        ResultsRootPrefix + "WritableAncestorRejectsWithoutOpeningAReplacementOrChangingItsBytes",
        ResultsRootPrefix + "RenamedSelectedDirectoryCannotBeReplacedBehindItsRetainedDescriptor",
        ResultsRootPrefix + "RenamedResultsParentCannotBeReplacedBehindTheRetainedChain",
        ResultsRootPrefix + "ChangedSelectedOwnerAfterOpenRejectsTheNextKernelRecheck",
        ResultsRootPrefix + "SelectedSymlinkCannotAdoptAnOtherwiseValidOutsideDirectory",
        ResultsRootPrefix + "PreCancelledOpenAndRecheckKeepTheOriginalTokenAndNeverChangeTheTree",
        ResultsRootPrefix + "ExpiredOriginalDeadlineRejectsOpenAndRecheckWithoutRenewingTheAllowance",
    ];

    [Fact]
    public void ExactTwentyNineAuditDiscoveryAndSelectedRootRowsAreAcceptedAsNonAcceptanceMeasurement()
    {
        var bytes = Data();
        using var binding = Binding(bytes);
        ApplicationBundleMeasurement.Validate(binding.RootElement, bytes);
    }

    [Theory]
    [InlineData("missing-binding-field")]
    [InlineData("extra-binding-field")]
    [InlineData("digest")]
    [InlineData("executed")]
    [InlineData("passed")]
    [InlineData("native-acceptance")]
    public void BindingMustDescribeOnlyTheExactTwentyNineRowNonAcceptanceTrx(string fault)
    {
        var bytes = Data();
        using var binding = Binding(bytes, fault);
        Assert.ThrowsAny<Exception>(() => ApplicationBundleMeasurement.Validate(binding.RootElement, bytes));
    }

    [Theory]
    [InlineData("missing-row")]
    [InlineData("six-only")]
    [InlineData("missing-discovery-case")]
    [InlineData("mismatched-parameter")]
    [InlineData("duplicate-row")]
    [InlineData("foreign-method")]
    [InlineData("not-passed")]
    [InlineData("counter-mismatch")]
    [InlineData("nonzero-skip-counter")]
    [InlineData("doctype")]
    [InlineData("unknown-counter")]
    public void MissingChangedSkippedOrExternalRowsCannotSatisfyTheFixedMethodSet(string fault)
    {
        var bytes = Data(fault);
        using var binding = Binding(bytes);
        Assert.ThrowsAny<Exception>(() => ApplicationBundleMeasurement.Validate(binding.RootElement, bytes));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("oversize")]
    public void TrxMustRemainInsideTheFixedNonemptyByteBound(string fault)
    {
        var bytes = fault == "empty" ? Array.Empty<byte>() : new byte[1024 * 1024 + 1];
        using var binding = Binding(bytes);
        Assert.ThrowsAny<Exception>(() => ApplicationBundleMeasurement.Validate(binding.RootElement, bytes));
    }

    private static byte[] Data(string? fault = null)
    {
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var rows = TestNames.Select(name => new XElement(ns + "UnitTestResult",
            new XAttribute("testName", name),
            new XAttribute("outcome", "Passed"))).ToList();
        if (fault == "six-only") rows.RemoveRange(6, rows.Count - 6);
        if (fault is "missing-row" or "missing-discovery-case") rows.RemoveAt(rows.Count - 1);
        if (fault == "duplicate-row") rows[^1].SetAttributeValue("testName", rows[6].Attribute("testName")!.Value);
        if (fault == "foreign-method") rows[^1].SetAttributeValue("testName", DiscoveryTestPrefix + "Unselected");
        if (fault == "mismatched-parameter") rows[9].SetAttributeValue("testName",
            DiscoveryTestPrefix + "SpecialOrUnsafeActualEntriesRejectWithoutChangingReportBytes(kind: \"not-symlink\")");
        if (fault == "not-passed") rows[^1].SetAttributeValue("outcome", "NotExecuted");
        var counters = new XElement(ns + "Counters",
            new XAttribute("total", 29), new XAttribute("executed", 29),
            new XAttribute("passed", 29),
            new XAttribute("failed", fault == "counter-mismatch" ? 1 : 0),
            new XAttribute("error", 0), new XAttribute("notExecuted", fault == "nonzero-skip-counter" ? 1 : 0),
            new XAttribute("warning", 0), new XAttribute("aborted", 0), new XAttribute("inconclusive", 0),
            new XAttribute("timeout", 0), new XAttribute("notRunnable", 0), new XAttribute("disconnected", 0),
            new XAttribute("completed", 0), new XAttribute("inProgress", 0), new XAttribute("pending", 0),
            new XAttribute("passedButRunAborted", 0));
        if (fault == "unknown-counter") counters.SetAttributeValue("unrecognizedFailure", 1);
        var xml = new XDocument(new XElement(ns + "TestRun",
            new XElement(ns + "ResultSummary", counters), new XElement(ns + "Results", rows)))
            .ToString(SaveOptions.DisableFormatting);
        if (fault == "doctype") xml = "<!DOCTYPE TestRun SYSTEM 'https://example.invalid/no-fetch'>" + xml;
        return Encoding.UTF8.GetBytes(xml);
    }

    private static JsonDocument Binding(byte[] bytes, string? fault = null)
    {
        var binding = new JsonObject
        {
            ["trx_sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            ["executed"] = 29,
            ["passed"] = 29,
            ["native_acceptance"] = false,
        };
        if (fault == "missing-binding-field") binding.Remove("passed");
        if (fault == "extra-binding-field") binding["unreviewed"] = 0;
        if (fault == "digest") binding["trx_sha256"] = new string('0', 64);
        if (fault == "executed") binding["executed"] = 5;
        if (fault == "passed") binding["passed"] = 5;
        if (fault == "native-acceptance") binding["native_acceptance"] = true;
        return JsonDocument.Parse(binding.ToJsonString());
    }
}
