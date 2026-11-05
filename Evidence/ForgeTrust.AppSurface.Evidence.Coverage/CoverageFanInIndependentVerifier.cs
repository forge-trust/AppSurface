using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>
/// Independently checks the semantic facts published by the coverage fan-in producer.
/// This verifier owns its identity, record, path, inventory, diff, and gate calculations.
/// </summary>
internal static class CoverageFanInIndependentVerifier
{
    private const string CompositionError = "Coverage composition failed independent verification.";
    private const string NormalizationError = "Coverage normalization failed independent verification.";
    private const string InventoryError = "Primary coverage inventory failed independent verification.";
    private const string GateError = "Coverage gates failed independent verification.";

    /// <summary>Re-derives composition and the primary-path projection from original reports.</summary>
    internal static void VerifyComposition(
        IReadOnlyList<(XDocument Document, string SourceRoot)> inputs,
        XDocument composed,
        XDocument primaryPaths)
    {
        try
        {
            Require(inputs is { Count: > 0 } && inputs.All(x => x.Document is not null && !string.IsNullOrWhiteSpace(x.SourceRoot)), CompositionError);
            var expected = new SortedDictionary<ClassId, ClassFacts>(ClassIdComparer.Instance);
            Snapshot? primary = null;
            foreach (var input in inputs)
            {
                var current = ReadSnapshot(input.Document, input.SourceRoot);
                primary ??= current;
                Merge(expected, current);
            }

            var actual = ReadSnapshot(composed, null);
            Require(SnapshotsEqual(expected, actual.Classes), CompositionError);
            CheckRootTotals(composed, actual, CompositionError);
            Require(primary is not null, CompositionError);
            var primaryActual = ReadSnapshot(primaryPaths, null);
            Require(SnapshotsEqual(primary.Classes, primaryActual.Classes), CompositionError);
            // The original normalizer's summary can include compiler points absent from its
            // serialized class lines. Preserve that exact primary aggregate rather than
            // replacing it with the composer's derived class totals.
            foreach (var counter in new[] { "lines-valid", "lines-covered", "branches-valid", "branches-covered" })
                Require(Counter(Root(primaryPaths, CompositionError), counter)
                    == Counter(Root(inputs[0].Document, CompositionError), counter), CompositionError);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (IsMalformedInput(ex)) { throw new InvalidDataException(CompositionError); }
    }

    /// <summary>Checks that native normalization retains original class and method observations.</summary>
    internal static void VerifyNormalization(XDocument raw, XDocument normalized, string sourceRoot)
    {
        try
        {
            Require(raw is not null && normalized is not null && !string.IsNullOrWhiteSpace(sourceRoot), NormalizationError);
            var before = ReadObservations(raw, sourceRoot);
            var after = ReadObservations(normalized, sourceRoot, NormalizationError);
            var rawMethods = ReadMethodObservations(raw, sourceRoot);
            var normalizedMethods = ReadMethodObservations(normalized, sourceRoot);
            Require(before.Count > 0 && before.Count == after.Count, NormalizationError);
            Require(MethodObservationsEqual(rawMethods, normalizedMethods), NormalizationError);
            foreach (var (key, point) in before)
            {
                Require(after.TryGetValue(key, out var retained) && retained.Hit == point.Hit
                    && retained.BranchValid >= point.BranchValid
                    && (point.BranchValid == 0 ? retained.BranchCovered == 0
                        : (long)retained.BranchCovered * point.BranchValid
                            <= (long)point.BranchCovered * retained.BranchValid), NormalizationError);
            }
            var rawRoot = Root(raw, NormalizationError);
            var normalizedRoot = Root(normalized, NormalizationError);
            CheckRootTotals(normalized, ReadSnapshot(normalized, sourceRoot, NormalizationError), NormalizationError);
            foreach (var dimension in new[] { "lines", "branches" })
            {
                var rawValid = Counter(rawRoot, dimension + "-valid");
                var rawCovered = Counter(rawRoot, dimension + "-covered");
                var normalizedValid = Counter(normalizedRoot, dimension + "-valid");
                var normalizedCovered = Counter(normalizedRoot, dimension + "-covered");
                Require(rawValid >= 0 && rawCovered >= 0 && rawCovered <= rawValid
                    && normalizedValid >= rawValid && normalizedCovered >= 0 && normalizedCovered <= normalizedValid
                    && (rawValid == 0 ? normalizedCovered == 0
                        : (long)normalizedCovered * rawValid <= (long)rawCovered * normalizedValid), NormalizationError);
            }
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (IsMalformedInput(ex)) { throw new InvalidDataException(NormalizationError); }
    }

    /// <summary>Requires one successful, complete timing entry for every trusted test project.</summary>
    internal static void VerifyPrimaryInventory(JsonElement timings, IReadOnlyList<string> expectedProjects,
        string configuration, string driver)
    {
        try
        {
            Require(expectedProjects is { Count: > 0 } && expectedProjects.All(x => !string.IsNullOrWhiteSpace(x)), InventoryError);
            Require(expectedProjects.Distinct(StringComparer.Ordinal).Count() == expectedProjects.Count, InventoryError);
            Require(String(timings, "configuration") == configuration && String(timings, "coverageDriver") == driver, InventoryError);
            Require(Int(Property(timings, "merge"), "exitCode") == 0, InventoryError);
            Require(Int(Property(timings, "artifacts"), "coverageFiles") == expectedProjects.Count, InventoryError);
            var entries = Property(timings, "projects");
            Require(entries.ValueKind == JsonValueKind.Array && entries.GetArrayLength() == expectedProjects.Count, InventoryError);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries.EnumerateArray())
            {
                var project = String(entry, "project");
                Require(seen.Add(project), InventoryError);
                Require(Int(entry, "exitCode") == 0 && String(entry, "executionStatus") == "completed"
                    && String(entry, "coverageArtifactStatus") == "produced", InventoryError);
            }
            Require(seen.SetEquals(expectedProjects), InventoryError);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (IsMalformedInput(ex)) { throw new InvalidDataException(InventoryError); }
    }

    /// <summary>Recomputes both gate reports, including patch metrics from the exact retained diff text.</summary>
    internal static void VerifyGates(JsonElement primaryGate, JsonElement composedGate, JsonElement trustedPolicy,
        XDocument primaryPaths, XDocument composed, string? diffText = null)
    {
        try
        {
            var policy = ReadGatePolicy(trustedPolicy);
            if (policy.Mode == "aggregateOnly")
            {
                Require(diffText is null && JsonEquivalent(primaryGate, composedGate), GateError);
                CheckGate(primaryGate, primaryPaths, policy, isComposed: false, diffText: null, patch: null);
                return;
            }

            Require(diffText is not null, GateError);
            var patch = ParsePatch(diffText!);
            CheckGate(primaryGate, primaryPaths, policy, isComposed: false, diffText, patch);
            CheckGate(composedGate, composed, policy, isComposed: true, diffText, patch);
            var primaryLine = Metric(Property(primaryGate, "patchLine"), "measurable");
            var composedLine = Metric(Property(composedGate, "patchLine"), "measurable");
            var primaryBranch = Metric(Property(primaryGate, "patchBranch"), "measurable");
            var composedBranch = Metric(Property(composedGate, "patchBranch"), "measurable");
            Require(composedLine >= primaryLine && composedBranch >= primaryBranch, GateError);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (IsMalformedInput(ex)) { throw new InvalidDataException(GateError); }
    }

    private static void CheckGate(JsonElement report, XDocument coverage, GatePolicy policy, bool isComposed,
        string? diffText, PatchFacts? patch)
    {
        Require(report.ValueKind == JsonValueKind.Object && Property(report, "passed").ValueKind == JsonValueKind.True, GateError);
        Require(String(report, "coverage") == (isComposed ? "coverage.cobertura.xml" : "primary-paths.cobertura.xml"), GateError);
        Require(Decimal(report, "tolerancePercent") == policy.Tolerance, GateError);
        var thresholds = Property(report, "thresholds");
        var expectedLine = isComposed ? 0m : policy.MinLine;
        var expectedBranch = isComposed ? 0m : policy.MinBranch;
        Require(Decimal(thresholds, "line") == expectedLine && Decimal(thresholds, "branch") == expectedBranch, GateError);
        decimal? expectedPatchLine = isComposed ? policy.MinPatchLine : null;
        decimal? expectedPatchBranch = isComposed ? policy.MinPatchBranch : null;
        Require(NullableDecimal(thresholds, "patchLine") == expectedPatchLine
            && NullableDecimal(thresholds, "patchBranch") == expectedPatchBranch, GateError);
        Require(StringOrNull(report, "patchLineMode") == (patch is null ? null : policy.PatchLineMode), GateError);

        var effective = Property(report, "effectiveThresholds");
        CheckEffective(effective, "line", expectedLine, policy.Tolerance);
        CheckEffective(effective, "branch", expectedBranch, policy.Tolerance);
        CheckEffective(effective, "patchLine", expectedPatchLine, policy.Tolerance);
        CheckEffective(effective, "patchBranch", expectedPatchBranch, policy.Tolerance);
        CheckAggregateMetric(Property(report, "line"), coverage, "lines-covered", "lines-valid", effective, "line");
        CheckAggregateMetric(Property(report, "branch"), coverage, "branches-covered", "branches-valid", effective, "branch");

        if (patch is null)
        {
            Require(Property(report, "patchDiffSource").ValueKind == JsonValueKind.Null
                && Property(report, "patchLine").ValueKind == JsonValueKind.Null
                && Property(report, "patchBranch").ValueKind == JsonValueKind.Null, GateError);
            return;
        }

        CheckDiffSource(Property(report, "patchDiffSource"), policy.DiffBase!, diffText!);
        var calculated = CalculatePatch(coverage, patch, policy.PatchLineMode);
        CheckPatchMetric(Property(report, "patchLine"), patch.LineChanged, calculated.LineCovered, calculated.LineMeasurable,
            policy.DiffBase!, effective, "patchLine");
        CheckPatchMetric(Property(report, "patchBranch"), patch.LineChanged, calculated.BranchCovered, calculated.BranchMeasurable,
            policy.DiffBase!, effective, "patchBranch");
    }

    private static PatchMetrics CalculatePatch(XDocument coverage, PatchFacts patch, string lineMode)
    {
        var lines = new Dictionary<(string File, int Number), PatchLine>();
        foreach (var cls in Root(coverage, GateError).Descendants().Where(x => x.Name.LocalName == "class"))
        {
            var file = NormalizeRelative(RequiredAttribute(cls, "filename"));
            foreach (var line in cls.Descendants().Where(x => x.Name.LocalName == "line"))
            {
                var number = Integer(RequiredAttribute(line, "number"));
                var hits = Integer(RequiredAttribute(line, "hits"));
                Require(number > 0 && hits >= 0, GateError);
                var (branchCovered, branchValid) = ReadBranches(line);
                var key = (file, number);
                var item = new PatchLine(hits > 0, branchCovered, branchValid);
                if (lines.TryGetValue(key, out var previous))
                {
                    lines[key] = new(previous.Covered || item.Covered,
                        checked(previous.BranchCovered + item.BranchCovered), checked(previous.BranchValid + item.BranchValid));
                }
                else lines.Add(key, item);
            }
        }

        var lineCovered = 0;
        var lineMeasured = 0;
        var branchCoveredTotal = 0;
        var branchMeasured = 0;
        foreach (var (file, numbers) in patch.Changed)
            foreach (var number in numbers)
            {
                if (!lines.TryGetValue((file, number), out var point)) continue;
                lineMeasured++;
                if (point.Covered && (lineMode == "measurable" || point.BranchValid == 0 || point.BranchCovered == point.BranchValid))
                    lineCovered++;
                branchCoveredTotal = checked(branchCoveredTotal + point.BranchCovered);
                branchMeasured = checked(branchMeasured + point.BranchValid);
            }
        return new(lineCovered, lineMeasured, branchCoveredTotal, branchMeasured);
    }

    private static GatePolicy ReadGatePolicy(JsonElement policy)
    {
        var gate = Property(policy, "gate");
        var mode = String(gate, "mode");
        Require(mode is "aggregateOnly" or "aggregateAndPatch", GateError);
        var line = Decimal(gate, "minLine");
        var branch = Decimal(gate, "minBranch");
        var patchLine = Decimal(gate, "minPatchLine");
        var patchBranch = Decimal(gate, "minPatchBranch");
        var tolerance = Decimal(gate, "tolerance");
        var patchMode = String(gate, "patchLineMode");
        var diffBase = StringOrNull(gate, "diffBase");
        Require(InPercentRange(line) && InPercentRange(branch) && InPercentRange(patchLine)
            && InPercentRange(patchBranch) && InPercentRange(tolerance), GateError);
        Require(patchMode is "codecov" or "measurable", GateError);
        Require(mode == "aggregateAndPatch" ? !string.IsNullOrWhiteSpace(diffBase) : diffBase is null, GateError);
        return new(mode, line, branch, patchLine, patchBranch, tolerance, patchMode, diffBase);
    }

    private static void CheckEffective(JsonElement effective, string name, decimal? configured, decimal tolerance)
    {
        var actual = NullableDecimal(effective, name);
        decimal? expected = configured is null ? null : Math.Max(0m, configured.Value - tolerance);
        Require(actual == expected, GateError);
    }

    private static void CheckAggregateMetric(JsonElement metric, XDocument coverage, string coveredName,
        string validName, JsonElement effective, string effectiveName)
    {
        var covered = Int(metric, "covered");
        var valid = Int(metric, "valid");
        var percent = Decimal(metric, "percent");
        var root = Root(coverage, GateError);
        var facts = ReadSnapshot(coverage, null);
        CheckRootTotals(coverage, facts, GateError);
        Require(covered == Counter(root, coveredName) && valid == Counter(root, validName) && valid > 0, GateError);
        var expected = RoundPercent(covered, valid);
        Require(percent == expected && percent >= Decimal(effective, effectiveName), GateError);
    }

    private static void CheckDiffSource(JsonElement source, string diffBase, string diffText)
    {
        Require(source.ValueKind == JsonValueKind.Object, GateError);
        Require(String(source, "kind") == "git-base" && String(source, "strictness") == "local-git"
            && String(source, "label") == diffBase && String(source, "diffBase") == diffBase
            && Property(source, "path").ValueKind == JsonValueKind.Null, GateError);
        var bytes = Encoding.UTF8.GetBytes(diffText);
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Require(Long(source, "bytes") == bytes.LongLength && String(source, "sha256") == digest
            && Boolean(source, "empty") == (bytes.Length == 0), GateError);
    }

    private static void CheckPatchMetric(JsonElement metric, int changed, int covered, int measurable,
        string diffBase, JsonElement effective, string effectiveName)
    {
        Require(String(metric, "diffBase") == diffBase && Int(metric, "changed") == changed
            && Int(metric, "covered") == covered && Int(metric, "measurable") == measurable
            && measurable > 0, GateError);
        var expectedPercent = RoundPercent(covered, measurable);
        var threshold = NullableDecimal(effective, effectiveName);
        Require(Decimal(metric, "percent") == expectedPercent
            && (threshold is null || expectedPercent >= threshold.Value), GateError);
    }

    private static PatchFacts ParsePatch(string diff)
    {
        Require(!string.IsNullOrWhiteSpace(diff), GateError);
        var changed = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        string? file = null;
        var newLine = 0;
        var oldRemaining = 0;
        var newRemaining = 0;
        var hunk = false;
        foreach (var raw in diff.Split('\n'))
        {
            var line = raw.EndsWith('\r') ? raw[..^1] : raw;
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                Require(!hunk || oldRemaining == 0 && newRemaining == 0, GateError);
                hunk = false;
                file = null;
                continue;
            }
            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                var name = DecodeGitPath(line[4..]);
                file = name == "/dev/null" ? null : NormalizeRelative(name.StartsWith("b/", StringComparison.Ordinal) ? name[2..] : name);
                continue;
            }
            if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                Require(!hunk || oldRemaining == 0 && newRemaining == 0, GateError);
                var match = Regex.Match(line, "^@@ -(?:[0-9]+)(?:,([0-9]+))? \\+([0-9]+)(?:,([0-9]+))? @@", RegexOptions.CultureInvariant);
                Require(match.Success, GateError);
                oldRemaining = RangeCount(match.Groups[1]);
                newRemaining = RangeCount(match.Groups[3]);
                newLine = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                hunk = true;
                continue;
            }
            if (!hunk || line == "\\ No newline at end of file") continue;
            if (line.StartsWith('+'))
            {
                Require(newRemaining > 0, GateError);
                if (file is not null)
                {
                    if (!changed.TryGetValue(file, out var points)) changed[file] = points = [];
                    Require(newLine > 0, GateError);
                    points.Add(newLine);
                }
                newLine++;
                newRemaining--;
            }
            else if (line.StartsWith('-'))
            {
                Require(oldRemaining > 0, GateError);
                oldRemaining--;
            }
            else if (line.StartsWith(' '))
            {
                Require(oldRemaining > 0 && newRemaining > 0, GateError);
                oldRemaining--;
                newRemaining--;
                newLine++;
            }
            else if (line.Length != 0)
            {
                Require(false, GateError);
            }
            if (oldRemaining == 0 && newRemaining == 0) hunk = false;
        }
        Require(!hunk && changed.Values.Sum(x => x.Count) > 0, GateError);
        return new(changed, changed.Values.Sum(x => x.Count));
    }

    private static int RangeCount(Group group) => group.Success && group.Value.Length > 0
        ? int.Parse(group.Value, CultureInfo.InvariantCulture) : 1;

    private static string DecodeGitPath(string value)
    {
        if (!value.StartsWith('"')) return value;
        Require(value.Length >= 2 && value[^1] == '"', GateError);
        using var result = new MemoryStream();
        for (var i = 1; i < value.Length - 1; i++)
        {
            var ch = value[i];
            if (ch != '\\')
            {
                var count = char.IsHighSurrogate(ch) && i + 1 < value.Length - 1 && char.IsLowSurrogate(value[i + 1]) ? 2 : 1;
                result.Write(Encoding.UTF8.GetBytes(value.Substring(i, count)));
                i += count - 1;
                continue;
            }
            Require(++i < value.Length - 1, GateError);
            ch = value[i];
            if (ch is >= '0' and <= '7')
            {
                var code = ch - '0';
                for (var n = 0; n < 2 && i + 1 < value.Length - 1 && value[i + 1] is >= '0' and <= '7'; n++)
                    code = code * 8 + value[++i] - '0';
                Require(code <= byte.MaxValue, GateError);
                result.WriteByte((byte)code);
            }
            else result.WriteByte(ch switch
            {
                'a' => 7,
                'b' => 8,
                't' => 9,
                'n' => 10,
                'v' => 11,
                'f' => 12,
                'r' => 13,
                '\\' => 92,
                '"' => 34,
                _ => throw new InvalidDataException(GateError)
            });
        }
        try { return new UTF8Encoding(false, true).GetString(result.GetBuffer(), 0, checked((int)result.Length)); }
        catch (DecoderFallbackException) { throw new InvalidDataException(GateError); }
    }

    private static Snapshot ReadSnapshot(XDocument document, string? root, string error = CompositionError)
    {
        var xmlRoot = Root(document, error);
        var classes = new SortedDictionary<ClassId, ClassFacts>(ClassIdComparer.Instance);
        var occurrences = new Dictionary<(string Package, string Name, string File), int>();
        foreach (var element in xmlRoot.Descendants().Where(x => x.Name.LocalName == "class"))
        {
            var package = element.Ancestors().FirstOrDefault(x => x.Name.LocalName == "package")?.Attribute("name")?.Value ?? string.Empty;
            var name = RequiredAttribute(element, "name");
            var filename = RequiredAttribute(element, "filename");
            var file = root is null ? NormalizeRelative(filename, error)
                : ResolveSourcePath(document, filename, root, error);
            var occurrenceBase = (package, name, file);
            var occurrence = occurrences.GetValueOrDefault(occurrenceBase);
            occurrences[occurrenceBase] = occurrence + 1;
            var id = new ClassId(package, name, file, occurrence);
            var facts = new ClassFacts();
            ReadPoints(element.Elements().FirstOrDefault(x => x.Name.LocalName == "lines"), facts.Lines);
            var methodOccurrences = new Dictionary<(string Name, string Signature), int>();
            foreach (var method in element.Elements().Where(x => x.Name.LocalName == "methods").Elements().Where(x => x.Name.LocalName == "method"))
            {
                var methodName = method.Attribute("name")?.Value ?? string.Empty;
                var signature = method.Attribute("signature")?.Value ?? string.Empty;
                var methodBase = (methodName, signature);
                var methodOccurrence = methodOccurrences.GetValueOrDefault(methodBase);
                methodOccurrences[methodBase] = methodOccurrence + 1;
                var methodId = new MethodId(methodName, signature, methodOccurrence);
                if (!facts.Methods.TryGetValue(methodId, out var methodPoints)) facts.Methods.Add(methodId, methodPoints = []);
                ReadPoints(method.Elements().FirstOrDefault(x => x.Name.LocalName == "lines"), methodPoints);
            }
            Require(classes.TryAdd(id, facts), error);
        }
        return new(classes);
    }

    private static void ReadPoints(XElement? container, SortedDictionary<LineId, Point> target)
    {
        if (container is null) return;
        var occurrences = new Dictionary<int, int>();
        foreach (var line in container.Elements().Where(x => x.Name.LocalName == "line"))
        {
            var number = Integer(RequiredAttribute(line, "number"));
            var hits = Integer(RequiredAttribute(line, "hits"));
            Require(number > 0 && hits >= 0, CompositionError);
            var occurrence = occurrences.GetValueOrDefault(number);
            occurrences[number] = occurrence + 1;
            var id = new LineId(number, occurrence);
            var branches = ReadBranches(line);
            target[id] = new(hits, branches.Covered, branches.Valid);
        }
    }

    private static (int Covered, int Valid) ReadBranches(XElement line)
    {
        var text = line.Attribute("condition-coverage")?.Value;
        if (text is null)
        {
            Require(!string.Equals(line.Attribute("branch")?.Value, "true", StringComparison.OrdinalIgnoreCase), CompositionError);
            return (0, 0);
        }
        var match = Regex.Match(text, "\\A[0-9.]+% \\(([0-9]+)/([0-9]+)\\)\\z", RegexOptions.CultureInvariant);
        Require(match.Success, CompositionError);
        var covered = Integer(match.Groups[1].Value);
        var valid = Integer(match.Groups[2].Value);
        Require(valid > 0 && covered >= 0 && covered <= valid, CompositionError);
        return (covered, valid);
    }

    private static void Merge(SortedDictionary<ClassId, ClassFacts> expected, Snapshot next)
    {
        foreach (var (id, incoming) in next.Classes)
        {
            if (!expected.TryGetValue(id, out var current)) expected.Add(id, current = new());
            MergePoints(current.Lines, incoming.Lines);
            foreach (var (methodId, points) in incoming.Methods)
            {
                if (!current.Methods.TryGetValue(methodId, out var methodPoints)) current.Methods.Add(methodId, methodPoints = []);
                MergePoints(methodPoints, points);
            }
        }
    }

    private static void MergePoints(SortedDictionary<LineId, Point> target, SortedDictionary<LineId, Point> source)
    {
        foreach (var (id, observation) in source)
        {
            if (!target.TryGetValue(id, out var previous)) { target.Add(id, observation); continue; }
            var covered = previous.BranchValid == observation.BranchValid && observation.BranchValid > 0
                && observation.BranchCovered == observation.BranchValid ? observation.BranchCovered : previous.BranchCovered;
            target[id] = new(Math.Max(previous.Hits, observation.Hits), covered, previous.BranchValid);
        }
    }

    private static bool SnapshotsEqual(IReadOnlyDictionary<ClassId, ClassFacts> expected,
        IReadOnlyDictionary<ClassId, ClassFacts> actual)
    {
        if (expected.Count != actual.Count) return false;
        foreach (var (id, facts) in expected)
        {
            if (!actual.TryGetValue(id, out var other) || !PointMapsEqual(facts.Lines, other.Lines)
                || facts.Methods.Count != other.Methods.Count) return false;
            foreach (var (methodId, points) in facts.Methods)
                if (!other.Methods.TryGetValue(methodId, out var methodPoints) || !PointMapsEqual(points, methodPoints)) return false;
        }
        return true;
    }

    private static bool PointMapsEqual(SortedDictionary<LineId, Point> left, SortedDictionary<LineId, Point> right) =>
        left.Count == right.Count && left.All(x => right.TryGetValue(x.Key, out var value) && value == x.Value);

    private static void CheckRootTotals(XDocument document, Snapshot snapshot, string error)
    {
        var root = Root(document, error);
        var lines = snapshot.Classes.Values.SelectMany(x => x.Lines.Values).ToArray();
        Require(Counter(root, "lines-valid") == lines.Length
            && Counter(root, "lines-covered") == lines.Count(x => x.Hits > 0)
            && Counter(root, "branches-valid") == lines.Sum(x => x.BranchValid)
            && Counter(root, "branches-covered") == lines.Sum(x => x.BranchCovered), error);
    }

    private static Dictionary<(string File, int Number), Observation> ReadObservations(
        XDocument document, string? root, string error = NormalizationError)
    {
        var result = new Dictionary<(string, int), Observation>();
        foreach (var element in Root(document, error).Descendants().Where(x => x.Name.LocalName == "class"))
        {
            var filename = RequiredAttribute(element, "filename");
            var file = root is null ? NormalizeRelative(filename, error)
                : ResolveSourcePath(document, filename, root, error);
            foreach (var line in element.Elements().Where(x => x.Name.LocalName == "lines").Elements().Where(x => x.Name.LocalName == "line"))
            {
                var key = (file, Integer(RequiredAttribute(line, "number")));
                var hits = Integer(RequiredAttribute(line, "hits"));
                var branch = ReadBranches(line);
                var observation = new Observation(hits > 0, branch.Covered, branch.Valid);
                if (result.TryGetValue(key, out var old))
                    result[key] = new(old.Hit || hits > 0, checked(old.BranchCovered + branch.Covered),
                        checked(old.BranchValid + branch.Valid));
                else result.Add(key, observation);
            }
        }
        return result;
    }

    private static Dictionary<MethodRecordId, SortedDictionary<LineId, Observation>> ReadMethodObservations(
        XDocument document, string sourceRoot)
    {
        var result = new Dictionary<MethodRecordId, SortedDictionary<LineId, Observation>>();
        var occurrences = new Dictionary<(string File, string Name, string Signature), int>();
        foreach (var element in Root(document, NormalizationError).Descendants().Where(x => x.Name.LocalName == "class"))
        {
            var filename = RequiredAttribute(element, "filename");
            var file = ResolveSourcePath(document, filename, sourceRoot, NormalizationError);
            foreach (var method in element.Elements().Where(x => x.Name.LocalName == "methods")
                .Elements().Where(x => x.Name.LocalName == "method"))
            {
                var name = method.Attribute("name")?.Value ?? string.Empty;
                var signature = method.Attribute("signature")?.Value ?? string.Empty;
                var methodBase = (file, name, signature);
                var occurrence = occurrences.GetValueOrDefault(methodBase);
                occurrences[methodBase] = occurrence + 1;
                var id = new MethodRecordId(file, name, signature, occurrence);
                var points = new SortedDictionary<LineId, Observation>();
                var lineOccurrences = new Dictionary<int, int>();
                foreach (var line in method.Elements().Where(x => x.Name.LocalName == "lines")
                    .Elements().Where(x => x.Name.LocalName == "line"))
                {
                    var number = Integer(RequiredAttribute(line, "number"));
                    var hits = Integer(RequiredAttribute(line, "hits"));
                    Require(number > 0 && hits >= 0, NormalizationError);
                    var lineOccurrence = lineOccurrences.GetValueOrDefault(number);
                    lineOccurrences[number] = lineOccurrence + 1;
                    var branches = ReadBranches(line);
                    Require(points.TryAdd(new(number, lineOccurrence), new(hits > 0, branches.Covered, branches.Valid)),
                        NormalizationError);
                }
                Require(result.TryAdd(id, points), NormalizationError);
            }
        }
        return result;
    }

    private static bool MethodObservationsEqual(
        IReadOnlyDictionary<MethodRecordId, SortedDictionary<LineId, Observation>> expected,
        IReadOnlyDictionary<MethodRecordId, SortedDictionary<LineId, Observation>> actual)
    {
        static Dictionary<(string, string, string, string), int> Count(
            IReadOnlyDictionary<MethodRecordId, SortedDictionary<LineId, Observation>> methods)
        {
            var counts = new Dictionary<(string, string, string, string), int>();
            foreach (var (method, points) in methods)
            {
                var encoded = new StringBuilder();
                foreach (var (line, point) in points)
                    encoded.Append(CultureInfo.InvariantCulture,
                        $"{line.Number}:{line.Occurrence}:{point.Hit}:{point.BranchCovered}:{point.BranchValid};");
                var identity = (method.File, method.Name, method.Signature, encoded.ToString());
                counts[identity] = checked(counts.GetValueOrDefault(identity) + 1);
            }
            return counts;
        }
        var original = Count(expected);
        var retained = Count(actual);
        return original.Count == retained.Count && original.All(item => retained.TryGetValue(item.Key, out var count)
            && count == item.Value);
    }

    /// <summary>Checks exact collector class ownership and numeric method facts after normalization projection.</summary>
    internal static void VerifyCollectorMethods(XDocument raw, XDocument normalized, string sourceRoot)
    {
        var original = ReadSnapshot(raw, sourceRoot).Classes;
        var retained = ReadSnapshot(normalized, sourceRoot).Classes;
        foreach (var (identity, facts) in original.Where(item => item.Value.Methods.Count > 0))
            Require(retained.TryGetValue(identity, out var other) && facts.Methods.Count == other.Methods.Count
                && facts.Methods.All(method => other.Methods.TryGetValue(method.Key, out var points)
                    && PointMapsEqual(method.Value, points)), NormalizationError);
        Require(original.Values.Sum(facts => facts.Methods.Count) == retained.Values.Sum(facts => facts.Methods.Count),
            NormalizationError);
    }

    private static string ResolveSourcePath(XDocument document, string filename, string sourceRoot, string error)
    {
        var trustedRoot = ParsePortablePath(sourceRoot, error);
        Require(trustedRoot.Kind != PortablePathKind.Relative, error);

        var parsedFilename = ParsePortablePath(filename, error);
        if (parsedFilename.Kind != PortablePathKind.Relative)
        {
            Require(TryGetRelativePath(trustedRoot, parsedFilename, out var absoluteRelative), error);
            return string.Join('/', absoluteRelative);
        }

        var sources = document.Root?.Elements().Where(x => x.Name.LocalName == "sources")
            .SelectMany(x => x.Elements().Where(y => y.Name.LocalName == "source")).ToArray() ?? [];
        if (sources.Length == 0)
            return NormalizeRelative(filename, error);

        var matches = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            var parsedSource = ParsePortablePath(source.Value, error);
            var basis = parsedSource.Kind == PortablePathKind.Relative
                ? new PortablePath(trustedRoot.Kind, trustedRoot.Root, [.. trustedRoot.Segments, .. parsedSource.Segments])
                : parsedSource;
            var candidate = new PortablePath(basis.Kind, basis.Root, [.. basis.Segments, .. parsedFilename.Segments]);
            if (TryGetRelativePath(trustedRoot, candidate, out var relative))
                matches.Add(string.Join('/', relative));
        }

        Require(matches.Count == 1, error);
        return matches.Single();
    }

    private static PortablePath ParsePortablePath(string value, string error)
    {
        Require(value.Length > 0 && !value.Contains('\0'), error);
        var path = value.Replace('\\', '/');
        var kind = path.StartsWith("//", StringComparison.Ordinal) ? PortablePathKind.Unc
            : path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '/'
                ? PortablePathKind.Drive
                : path.StartsWith("/", StringComparison.Ordinal) ? PortablePathKind.Posix : PortablePathKind.Relative;
        var prefixLength = kind switch
        {
            PortablePathKind.Posix => 1,
            PortablePathKind.Drive => 3,
            PortablePathKind.Unc => 2,
            _ => 0,
        };
        var remainder = path[prefixLength..];
        if (kind != PortablePathKind.Relative && remainder.EndsWith("/", StringComparison.Ordinal))
            remainder = remainder[..^1];
        string[] segments = remainder.Length == 0 ? [] : remainder.Split('/');
        Require(segments.All(x => x.Length > 0 && x is not ("." or "..") && !x.Contains(':')), error);
        var root = kind switch
        {
            PortablePathKind.Drive => path[..2],
            PortablePathKind.Unc => string.Join('/', segments.Take(2)),
            _ => string.Empty,
        };
        if (kind == PortablePathKind.Unc)
        {
            Require(segments.Length >= 2, error);
            segments = segments[2..];
        }
        return new(kind, root, segments);
    }

    private static bool TryGetRelativePath(PortablePath trustedRoot, PortablePath candidate, out string[] relative)
    {
        relative = [];
        if (trustedRoot.Kind != candidate.Kind || trustedRoot.Kind == PortablePathKind.Relative
            || !string.Equals(trustedRoot.Root, candidate.Root, StringComparison.Ordinal)
            || candidate.Segments.Length <= trustedRoot.Segments.Length)
            return false;
        for (var i = 0; i < trustedRoot.Segments.Length; i++)
            if (!string.Equals(trustedRoot.Segments[i], candidate.Segments[i], StringComparison.Ordinal))
                return false;
        relative = candidate.Segments[trustedRoot.Segments.Length..];
        return true;
    }

    private static string NormalizeRelative(string value, string error = CompositionError)
    {
        var path = ParsePortablePath(value, error);
        Require(path.Kind == PortablePathKind.Relative && path.Segments.Length > 0, error);
        return string.Join('/', path.Segments);
    }

    private static XElement Root(XDocument document, string error)
    {
        Require(document?.Root is { } root && root.Name.LocalName == "coverage", error);
        return document!.Root!;
    }

    private static int Counter(XElement root, string name) => Integer(RequiredAttribute(root, name));
    private static string RequiredAttribute(XElement element, string name) =>
        element.Attribute(name)?.Value ?? throw new InvalidDataException(CompositionError);
    private static int Integer(string value) => int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
    private static decimal RoundPercent(int covered, int valid) => valid == 0 ? 100m
        : Math.Round((decimal)covered * 100m / valid, 4, MidpointRounding.AwayFromZero);

    private static JsonElement Property(JsonElement element, string name)
    {
        Require(element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out _), GateError);
        return element.GetProperty(name);
    }

    private static string String(JsonElement element, string name)
    {
        var value = Property(element, name);
        Require(value.ValueKind == JsonValueKind.String, GateError);
        return value.GetString()!;
    }

    private static string? StringOrNull(JsonElement element, string name)
    {
        var value = Property(element, name);
        Require(value.ValueKind is JsonValueKind.String or JsonValueKind.Null, GateError);
        return value.ValueKind == JsonValueKind.Null ? null : value.GetString();
    }

    private static int Int(JsonElement element, string name)
    {
        var value = Property(element, name);
        Require(value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result), GateError);
        return value.GetInt32();
    }

    private static long Long(JsonElement element, string name)
    {
        var value = Property(element, name);
        Require(value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _), GateError);
        return value.GetInt64();
    }

    private static decimal Decimal(JsonElement element, string name)
    {
        var value = Property(element, name);
        Require(value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out _), GateError);
        return value.GetDecimal();
    }

    private static decimal? NullableDecimal(JsonElement element, string name)
    {
        var value = Property(element, name);
        if (value.ValueKind == JsonValueKind.Null) return null;
        Require(value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out _), GateError);
        return value.GetDecimal();
    }

    private static bool Boolean(JsonElement element, string name)
    {
        var value = Property(element, name);
        Require(value.ValueKind is JsonValueKind.True or JsonValueKind.False, GateError);
        return value.GetBoolean();
    }

    private static int Metric(JsonElement element, string name) => Int(element, name);
    private static bool InPercentRange(decimal value) => value is >= 0 and <= 100;

    private static bool JsonEquivalent(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind) return false;
        if (left.ValueKind == JsonValueKind.Object)
        {
            var a = left.EnumerateObject().ToArray();
            var b = right.EnumerateObject().ToArray();
            return a.Length == b.Length && a.All(x => right.TryGetProperty(x.Name, out var v) && JsonEquivalent(x.Value, v));
        }
        if (left.ValueKind == JsonValueKind.Array)
            return left.GetArrayLength() == right.GetArrayLength()
                && left.EnumerateArray().Zip(right.EnumerateArray()).All(x => JsonEquivalent(x.First, x.Second));
        return left.ValueKind switch
        {
            JsonValueKind.Number => left.TryGetDecimal(out var a) && right.TryGetDecimal(out var b) && a == b,
            JsonValueKind.String => left.GetString() == right.GetString(),
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            _ => false,
        };
    }

    private static void Require([DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private static bool IsMalformedInput(Exception exception) => exception is ArgumentException or FormatException
        or OverflowException or InvalidOperationException or KeyNotFoundException or NullReferenceException
        or JsonException or XmlException;

    private sealed record GatePolicy(string Mode, decimal MinLine, decimal MinBranch, decimal MinPatchLine,
        decimal MinPatchBranch, decimal Tolerance, string PatchLineMode, string? DiffBase);
    private sealed record PatchFacts(Dictionary<string, HashSet<int>> Changed, int LineChanged)
    {
        internal int BranchChanged => LineChanged;
    }
    private sealed record PatchLine(bool Covered, int BranchCovered, int BranchValid);
    private sealed record PatchMetrics(int LineCovered, int LineMeasurable, int BranchCovered, int BranchMeasurable);
    private readonly record struct Observation(bool Hit, int BranchCovered, int BranchValid);
    private readonly record struct MethodRecordId(string File, string Name, string Signature, int Occurrence);
    private enum PortablePathKind { Relative, Posix, Drive, Unc }
    private sealed record PortablePath(PortablePathKind Kind, string Root, string[] Segments);
    private sealed record Snapshot(SortedDictionary<ClassId, ClassFacts> Classes);
    private sealed class ClassFacts
    {
        internal SortedDictionary<LineId, Point> Lines { get; } = [];
        internal SortedDictionary<MethodId, SortedDictionary<LineId, Point>> Methods { get; } = new(MethodIdComparer.Instance);
    }
    private readonly record struct Point(int Hits, int BranchCovered, int BranchValid);
    private readonly record struct ClassId(string Package, string Name, string File, int Occurrence);
    private readonly record struct MethodId(string Name, string Signature, int Occurrence);
    private readonly record struct LineId(int Number, int Occurrence) : IComparable<LineId>
    {
        public int CompareTo(LineId other) => Number != other.Number ? Number.CompareTo(other.Number) : Occurrence.CompareTo(other.Occurrence);
    }
    private sealed class ClassIdComparer : IComparer<ClassId>
    {
        internal static ClassIdComparer Instance { get; } = new();
        public int Compare(ClassId x, ClassId y)
        {
            var c = StringComparer.Ordinal.Compare(x.Package, y.Package);
            if (c == 0) c = StringComparer.Ordinal.Compare(x.Name, y.Name);
            if (c == 0) c = StringComparer.Ordinal.Compare(x.File, y.File);
            return c != 0 ? c : x.Occurrence.CompareTo(y.Occurrence);
        }
    }
    private sealed class MethodIdComparer : IComparer<MethodId>
    {
        internal static MethodIdComparer Instance { get; } = new();
        public int Compare(MethodId x, MethodId y)
        {
            var c = StringComparer.Ordinal.Compare(x.Name, y.Name);
            if (c == 0) c = StringComparer.Ordinal.Compare(x.Signature, y.Signature);
            return c != 0 ? c : x.Occurrence.CompareTo(y.Occurrence);
        }
    }
}
