using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace ForgeTrust.AppSurface.PackageIndex.Tests;

public sealed class DurableTemplateTestObservationTests : IDisposable
{
    private static readonly XNamespace Namespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
    private const string Secret = "private-assertion-argument-credential";
    private readonly string _root = Path.Join(Path.GetTempPath(), "template-test-observation-" + Guid.NewGuid().ToString("N"));

    public DurableTemplateTestObservationTests()
    {
        if (OperatingSystem.IsMacOS() && (_root.StartsWith("/var/", StringComparison.Ordinal)
            || _root.StartsWith("/tmp/", StringComparison.Ordinal))) _root = "/private" + _root;
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void Missing_report_is_a_diagnostic_gap()
    {
        var result = DurableTemplateTestObservation.Read(Path.Join(_root, "absent.trx"));
        Assert.Equal("missing", result.Status);
        Assert.Equal(0, result.Total);
        Assert.Empty(result.FailedMethodSha256);
    }

    [Fact]
    public void Observed_failure_retains_counters_and_only_method_identity_hashes()
    {
        var path = Write(Report(3, 3, 1, 2,
            [Result("first", "Failed"), Result("same", "Failed"), Result("passed", "Passed")],
            [Definition("first", "Known.Tests", "Fails"), Definition("same", "Known.Tests", "Fails"),
                Definition("passed", "Ignored.Tests", "Passes")]));

        var result = DurableTemplateTestObservation.Read(path);

        Assert.Equal("observed", result.Status);
        Assert.Equal(3, result.Total);
        Assert.Equal(3, result.Executed);
        Assert.Equal(1, result.Passed);
        Assert.Equal(2, result.Failed);
        Assert.Equal("0bf784bd5ab562aaeb78b5a8c621e86d55c04d7d4c198cac795c4132fad29fdc", Assert.Single(result.FailedMethodSha256));
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(Secret, json, StringComparison.Ordinal);
        Assert.DoesNotContain("Known.Tests", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Fails", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Method_hash_inventory_is_distinct_sorted_and_bounded()
    {
        var ids = Enumerable.Range(0, 20).Select(index => index.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var path = Write(Report(20, 20, 0, 20,
            ids.Select(id => Result(id, "Failed")), ids.Select(id => Definition(id, "Known.Tests", "Fails" + id))));
        var expected = ids.Select(id => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("Known.Tests\nFails" + id))))
            .Order(StringComparer.Ordinal).Take(16).ToArray();
        Assert.Equal(expected, DurableTemplateTestObservation.Read(path).FailedMethodSha256);
    }

    [Fact]
    public void Missing_definitions_or_results_do_not_invent_method_identity()
    {
        foreach (var report in new[]
        {
            Report(1, 1, 0, 1, [Result("unknown", "Failed")], []),
            Report(1, 1, 1, 0, [], [Definition("unmatched", "Known.Tests", "Passes")]),
        })
        {
            var result = DurableTemplateTestObservation.Read(Write(report));
            Assert.Equal("observed", result.Status);
            Assert.Empty(result.FailedMethodSha256);
        }
    }

    [Theory]
    [InlineData("total", "-1")]
    [InlineData("total", "10001")]
    [InlineData("total", "secret")]
    [InlineData("total", "")]
    [InlineData("executed", "2")]
    [InlineData("passed", "2")]
    [InlineData("failed", "1")]
    public void Invalid_or_contradictory_counters_are_withheld(string counter, string value)
    {
        var report = Report(1, 1, 1, 0, [], []);
        report.Root!.Element(Namespace + "ResultSummary")!.Element(Namespace + "Counters")!.SetAttributeValue(counter, value);
        AssertInvalid(Write(report));
    }

    [Theory]
    [InlineData("className", "")]
    [InlineData("name", "")]
    [InlineData("className", null)]
    [InlineData("name", null)]
    [InlineData("className", "oversized")]
    [InlineData("name", "oversized")]
    public void Invalid_failed_method_metadata_is_withheld(string attribute, string? value)
    {
        var definition = Definition("failed", "Known.Tests", "Fails");
        definition.Element(Namespace + "TestMethod")!.SetAttributeValue(attribute,
            value == "oversized" ? new string('x', 1025) : value);
        AssertInvalid(Write(Report(1, 1, 0, 1, [Result("failed", "Failed")], [definition])));
    }

    [Theory]
    [InlineData("<TestRun />")]
    [InlineData("<broken")]
    [InlineData("<!DOCTYPE TestRun [<!ENTITY private SYSTEM 'file:///private/test-secret'>]><TestRun>&private;</TestRun>")]
    public void Malformed_xml_foreign_schema_and_external_entities_are_withheld(string content)
    {
        var path = Path.Join(_root, "invalid.trx");
        File.WriteAllText(path, content);
        AssertInvalid(path);
    }

    [Fact]
    public void Oversized_or_linked_reports_are_withheld()
    {
        var path = Path.Join(_root, "large.trx");
        File.WriteAllText(path, new string('x', (1024 * 1024) + 1));
        AssertInvalid(path);
        if (OperatingSystem.IsWindows()) return; // Unprivileged Windows does not promise symlink creation.
        var linked = Path.Join(_root, "linked.trx");
        File.CreateSymbolicLink(linked, path);
        AssertInvalid(linked);
    }

    private static void AssertInvalid(string path)
    {
        var result = DurableTemplateTestObservation.Read(path);
        Assert.Equal("invalid", result.Status);
        Assert.Equal(0, result.Total);
        Assert.Equal(0, result.Executed);
        Assert.Equal(0, result.Passed);
        Assert.Equal(0, result.Failed);
        Assert.Empty(result.FailedMethodSha256);
    }

    private string Write(XDocument report)
    {
        var path = Path.Join(_root, Guid.NewGuid().ToString("N") + ".trx");
        report.Save(path);
        return path;
    }

    private static XDocument Report(int total, int executed, int passed, int failed,
        IEnumerable<XElement> results, IEnumerable<XElement> definitions) => new(new XElement(Namespace + "TestRun",
        new XElement(Namespace + "Results", results), new XElement(Namespace + "TestDefinitions", definitions),
        new XElement(Namespace + "ResultSummary", new XElement(Namespace + "Counters",
            new XAttribute("total", total), new XAttribute("executed", executed),
            new XAttribute("passed", passed), new XAttribute("failed", failed)))));

    private static XElement Result(string id, string outcome) => new(Namespace + "UnitTestResult",
        new XAttribute("testId", id), new XAttribute("outcome", outcome), new XAttribute("testName", Secret),
        new XElement(Namespace + "Output", new XElement(Namespace + "ErrorInfo",
            new XElement(Namespace + "Message", Secret), new XElement(Namespace + "StackTrace", Secret))));

    private static XElement Definition(string id, string className, string name) => new(Namespace + "UnitTest",
        new XAttribute("id", id), new XAttribute("name", Secret), new XElement(Namespace + "TestMethod",
            new XAttribute("className", className), new XAttribute("name", name)));

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
