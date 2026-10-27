using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests;

/// <summary>Validates the separate current-run application-bundle audit and report-discovery TRX.</summary>
internal static class ApplicationBundleMeasurement
{
    private const int MaximumBytes = 1024 * 1024;
    private static readonly XNamespace Trx = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
    private const string AuditTestPrefix = "ForgeTrust.AppSurface.Evidence.Supervision.Tests.LinuxApplicationBundleAuditNativeTests.";
    private const string DiscoveryTestPrefix = "ForgeTrust.AppSurface.Evidence.Supervision.Tests.LinuxProducerReportDiscoveryNativeTests.";
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
    ];
    private static readonly string[] BindingFields = ["executed", "native_acceptance", "passed", "trx_sha256"];

    /// <summary>Requires the exact 17 current audit and discovery rows and their bounded TRX bytes.</summary>
    /// <remarks>This is measurement validation only; <c>native_acceptance</c> must remain false.</remarks>
    internal static void Validate(JsonElement binding, byte[] bytes)
    {
        Assert.InRange(bytes.Length, 1, MaximumBytes);
        Assert.Equal(BindingFields, binding.EnumerateObject().Select(static property => property.Name)
            .Order(StringComparer.Ordinal));
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var claimedDigest = binding.GetProperty("trx_sha256").GetString();
        Assert.Matches("^[0-9a-f]{64}$", claimedDigest!);
        Assert.Equal(digest, claimedDigest);
        Assert.Equal(17, binding.GetProperty("executed").GetInt32());
        Assert.Equal(17, binding.GetProperty("passed").GetInt32());
        Assert.False(binding.GetProperty("native_acceptance").GetBoolean());

        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumBytes,
        });
        var document = XDocument.Load(reader);
        Assert.Equal(Trx + "TestRun", document.Root!.Name);
        var counters = Assert.Single(document.Descendants(Trx + "Counters"));
        var zeroCounters = new[]
        {
            "failed", "error", "notExecuted", "warning", "aborted", "inconclusive", "timeout",
            "notRunnable", "disconnected", "completed", "inProgress", "pending", "passedButRunAborted",
        };
        Assert.Equal(zeroCounters.Concat(["total", "executed", "passed"]).Order(StringComparer.Ordinal),
            counters.Attributes().Select(static attribute => attribute.Name.ToString()).Order(StringComparer.Ordinal));
        foreach (var name in new[] { "total", "executed", "passed" })
            Assert.Equal(17, Number(counters, name));
        foreach (var name in zeroCounters)
            Assert.Equal(0, Number(counters, name));

        var rows = document.Descendants(Trx + "UnitTestResult").ToArray();
        Assert.Equal(17, rows.Length);
        Assert.All(rows, static row => Assert.Equal("Passed", (string?)row.Attribute("outcome")));
        var names = rows.Select(static row => (string?)row.Attribute("testName")).ToArray();
        Assert.DoesNotContain(names, string.IsNullOrWhiteSpace);
        Assert.Equal(17, names.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(TestNames.Order(StringComparer.Ordinal),
            names.Select(static name => name!).Order(StringComparer.Ordinal));
    }

    private static int Number(XElement element, string name) => int.Parse(element.Attribute(name)!.Value,
        System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture);
}
