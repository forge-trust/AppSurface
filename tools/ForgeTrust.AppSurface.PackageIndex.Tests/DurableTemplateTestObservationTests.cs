using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace ForgeTrust.AppSurface.PackageIndex.Tests;

public sealed class DurableTemplateTestObservationTests : IDisposable
{
    private static readonly XNamespace Namespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
    private const string Secret = "private-assertion-argument-credential";
    private const string FirstWorkClass = "DurableWorkerTemplate.Tests.FirstDurableWorkTests";
    private const string FirstWorkMethod = "FirstDurableWork_persists_typed_terminal_result_and_exports_the_real_activation";
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

    [Theory]
    [InlineData(FirstWorkClass)]
    [InlineData(FirstWorkClass + ", DurableWorkerTemplate.Tests, Version=1.0.0.0, Culture=neutral")]
    public void Failed_first_work_identity_attaches_only_its_stdout_diagnostic(string className)
    {
        var path = Write(Report(3, 3, 1, 2,
            [ResultWithOutput("target", "Failed", FirstWorkMarker()),
                ResultWithOutput("unrelated", "Failed", FirstWorkMarker("sdk-export-flush")),
                ResultWithOutput("passed", "Passed", FirstWorkMarker("typed-work-acceptance"))],
            [Definition("target", className, FirstWorkMethod), Definition("unrelated", "Unrelated.Tests", FirstWorkMethod),
                Definition("passed", FirstWorkClass, FirstWorkMethod)]));

        var result = DurableTemplateTestObservation.Read(path);

        Assert.Equal("observed", result.Status);
        Assert.Equal(3, result.Total);
        Assert.Equal(3, result.Executed);
        Assert.Equal(1, result.Passed);
        Assert.Equal(2, result.Failed);
        Assert.Equal(new DurableTemplateFirstWorkDiagnostic("initial-readiness", "TimeoutException", "none"),
            Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<DurableTemplateFirstWorkDiagnostic>>(result.FirstWorkFailures)));
    }

    [Fact]
    public void First_work_failure_serialization_redacts_all_output_fields_without_claiming_success()
    {
        var output = Secret + " before\n" + FirstWorkMarker(code: Secret, cleanup: Secret) + "\n" + Secret + " after";
        var path = Write(Report(1, 1, 0, 1, [ResultWithOutput("target", "Failed", output)],
            [Definition("target", FirstWorkClass + ", " + Secret, FirstWorkMethod)]));

        var result = DurableTemplateTestObservation.Read(path);
        var json = JsonSerializer.Serialize(result);
        using var document = JsonDocument.Parse(json);
        var diagnostic = Assert.Single(document.RootElement.GetProperty("FirstWorkFailures").EnumerateArray());

        Assert.Equal("observed", result.Status);
        Assert.Equal(1, result.Total);
        Assert.Equal(1, result.Executed);
        Assert.Equal(0, result.Passed);
        Assert.Equal(1, result.Failed);
        Assert.Single(result.FailedMethodSha256);
        Assert.Equal(7, document.RootElement.EnumerateObject().Count());
        Assert.Equal(3, diagnostic.EnumerateObject().Count());
        Assert.Equal("initial-readiness", diagnostic.GetProperty("Phase").GetString());
        Assert.Equal("Other", diagnostic.GetProperty("Code").GetString());
        Assert.Equal("unknown", diagnostic.GetProperty("Cleanup").GetString());
        Assert.DoesNotContain(Secret, json, StringComparison.Ordinal);
        Assert.DoesNotContain(FirstWorkClass, json, StringComparison.Ordinal);
        Assert.DoesNotContain(FirstWorkMethod, json, StringComparison.Ordinal);
    }

    [Fact]
    public void Failed_first_work_setup_retains_only_the_closed_operation_through_the_trx_boundary()
    {
        const string location = "<AcquirePinnedImageAsync>d__50.MoveNext->ExceptionDispatchInfo.Throw";
        var marker = FirstWorkMarker("postgresql-fixture-setup", "InvalidOperationException", location: location);
        var path = Write(Report(2, 2, 1, 1,
            [ResultWithOutput("failed", "Failed", marker), ResultWithOutput("passed", "Passed", marker)],
            [Definition("failed", FirstWorkClass, FirstWorkMethod), Definition("passed", FirstWorkClass, FirstWorkMethod)]));

        var result = DurableTemplateTestObservation.Read(path);
        var diagnostic = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<DurableTemplateFirstWorkDiagnostic>>(result.FirstWorkFailures));
        Assert.Equal("pinned-image-acquisition", diagnostic.SetupOperation);
        Assert.Equal(1, result.Failed);
        Assert.Equal(1, result.Passed);
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(location, json, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Unrelated.Tests", FirstWorkMethod)]
    [InlineData(FirstWorkClass + "Extra", FirstWorkMethod)]
    [InlineData(FirstWorkClass + "+Nested", FirstWorkMethod)]
    [InlineData("Prefix." + FirstWorkClass, FirstWorkMethod)]
    [InlineData(FirstWorkClass, FirstWorkMethod + "Extra")]
    [InlineData(FirstWorkClass, "Unrelated_method")]
    public void Unrelated_failed_methods_cannot_attach_a_first_work_diagnostic(string className, string methodName)
    {
        var path = Write(Report(1, 1, 0, 1, [ResultWithOutput("target", "Failed", FirstWorkMarker())],
            [Definition("target", className, methodName)]));

        AssertNoFirstWorkFailure(DurableTemplateTestObservation.Read(path));
    }

    [Fact]
    public void Passing_first_work_and_unmatched_failed_ids_cannot_attach_diagnostics()
    {
        var path = Write(Report(2, 2, 1, 1,
            [ResultWithOutput("passed", "Passed", FirstWorkMarker()), ResultWithOutput("unmatched", "Failed", FirstWorkMarker())],
            [Definition("passed", FirstWorkClass, FirstWorkMethod), Definition("different-id", FirstWorkClass, FirstWorkMethod)]));

        var result = DurableTemplateTestObservation.Read(path);

        AssertNoFirstWorkFailure(result);
        Assert.Equal(1, result.Passed);
        Assert.Equal(1, result.Failed);
        Assert.Empty(result.FailedMethodSha256);
    }

    [Theory]
    [InlineData("StdErr")]
    [InlineData("Message")]
    [InlineData("StackTrace")]
    public void First_work_markers_in_other_output_fields_are_not_retained(string field)
    {
        var testResult = Result("target", "Failed");
        var output = testResult.Element(Namespace + "Output")!;
        if (field == "StdErr") output.Add(new XElement(Namespace + field, FirstWorkMarker()));
        else output.Element(Namespace + "ErrorInfo")!.Element(Namespace + field)!.Value = FirstWorkMarker();
        var path = Write(Report(1, 1, 0, 1, [testResult], [Definition("target", FirstWorkClass, FirstWorkMethod)]));

        AssertNoFirstWorkFailure(DurableTemplateTestObservation.Read(path));
    }

    [Fact]
    public void Absent_or_invalid_stdout_withholds_only_the_first_work_detail()
    {
        foreach (var output in new string?[] { null, string.Empty, FirstWorkMarker("unreviewed-phase"), FirstWorkMarker() + "\n" + FirstWorkMarker() })
        {
            var testResult = output is null ? Result("target", "Failed") : ResultWithOutput("target", "Failed", output);
            var result = DurableTemplateTestObservation.Read(Write(Report(1, 1, 0, 1, [testResult],
                [Definition("target", FirstWorkClass, FirstWorkMethod)])));

            AssertNoFirstWorkFailure(result);
            Assert.Equal(1, result.Failed);
            Assert.Single(result.FailedMethodSha256);
        }
    }

    [Fact]
    public void Missing_output_preserves_the_failed_identity_without_attaching_first_work_details()
    {
        var testResult = ResultWithOutput("target", "Failed", FirstWorkMarker());
        testResult.Element(Namespace + "Output")!.Remove();
        var path = Write(Report(1, 1, 0, 1, [testResult], [Definition("target", FirstWorkClass, FirstWorkMethod)]));

        var result = DurableTemplateTestObservation.Read(path);

        AssertNoFirstWorkFailure(result);
        Assert.Equal(1, result.Total);
        Assert.Equal(1, result.Executed);
        Assert.Equal(0, result.Passed);
        Assert.Equal(1, result.Failed);
        Assert.Single(result.FailedMethodSha256);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public void First_work_failure_inventory_is_distinct_and_ordered_by_phase_code_then_cleanup()
    {
        DurableTemplateFirstWorkDiagnostic[] expected =
        [
            new("authenticated-work-activation", "TrueException", "none"),
            new("initial-readiness", "EqualException", "owned-cleanup-unfinished"),
            new("initial-readiness", "IOException", "none"),
            new("initial-readiness", "IOException", "owned-cleanup-total-budget-exhausted"),
            new("initial-readiness", "IOException", "owned-cleanup-unfinished"),
            new("typed-work-acceptance", "TimeoutException", "none"),
        ];
        var inputs = expected.Reverse().Concat([expected[2]]).ToArray();

        var result = ObserveFirstWorkFailures(inputs);

        Assert.Equal(expected, result.FirstWorkFailures);
    }

    [Fact]
    public void First_work_failure_inventory_applies_the_sixteen_item_bound_after_distinct_ordering()
    {
        string[] phases = ["authenticated-work-activation", "authorization-before-wake-body", "healthy-readiness-transition",
            "host-read-only-startup", "initial-readiness", "persisted-terminal-work", "postgresql-fixture-setup",
            "read-only-startup-catalog", "sdk-export-flush", "typed-work-acceptance"];
        var distinct = phases.SelectMany(phase => new[]
        {
            new DurableTemplateFirstWorkDiagnostic(phase, "EqualException", "none"),
            new DurableTemplateFirstWorkDiagnostic(phase, "TimeoutException", "none"),
        }).ToArray();
        // Put repeated values before unsorted distinct values to detect truncation before deduplication or sorting.
        var result = ObserveFirstWorkFailures(Enumerable.Repeat(distinct[0], 8).Concat(distinct.Reverse()).ToArray());
        var actual = Assert.IsAssignableFrom<IReadOnlyList<DurableTemplateFirstWorkDiagnostic>>(result.FirstWorkFailures);

        Assert.Equal(16, actual.Count);
        Assert.Equal(16, actual.Distinct().Count());
        Assert.Equal(new DurableTemplateFirstWorkDiagnostic("authenticated-work-activation", "EqualException", "none"), actual[0]);
        Assert.Equal(new DurableTemplateFirstWorkDiagnostic("read-only-startup-catalog", "TimeoutException", "none"), actual[^1]);
        Assert.DoesNotContain(actual, item => item.Phase is "sdk-export-flush" or "typed-work-acceptance");
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

    [Fact]
    public void Missing_results_element_preserves_counters_without_inventing_failed_identity()
    {
        var report = Report(1, 1, 0, 1, [ResultWithOutput("target", "Failed", FirstWorkMarker())],
            [Definition("target", FirstWorkClass, FirstWorkMethod)]);
        report.Root!.Element(Namespace + "Results")!.Remove();

        var result = DurableTemplateTestObservation.Read(Write(report));

        AssertNoFirstWorkFailure(result);
        Assert.Equal(1, result.Total);
        Assert.Equal(1, result.Executed);
        Assert.Equal(0, result.Passed);
        Assert.Equal(1, result.Failed);
        Assert.Empty(result.FailedMethodSha256);
    }

    [Fact]
    public void Failed_result_without_test_id_cannot_borrow_a_first_work_definition()
    {
        var testResult = ResultWithOutput("target", "Failed", FirstWorkMarker());
        testResult.SetAttributeValue("testId", null);
        var path = Write(Report(1, 1, 0, 1, [testResult], [Definition("target", FirstWorkClass, FirstWorkMethod)]));

        var result = DurableTemplateTestObservation.Read(path);

        AssertNoFirstWorkFailure(result);
        Assert.Equal(1, result.Total);
        Assert.Equal(1, result.Executed);
        Assert.Equal(0, result.Passed);
        Assert.Equal(1, result.Failed);
        Assert.Empty(result.FailedMethodSha256);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(result), StringComparison.Ordinal);
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
        Assert.Null(result.FirstWorkFailures);
    }

    private static void AssertNoFirstWorkFailure(DurableTemplateTestObservation result)
    {
        Assert.Equal("observed", result.Status);
        Assert.Null(result.FirstWorkFailures);
        Assert.DoesNotContain("FirstWorkFailures", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    private DurableTemplateTestObservation ObserveFirstWorkFailures(IReadOnlyList<DurableTemplateFirstWorkDiagnostic> diagnostics)
    {
        var ids = Enumerable.Range(0, diagnostics.Count).Select(index => index.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var results = ids.Select((id, index) => ResultWithOutput(id, "Failed",
            FirstWorkMarker(diagnostics[index].Phase, diagnostics[index].Code, diagnostics[index].Cleanup)));
        var definitions = ids.Select(id => Definition(id, FirstWorkClass, FirstWorkMethod));
        return DurableTemplateTestObservation.Read(Write(Report(ids.Length, ids.Length, 0, ids.Length, results, definitions)));
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

    private static XElement ResultWithOutput(string id, string outcome, string stdout)
    {
        var result = Result(id, outcome);
        result.Element(Namespace + "Output")!.Add(new XElement(Namespace + "StdOut", stdout), new XElement(Namespace + "StdErr", Secret));
        return result;
    }

    private static string FirstWorkMarker(string phase = "initial-readiness", string code = "TimeoutException", string cleanup = "none", string location = Secret) =>
        $"[FirstDurableWork] failure phase={phase} code={code} at={location} cause={Secret}; cleanup={cleanup}; retry={Secret}";

    private static XElement Definition(string id, string className, string name) => new(Namespace + "UnitTest",
        new XAttribute("id", id), new XAttribute("name", Secret), new XElement(Namespace + "TestMethod",
            new XAttribute("className", className), new XAttribute("name", name)));

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
