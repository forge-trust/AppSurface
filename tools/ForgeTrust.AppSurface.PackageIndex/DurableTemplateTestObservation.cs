using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace ForgeTrust.AppSurface.PackageIndex;

/// <summary>Diagnostic-only test counters and method hashes; never retains result text or authorizes success.</summary>
/// <remarks>Status is observed, missing or invalid. Counts are zero when no complete bounded TRX observation exists.
/// FailedMethodSha256 contains at most 16 distinct hashes of UTF-8 className, a newline and method name,
/// sorted ordinally. It excludes display names, theory arguments, messages, stacks and child output.
/// Raw TRX files stay in the owned proof workspace and are deleted during its existing cleanup.</remarks>
/// <param name="Status">Closed diagnostic status: observed, missing or invalid.</param>
/// <param name="Total">Reported test count, at most 10,000; zero without an observation.</param>
/// <param name="Executed">Reported executed count, no greater than Total.</param>
/// <param name="Passed">Reported passed count; Passed plus Failed cannot exceed Executed.</param>
/// <param name="Failed">Reported failed count; does not determine the command's success.</param>
/// <param name="FailedMethodSha256">At most 16 distinct, sorted method-identity digests; incomplete metadata may leave this empty.</param>
internal sealed record DurableTemplateTestObservation(
    string Status, int Total, int Executed, int Passed, int Failed, IReadOnlyList<string> FailedMethodSha256)
{
    /// <summary>Gets at most 16 distinct closed failure markers from identity-matched failed first-work tests.</summary>
    /// <remarks>Null means no valid marker was available. These diagnostics never authorize a successful proof.</remarks>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<DurableTemplateFirstWorkDiagnostic>? FirstWorkFailures { get; init; }

    private const long MaximumBytes = 1024 * 1024;
    private const int MaximumTests = 10_000;
    private const int MaximumMethodCharacters = 1024;
    private static readonly XNamespace Namespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    /// <summary>Reads at most one MiB of regular TRX XML; missing or invalid diagnostics cannot override a child exit.</summary>
    /// <param name="path">TRX path inside the proof's canonical owned results directory.</param>
    /// <returns>Safe counters and hashes, or a closed missing/invalid observation without raw exception details.</returns>
    internal static DurableTemplateTestObservation Read(string path)
    {
        if (!File.Exists(path)) return Empty("missing");
        try
        {
            DurableTemplateStaging.RequireRegularPath(path);
            if (new FileInfo(path).Length > MaximumBytes) return Empty("invalid");
            using var reader = XmlReader.Create(path, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumBytes,
            });
            var document = XDocument.Load(reader);
            if (document.Root?.Name != Namespace + "TestRun") return Empty("invalid");
            var counters = document.Root.Element(Namespace + "ResultSummary")?.Element(Namespace + "Counters");
            var total = Count(counters, "total");
            var executed = Count(counters, "executed");
            var passed = Count(counters, "passed");
            var failed = Count(counters, "failed");
            if (executed > total || passed + failed > executed) return Empty("invalid");
            var failedResults = (document.Root.Element(Namespace + "Results")?.Elements(Namespace + "UnitTestResult") ?? [])
                .Where(result => (string?)result.Attribute("outcome") == "Failed")
                .Where(result => result.Attribute("testId") is not null)
                .ToLookup(result => (string)result.Attribute("testId")!, StringComparer.Ordinal);
            var hashes = new SortedSet<string>(StringComparer.Ordinal);
            var firstWorkFailures = new List<DurableTemplateFirstWorkDiagnostic>();
            var observedFirstWorkIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var test in document.Root.Element(Namespace + "TestDefinitions")?.Elements(Namespace + "UnitTest") ?? [])
            {
                var testId = (string?)test.Attribute("id");
                if (testId is null || !failedResults.Contains(testId)) continue;
                var method = test.Element(Namespace + "TestMethod");
                var className = (string?)method?.Attribute("className");
                var name = (string?)method?.Attribute("name");
                if (string.IsNullOrEmpty(className) || string.IsNullOrEmpty(name)
                    || className.Length > MaximumMethodCharacters || name.Length > MaximumMethodCharacters)
                    return Empty("invalid");
                hashes.Add(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(className + "\n" + name))));
                if (name == "FirstDurableWork_persists_typed_terminal_result_and_exports_the_real_activation"
                    && className.Split(',')[0] == "DurableWorkerTemplate.Tests.FirstDurableWorkTests"
                    && observedFirstWorkIds.Add(testId))
                {
                    foreach (var result in failedResults[testId])
                    {
                        if (DurableTemplateFirstWorkDiagnostic.Read(result.Element(Namespace + "Output")?.Element(Namespace + "StdOut")?.Value) is { } diagnostic)
                            firstWorkFailures.Add(diagnostic);
                    }
                }
            }
            return new("observed", total, executed, passed, failed, hashes.Take(16).ToArray())
            {
                FirstWorkFailures = firstWorkFailures.Count == 0 ? null : firstWorkFailures.Distinct()
                    .OrderBy(x => x.Phase, StringComparer.Ordinal).ThenBy(x => x.Code, StringComparer.Ordinal)
                    .ThenBy(x => x.Cleanup, StringComparer.Ordinal).Take(16).ToArray(),
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException or PackageIndexException)
        {
            return Empty("invalid");
        }
    }

    private static int Count(XElement? counters, string name)
    {
        if (!int.TryParse((string?)counters?.Attribute(name), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            || value is < 0 or > MaximumTests)
            throw new XmlException("Invalid bounded test counters.");
        return value;
    }

    private static DurableTemplateTestObservation Empty(string status) => new(status, 0, 0, 0, 0, []);
}
