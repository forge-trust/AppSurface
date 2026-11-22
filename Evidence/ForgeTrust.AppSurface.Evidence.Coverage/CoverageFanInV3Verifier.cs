using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Independent v3 semantic entry point. The legacy verifier owns the replay algorithms, separately from writers.</summary>
internal static class CoverageFanInV3Verifier
{
    internal static void Verify(JsonElement primaryGate, JsonElement composedGate, JsonElement applicability,
        JsonElement metadata, CoverageFanInExpected expected, XDocument primary, XDocument composed,
        CoverageFanInDiffInput input, IReadOnlyList<string> captureHashes, string primaryReportHash,
        string composedReportHash, IReadOnlySet<string> trackedPaths) =>
        CoverageFanInIndependentVerifier.VerifyV3(primaryGate, composedGate, applicability, metadata, expected,
            primary, composed, input, captureHashes, primaryReportHash, composedReportHash, trackedPaths);
}

internal static partial class CoverageFanInIndependentVerifier
{
    private const string StrictDiffPolicyId = "appsurface-git-patch-v1";
    private const string VisualBaselinePngDiffPolicyId = "appsurface-visual-baseline-png-v1";

    /// <summary>Independently replays raw objects, hunks, counters, gates and all stored claim copies.</summary>
    internal static void VerifyV3(JsonElement primaryGate, JsonElement composedGate, JsonElement claim,
        JsonElement metadata, CoverageFanInExpected expected, XDocument primary, XDocument composed,
        CoverageFanInDiffInput input, IReadOnlyList<string> captureHashes, string primaryReportHash,
        string composedReportHash, IReadOnlySet<string> trackedPaths)
    {
        try
        {
            Require(expected.ContractVersion == 3, GateError);
            var diffPolicyId = expected.DiffPolicyId;
            Require((diffPolicyId is StrictDiffPolicyId or VisualBaselinePngDiffPolicyId)
                && input.DiffPolicyId == diffPolicyId, GateError);
            Require(diffPolicyId != VisualBaselinePngDiffPolicyId || !input.Patch.AsSpan().Contains((byte)0), GateError);
            var qualifiedPngPaths = input.QualifiedPngPaths.ToHashSet(StringComparer.Ordinal);
            Require(qualifiedPngPaths.Count == input.QualifiedPngPaths.Count, GateError);
            var raw = V3Raw(input);
            V3QualifiedPngChanges(qualifiedPngPaths, raw, diffPolicyId);
            var facts = V3Patch(input, raw, qualifiedPngPaths);
            var expectedMetadata = CoverageFanInV3Encoding.Element(new
            {
                schemaVersion = 3,
                requestedBaseCommit = input.RequestedBase,
                mergeBaseCommit = input.MergeBase,
                headCommit = input.Head,
                diffPolicyId,
                diffSha256 = CoverageFanInIO.Hash(input.Patch),
                empty = input.Patch.Length == 0,
                changes = raw.OrderBy(x => x.NewPath ?? x.OldPath, StringComparer.Ordinal).ThenBy(x => x.OldPath, StringComparer.Ordinal)
                    .ThenBy(x => x.Kind, StringComparer.Ordinal).Select(x => new
                    {
                        kind = x.Kind,
                        oldPath = x.OldPath,
                        newPath = x.NewPath,
                        addedRanges = V3Ranges(x.Additions).Select(range => new { start = range.Start, count = range.Count })
                    })
            });
            Require(JsonElement.DeepEquals(expectedMetadata, metadata), GateError);
            V3Exact(claim, "schemaVersion", "reportProfileId", "policyDigest", "source", "run", "diffSha256",
                "diffMetadataSha256", "captureManifestHashes", "primaryReportSha256", "composedReportSha256", "primary", "composed");
            Require(Int(claim, "schemaVersion") == 3 && String(claim, "reportProfileId") == String(expected.Policy, "reportProfileId")
                && String(claim, "policyDigest") == expected.Digest && JsonElement.DeepEquals(Property(claim, "source"), expected.Source)
                && JsonElement.DeepEquals(Property(claim, "run"), expected.Run) && String(claim, "diffSha256") == CoverageFanInIO.Hash(input.Patch)
                && String(claim, "diffMetadataSha256") == CoverageFanInIO.Hash(CoverageFanInV3Encoding.Canonical(expectedMetadata))
                && String(claim, "primaryReportSha256") == primaryReportHash && String(claim, "composedReportSha256") == composedReportHash, GateError);
            var hashes = Property(claim, "captureManifestHashes");
            Require(hashes.ValueKind == JsonValueKind.Array && hashes.EnumerateArray().Select(x => x.GetString()).SequenceEqual(captureHashes), GateError);
            var aliases = new HashSet<string>(trackedPaths.Select(path => path.Normalize(NormalizationForm.FormC)), StringComparer.OrdinalIgnoreCase);
            var qualifiedAliases = new HashSet<string>(qualifiedPngPaths.Select(path =>
                path.Normalize(NormalizationForm.FormC)), StringComparer.OrdinalIgnoreCase);
            foreach (var document in new[] { primary, composed })
                foreach (var cls in document.Descendants().Where(x => x.Name.LocalName == "class"))
                {
                    var path = NormalizeRelative(RequiredAttribute(cls, "filename"));
                    V3Path(path);
                    Require(trackedPaths.Contains(path) || !aliases.Contains(path.Normalize(NormalizationForm.FormC)), GateError);
                    Require(!qualifiedAliases.Contains(path.Normalize(NormalizationForm.FormC)), GateError);
                }
            var policy = ReadGatePolicy(expected.Policy);
            var primaryMetrics = CalculatePatch(primary, facts, policy.PatchLineMode);
            var composedMetrics = CalculatePatch(composed, facts, policy.PatchLineMode);
            Require(composedMetrics.LineMeasurable >= primaryMetrics.LineMeasurable && composedMetrics.BranchMeasurable >= primaryMetrics.BranchMeasurable, GateError);
            V3CheckGate(primaryGate, primary, policy, false, input, facts.LineChanged, primaryMetrics, Property(claim, "primary"));
            V3CheckGate(composedGate, composed, policy, true, input, facts.LineChanged, composedMetrics, Property(claim, "composed"));
        }
        catch (CoverageFanInEvidenceException) { throw; }
        catch (Exception exception) when (exception is InvalidDataException || IsMalformedInput(exception)
            || exception is DecoderFallbackException or ArgumentException)
        { throw new CoverageFanInEvidenceException("applicability-mismatch", "verification", "Independently recomputed v3 claims disagree; preserve evidence and report the operation ID before regenerating."); }
    }

    private static void V3CheckGate(JsonElement report, XDocument document, GatePolicy policy, bool composed,
        CoverageFanInDiffInput input, int changed, PatchMetrics metrics, JsonElement claim)
    {
        V3Exact(report, "passed", "coverage", "tolerancePercent", "thresholds", "effectiveThresholds", "patchLineMode", "patchDiffSource", "line", "branch", "patchLine", "patchBranch");
        V3Exact(claim, "line", "branch");
        Require(String(report, "coverage") == (composed ? "coverage.cobertura.xml" : "primary-paths.cobertura.xml")
            && Decimal(report, "tolerancePercent") == policy.Tolerance && String(report, "patchLineMode") == policy.PatchLineMode, GateError);
        var thresholds = Property(report, "thresholds"); var effective = Property(report, "effectiveThresholds");
        V3Exact(thresholds, "line", "branch", "patchLine", "patchBranch"); V3Exact(effective, "line", "branch", "patchLine", "patchBranch");
        var lineThreshold = composed ? 0 : policy.MinLine; var branchThreshold = composed ? 0 : policy.MinBranch;
        decimal? patchLine = composed ? policy.MinPatchLine : null; decimal? patchBranch = composed ? policy.MinPatchBranch : null;
        Require(Decimal(thresholds, "line") == lineThreshold && Decimal(thresholds, "branch") == branchThreshold
            && NullableDecimal(thresholds, "patchLine") == patchLine && NullableDecimal(thresholds, "patchBranch") == patchBranch, GateError);
        CheckEffective(effective, "line", lineThreshold, policy.Tolerance); CheckEffective(effective, "branch", branchThreshold, policy.Tolerance);
        CheckEffective(effective, "patchLine", patchLine, policy.Tolerance); CheckEffective(effective, "patchBranch", patchBranch, policy.Tolerance);
        // Both records must be validated even when one threshold fails.
        var linePassed = V3Aggregate(Property(report, "line"), document, "lines", lineThreshold, policy.Tolerance);
        var branchPassed = V3Aggregate(Property(report, "branch"), document, "branches", branchThreshold, policy.Tolerance);
        var passed = linePassed && branchPassed;
        V3Exact(Property(report, "patchDiffSource"), "kind", "label", "strictness", "diffBase", "path", "bytes", "sha256", "empty");
        CheckDiffSource(Property(report, "patchDiffSource"), input.RequestedBase, new UTF8Encoding(false, true).GetString(input.Patch));
        passed &= V3Metric(Property(report, "patchLine"), changed, metrics.LineCovered, metrics.LineMeasurable, patchLine, policy.Tolerance);
        passed &= V3Metric(Property(report, "patchBranch"), changed, metrics.BranchCovered, metrics.BranchMeasurable, patchBranch, policy.Tolerance);
        Require(JsonElement.DeepEquals(Property(report, "patchLine"), Property(claim, "line"))
            && JsonElement.DeepEquals(Property(report, "patchBranch"), Property(claim, "branch"))
            && Boolean(report, "passed") == passed, GateError);
    }

    private static bool V3Aggregate(JsonElement metric, XDocument document, string dimension, decimal threshold, decimal tolerance)
    {
        V3Exact(metric, "covered", "valid", "percent");
        var root = Root(document, GateError); var valid = Counter(root, dimension + "-valid"); var covered = Counter(root, dimension + "-covered");
        Require(valid > 0 && covered >= 0 && covered <= valid && Int(metric, "valid") == valid && Int(metric, "covered") == covered
            && Decimal(metric, "percent") == RoundPercent(covered, valid), GateError);
        return RoundPercent(covered, valid) >= Math.Max(0, threshold - tolerance);
    }

    private static bool V3Metric(JsonElement metric, int changed, int covered, int valid, decimal? threshold, decimal tolerance)
    {
        V3Exact(metric, "outcome", "reason", "changedLines", "measurable", "covered", "percent");
        Require(changed >= 0 && valid >= 0 && covered >= 0 && covered <= valid && Int(metric, "changedLines") == changed
            && Int(metric, "measurable") == valid && Int(metric, "covered") == covered, GateError);
        if (valid == 0)
        {
            Require(String(metric, "outcome") == "notApplicable" && String(metric, "reason") == "no-reported-patch-points"
                && Property(metric, "percent").ValueKind == JsonValueKind.Null, GateError);
            return true;
        }
        Require(String(metric, "outcome") == "measured" && String(metric, "reason") == "reported-points"
            && Decimal(metric, "percent") == RoundPercent(covered, valid), GateError);
        return threshold is null || RoundPercent(covered, valid) >= Math.Max(0, threshold.Value - tolerance);
    }

    private static void V3Exact(JsonElement value, params string[] fields)
    {
        Require(value.ValueKind == JsonValueKind.Object, GateError);
        var properties = value.EnumerateObject().Select(x => x.Name).ToArray();
        Require(properties.Length == fields.Length && properties.Distinct(StringComparer.Ordinal).Count() == fields.Length
            && properties.ToHashSet(StringComparer.Ordinal).SetEquals(fields), GateError);
    }

    private sealed record V3Change(string Kind, string? OldPath, string? NewPath, string OldId, string NewId, string OldMode, string NewMode)
    { internal SortedSet<int> Additions { get; } = []; }

    private static void V3QualifiedPngChanges(IReadOnlySet<string> qualifiedPngPaths, IReadOnlyList<V3Change> changes, string diffPolicyId)
    {
        if (diffPolicyId == StrictDiffPolicyId)
        {
            Require(qualifiedPngPaths.Count == 0, GateError);
            return;
        }

        var expected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in changes)
        {
            var oldBaseline = change.OldPath is { } oldPath && CoverageFanInPngValidator.IsQualifiedPath(oldPath);
            var newBaseline = change.NewPath is { } newPath && CoverageFanInPngValidator.IsQualifiedPath(newPath);
            if (!oldBaseline && !newBaseline) continue;
            Require(change.Kind == "modified" && change.OldMode == "100644" && change.NewMode == "100644", GateError);
            expected.Add(change.NewPath!);
        }
        Require(expected.SetEquals(qualifiedPngPaths), GateError);
    }

    private static List<V3Change> V3Raw(CoverageFanInDiffInput input)
    {
        var result = new List<V3Change>();
        if (input.Raw.Length == 0) return result;
        var pieces = input.Raw.Split('\0'); Require(pieces[^1] == string.Empty, GateError);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < pieces.Length - 1;)
        {
            var entry = pieces[index++].Split(' ');
            Require(entry.Length == 5 && entry[0].Length == 7 && entry[0][0] == ':' && index < pieces.Length - 1, GateError);
            var oldMode = entry[0][1..]; var mode = entry[1]; var before = entry[2]; var after = entry[3]; var status = entry[4];
            Require(V3Id(before, input.ObjectIdLength) && V3Id(after, input.ObjectIdLength), GateError);
            var path = V3Path(pieces[index++]); string? oldPath = path, newPath = path;
            var kind = status switch { "A" => "added", "D" => "deleted", "M" => "modified", _ => "renamed" };
            if (kind == "renamed")
            {
                Require(status.Length > 1 && status[0] == 'R' && int.TryParse(status.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var score)
                    && score is >= 50 and <= 100 && index < pieces.Length - 1, GateError);
                newPath = V3Path(pieces[index++]); Require(oldPath != newPath, GateError);
            }
            if (kind == "added") oldPath = null;
            if (kind == "deleted") newPath = null;
            Require(oldPath is null ? oldMode == "000000" && before.All(c => c == '0') : oldMode is "100644" or "100755" && before.Any(c => c != '0'), GateError);
            Require(newPath is null ? mode == "000000" && after.All(c => c == '0') : mode is "100644" or "100755" && after.Any(c => c != '0'), GateError);
            if (kind == "modified" && before == after) { Require(oldMode != mode, GateError); kind = "modeChanged"; }
            Require(seen.Add(newPath ?? oldPath!), GateError);
            result.Add(new(kind, oldPath, newPath, before, after, oldMode, mode));
        }
        return result;
    }

    private static PatchFacts V3Patch(CoverageFanInDiffInput input, IReadOnlyList<V3Change> changes,
        IReadOnlySet<string> qualifiedPngPaths)
    {
        var text = new UTF8Encoding(false, true).GetString(input.Patch);
        if (text.Length == 0) { Require(changes.Count == 0, GateError); return new(new(StringComparer.Ordinal), 0); }
        Require(text.EndsWith('\n'), GateError);
        var lines = text.Split('\n'); var fileIndex = -1; V3Change? current = null;
        var oldLeft = 0; var newLeft = 0; var cursor = 0; var oldEnd = -1; var newEnd = -1; var added = 0;
        var hasIndex = false; var oldHeader = false; var newHeader = false; var renameFrom = false; var renameTo = false; var hasHunk = false;
        void Finish()
        {
            if (current is null) return;
            Require(oldLeft == 0 && newLeft == 0 && oldHeader == newHeader && (current.OldId == current.NewId || hasIndex)
                && (current.Kind != "renamed" || renameFrom && renameTo) && (current.Kind != "modeChanged" || !hasIndex && !hasHunk), GateError);
        }
        for (var i = 0; i < lines.Length - 1; i++)
        {
            var line = lines[i];
            if (line == "\\ No newline at end of file") { Require(hasHunk, GateError); continue; }
            if (oldLeft != 0 || newLeft != 0)
            {
                Require(current is not null && line.Length > 0, GateError);
                if (line[0] == '+' && newLeft > 0)
                {
                    Require(current!.NewPath is not null && cursor > 0 && added < input.MaximumRecords && current.Additions.Add(cursor), GateError);
                    added = checked(added + 1); cursor = checked(cursor + 1); newLeft--;
                }
                else if (line[0] == '-' && oldLeft > 0) oldLeft--;
                else if (line[0] == ' ' && oldLeft > 0 && newLeft > 0) { oldLeft--; newLeft--; cursor = checked(cursor + 1); }
                else Require(false, GateError);
                continue;
            }
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                Finish(); Require(++fileIndex < changes.Count, GateError); current = changes[fileIndex];
                Require(V3HeaderPair(line[11..], "a/" + (current.OldPath ?? current.NewPath), "b/" + (current.NewPath ?? current.OldPath)), GateError);
                oldEnd = newEnd = -1; hasIndex = oldHeader = newHeader = renameFrom = renameTo = hasHunk = false;
                if (qualifiedPngPaths.Contains(current.NewPath ?? string.Empty))
                {
                    Require(i + 2 < lines.Length - 1, GateError);
                    Require(lines[++i] == $"index {current.OldId}..{current.NewId} 100644", GateError);
                    Require(lines[++i] == $"Binary files a/{current.NewPath} and b/{current.NewPath} differ", GateError);
                    current = null;
                }
                continue;
            }
            Require(current is not null, GateError);
            if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                Require(hasIndex && oldHeader && newHeader, GateError);
                var close = line.IndexOf(" @@", 3, StringComparison.Ordinal); Require(close > 0, GateError);
                var spans = line[3..close].Split(' '); Require(spans.Length == 2 && spans[0].StartsWith('-') && spans[1].StartsWith('+'), GateError);
                var (oldStart, oldCount) = V3Span(spans[0][1..]); var (newStart, newCount) = V3Span(spans[1][1..]);
                Require(oldCount != 0 || newCount != 0, GateError); Require(oldStart >= oldEnd && newStart >= newEnd, GateError);
                oldEnd = checked(oldStart + oldCount); newEnd = checked(newStart + newCount);
                oldLeft = oldCount; newLeft = newCount; cursor = newStart; hasHunk = true; continue;
            }
            Require(!hasHunk, GateError);
            if (line.StartsWith("index ", StringComparison.Ordinal))
            {
                Require(!hasIndex, GateError); var parts = line[6..].Split(' '); Require(parts.Length is 1 or 2, GateError);
                Require(parts[0] == current!.OldId + ".." + current.NewId
                    && (parts.Length == 1 || current.OldMode == current.NewMode && parts[1] == current.NewMode), GateError); hasIndex = true;
            }
            else if (line.StartsWith("--- ", StringComparison.Ordinal)) { Require(!oldHeader && DecodeGitPathHeader(line[4..]) == (current!.OldPath is null ? "/dev/null" : "a/" + current.OldPath), GateError); oldHeader = true; }
            else if (line.StartsWith("+++ ", StringComparison.Ordinal)) { Require(oldHeader && !newHeader && DecodeGitPathHeader(line[4..]) == (current!.NewPath is null ? "/dev/null" : "b/" + current.NewPath), GateError); newHeader = true; }
            else if (line.StartsWith("rename from ", StringComparison.Ordinal)) { Require(current!.Kind == "renamed" && !renameFrom && DecodeGitPathHeader(line[12..]) == current.OldPath, GateError); renameFrom = true; }
            else if (line.StartsWith("rename to ", StringComparison.Ordinal)) { Require(current!.Kind == "renamed" && !renameTo && DecodeGitPathHeader(line[10..]) == current.NewPath, GateError); renameTo = true; }
            else if (line.StartsWith("similarity index ", StringComparison.Ordinal)) { Require(current!.Kind == "renamed" && line.EndsWith('%') && int.TryParse(line.AsSpan(17, line.Length - 18), out var similarity) && similarity is >= 50 and <= 100, GateError); }
            else if (line.StartsWith("new file mode ", StringComparison.Ordinal)) Require(current!.Kind == "added" && line[14..] == current.NewMode, GateError);
            else if (line.StartsWith("deleted file mode ", StringComparison.Ordinal)) Require(current!.Kind == "deleted" && line[18..] == current.OldMode, GateError);
            else if (line.StartsWith("old mode ", StringComparison.Ordinal)) Require(line[9..] == current!.OldMode && current.OldMode != current.NewMode, GateError);
            else if (line.StartsWith("new mode ", StringComparison.Ordinal)) Require(line[9..] == current!.NewMode && current.OldMode != current.NewMode, GateError);
            else Require(false, GateError);
        }
        Finish(); Require(fileIndex + 1 == changes.Count, GateError);
        var changed = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        foreach (var change in changes.Where(x => x.Additions.Count != 0)) { Require(change.NewPath is not null, GateError); changed.Add(change.NewPath!, change.Additions.ToHashSet()); }
        return new(changed, added);
    }

    private static (int Start, int Count) V3Span(string text)
    {
        var pair = text.Split(','); Require(pair.Length is 1 or 2, GateError);
        var start = Integer(pair[0]); var count = pair.Length == 1 ? 1 : Integer(pair[1]);
        Require(count == 0 || start > 0, GateError); return (start, count);
    }

    private static IEnumerable<(int Start, int Count)> V3Ranges(SortedSet<int> additions)
    {
        var start = 0; var count = 0;
        foreach (var line in additions)
        {
            if (count == 0) { start = line; count = 1; }
            else if ((long)start + count == line) count = checked(count + 1);
            else { yield return (start, count); start = line; count = 1; }
        }
        if (count != 0) yield return (start, count);
    }

    private static bool V3HeaderPair(string field, string oldPath, string newPath)
    {
        // Match against both raw names without splitting literal filename spaces. Quoted names are
        // decoded by the verifier's existing independent C-escape/UTF8 primitive.
        if (!field.StartsWith('"'))
            return field == oldPath + " " + newPath || field.StartsWith(oldPath + " ", StringComparison.Ordinal)
                && DecodeGitPathHeader(field[(oldPath.Length + 1)..]) == newPath;
        for (var i = 1; i < field.Length; i++)
        {
            if (field[i] == '\\') { i++; continue; }
            if (field[i] != '"') continue;
            return i + 2 < field.Length && field[i + 1] == ' ' && DecodeGitPathHeader(field[..(i + 1)]) == oldPath
                && DecodeGitPathHeader(field[(i + 2)..]) == newPath;
        }
        return false;
    }

    private static bool V3Id(string text, int size) => size is 40 or 64 && text.Length == size && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string V3Path(string text)
    {
        Require(text.Length is > 0 and <= 4096 && !text.StartsWith('/') && !text.Contains('\\') && !text.Any(char.IsControl)
            && !text.Any(c => char.GetUnicodeCategory(c) == UnicodeCategory.Format), GateError);
        foreach (var segment in text.Split('/'))
        {
            Require(segment is not ("" or "." or "..") && !segment.EndsWith('.') && !segment.EndsWith(' ')
                && segment.IndexOfAny(['<', '>', ':', '|', '?', '*']) < 0, GateError);
            var stem = segment.Split('.')[0].ToUpperInvariant();
            Require(stem is not ("CON" or "PRN" or "AUX" or "NUL") && !(stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9'), GateError);
        }
        _ = new UTF8Encoding(false, true).GetByteCount(text); return text;
    }
}
