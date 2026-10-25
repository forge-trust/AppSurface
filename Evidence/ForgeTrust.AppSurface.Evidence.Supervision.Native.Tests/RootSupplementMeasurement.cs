using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests;

/// <summary>Checks the separate current-run root supplement's bounded TRX, without granting native authority.</summary>
internal static class RootSupplementMeasurement
{
    private static readonly XNamespace Trx = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    /// <summary>Requires all 41 actual result rows, the three fixed test classes, and their original bound bytes.</summary>
    /// <remarks>The original 41-case root measurement and its predecessor hashes remain separate.</remarks>
    internal static void Validate(JsonElement binding, byte[] bytes)
    {
        Assert.InRange(bytes.Length, 1, 1024 * 1024);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), binding.GetProperty("trx_sha256").GetString());
        Assert.Equal(41, binding.GetProperty("executed").GetInt32());
        Assert.Equal(41, binding.GetProperty("passed").GetInt32());
        Assert.False(binding.GetProperty("native_acceptance").GetBoolean());
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var document = XDocument.Load(reader);
        Assert.Equal(Trx + "TestRun", document.Root!.Name);
        var counter = Assert.Single(document.Descendants(Trx + "Counters"));
        foreach (var name in new[] { "total", "executed", "passed" }) Assert.Equal(41, Number(counter, name));
        foreach (var name in new[] { "failed", "error", "notExecuted", "warning" }) Assert.Equal(0, Number(counter, name));
        var rows = document.Descendants(Trx + "UnitTestResult").ToArray();
        Assert.Equal(41, rows.Length);
        Assert.All(rows, row => Assert.Equal("Passed", (string?)row.Attribute("outcome")));
        var names = rows.Select(row => (string?)row.Attribute("testName")).ToArray();
        Assert.DoesNotContain(names, string.IsNullOrWhiteSpace);
        Assert.Equal(41, names.Distinct(StringComparer.Ordinal).Count());
        foreach (var (name, count) in new[] { ("LinuxOwnerGuardNativeTests", 19), ("LinuxCustodySupplementNativeTests", 9), ("LinuxProducerFileTreeNativeTests", 13) })
            Assert.Equal(count, names.Count(value => value!.StartsWith(
                "ForgeTrust.AppSurface.Evidence.Supervision.Tests." + name + ".", StringComparison.Ordinal)));
    }

    private static int Number(XElement element, string name) => int.Parse(element.Attribute(name)!.Value,
        System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture);
}
