using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests;

/// <summary>Validates a separate four-case peer-credential TRX; it grants no owner or admission authority.</summary>
internal static class PeerCredentialsMeasurement
{
    private const string Prefix = "ForgeTrust.AppSurface.Evidence.Supervision.Tests.LinuxPeerCredentialsNativeTests.";
    private const string Mismatch = Prefix + "ActualSetprivPeerRejectsEachWrongExpectedCredential";
    private static readonly XNamespace Trx = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
    private static readonly string[] Names =
    [
        Prefix + "ActualSetprivPeerMatchesItsKernelPidUidAndGid",
        Mismatch + "(field: \"pid\")",
        Mismatch + "(field: \"uid\")",
        Mismatch + "(field: \"gid\")",
    ];
    private static readonly string[] ZeroCounters =
    [
        "failed", "error", "notExecuted", "warning", "aborted", "inconclusive", "timeout",
        "notRunnable", "disconnected", "completed", "inProgress", "pending", "passedButRunAborted",
    ];

    /// <summary>Requires a hash-bound exact passing TRX from the separate peer-only test invocation.</summary>
    internal static void Validate(JsonElement binding, byte[] bytes)
    {
        Assert.InRange(bytes.Length, 1, 1024 * 1024);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), binding.GetProperty("trx_sha256").GetString());
        Assert.Equal(4, binding.GetProperty("executed").GetInt32());
        Assert.Equal(4, binding.GetProperty("passed").GetInt32());
        Assert.False(binding.GetProperty("native_acceptance").GetBoolean());

        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var document = XDocument.Load(reader);
        Assert.Equal(Trx + "TestRun", document.Root!.Name);
        var counters = Assert.Single(document.Descendants(Trx + "Counters"));
        Assert.Equal(ZeroCounters.Append("total").Append("executed").Append("passed").Order(StringComparer.Ordinal),
            counters.Attributes().Select(static attribute => attribute.Name.LocalName).Order(StringComparer.Ordinal));
        Assert.Equal(4, Number(counters, "total"));
        Assert.Equal(4, Number(counters, "executed"));
        Assert.Equal(4, Number(counters, "passed"));
        foreach (var name in ZeroCounters) Assert.Equal(0, Number(counters, name));

        var results = document.Descendants(Trx + "UnitTestResult").ToArray();
        Assert.Equal(4, results.Length);
        Assert.All(results, static result => Assert.Equal("Passed", (string?)result.Attribute("outcome")));
        Assert.Equal(Names.Order(StringComparer.Ordinal), results.Select(static result =>
            (string?)result.Attribute("testName")).Order(StringComparer.Ordinal));
    }

    private static int Number(XElement element, string name) => int.Parse(element.Attribute(name)!.Value,
        System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture);
}
