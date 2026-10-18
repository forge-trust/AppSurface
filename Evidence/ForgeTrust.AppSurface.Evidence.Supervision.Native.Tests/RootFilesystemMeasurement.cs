using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests;

/// <summary>Validates bounded current-run root filesystem test data; this establishes no native ownership.</summary>
internal static class RootFilesystemMeasurement
{
    private static readonly XNamespace Trx = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
    private static readonly string[] Classes = ["LinuxProtectedNodeNativeTests", "LinuxRetainedReportNativeTests"];

    /// <summary>Requires exact passing counters, expected case names and a hash-bound real TRX.</summary>
    internal static void Validate(JsonElement binding, byte[] bytes)
    {
        Assert.InRange(bytes.Length, 1, 1024 * 1024);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), binding.GetProperty("trx_sha256").GetString());
        Assert.False(binding.GetProperty("native_acceptance").GetBoolean());
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var document = XDocument.Load(reader);
        Assert.Equal(Trx + "TestRun", document.Root!.Name);
        var counters = Assert.Single(document.Descendants(Trx + "Counters"));
        var total = Number(counters, "total");
        Assert.InRange(total, 2, 128);
        Assert.Equal(total, Number(counters, "executed"));
        Assert.Equal(total, Number(counters, "passed"));
        foreach (var name in new[] { "failed", "error", "notExecuted", "warning" }) Assert.Equal(0, Number(counters, name));
        Assert.Equal(total, binding.GetProperty("executed").GetInt32());
        Assert.Equal(total, binding.GetProperty("passed").GetInt32());
        var results = document.Descendants(Trx + "UnitTestResult").ToArray();
        Assert.Equal(total, results.Length);
        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var result in results)
        {
            Assert.Equal("Passed", (string?)result.Attribute("outcome"));
            var testName = (string?)result.Attribute("testName");
            var owner = Assert.Single(Classes, name => testName?.StartsWith(
                "ForgeTrust.AppSurface.Evidence.Supervision.Tests." + name + ".", StringComparison.Ordinal) == true);
            observed.Add(owner);
        }
        Assert.Equal(Classes.Order(), observed.Order());
    }

    private static int Number(XElement element, string name) => int.Parse(element.Attribute(name)!.Value,
        System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture);
}
