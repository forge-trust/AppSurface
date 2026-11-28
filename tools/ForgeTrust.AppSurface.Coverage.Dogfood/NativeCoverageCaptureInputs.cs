using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Supplies the existing indexed native capture with timings and staged original report paths.</summary>
/// <param name="TimingsPath">The original successful collection timings.</param>
/// <param name="RawCoveragePaths">Original collector report paths keyed by repository-relative project identity.</param>
internal sealed record NativeCoverageCaptureInputs(string TimingsPath, IReadOnlyDictionary<string, string> RawCoveragePaths)
{
    /// <summary>Resolves the exact project inventory from the completed coverage run.</summary>
    /// <param name="collectionRoot">Output directory from the installed coverage run command.</param>
    /// <param name="expectedProjects">The project inventory in the native capture policy, matched ordinally.</param>
    /// <param name="maximumTimingsBytes">The capture policy's inclusive JSON document size limit.</param>
    /// <returns>The timings and contained staged report paths, without rewriting any reports.</returns>
    /// <remarks>The native capture independently validates the timings and retains the original report bytes.</remarks>
    internal static NativeCoverageCaptureInputs FromCollection(string collectionRoot, IReadOnlyList<string> expectedProjects,
        int maximumTimingsBytes)
    {
        var root = Path.GetFullPath(collectionRoot);
        var timingsPath = EvidenceArtifactValidation.GetContainedPath(root, "timings.json");
        using var directory = CoverageRunOutputLease.AcquireArtifactDirectory(root, createMissing: false, requirePrivate: false)
            ?? throw new DirectoryNotFoundException("Native coverage collection is unavailable.");
        using var stream = directory.OpenArtifactFile("timings.json", createNew: false, requirePrivate: false);
        var bytes = BoundedEvidenceDocument.Read(stream, maximumTimingsBytes)
            ?? throw new InvalidDataException("Native coverage timings exceed the capture JSON limit.");
        using var timings = JsonDocument.Parse(bytes);
        var document = timings.RootElement;
        if (document.ValueKind != JsonValueKind.Object || !document.TryGetProperty("projects", out var projects)
            || projects.ValueKind != JsonValueKind.Array || expectedProjects.Count == 0
            || projects.GetArrayLength() != expectedProjects.Count)
            throw new InvalidDataException("Native coverage timings must contain exactly the declared projects.");

        var expected = expectedProjects.ToHashSet(StringComparer.Ordinal);
        var reports = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in projects.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("project", out var projectValue) || projectValue.ValueKind != JsonValueKind.String
                || projectValue.GetString() is not { } project || !expected.Contains(project)
                || !entry.TryGetProperty("coverageFile", out var reportValue) || reportValue.ValueKind != JsonValueKind.String
                || reportValue.GetString() is not { } report || string.IsNullOrWhiteSpace(report))
                throw new InvalidDataException("Native coverage timings contain an invalid project-to-report binding.");
            if (!reports.TryAdd(project, EvidenceArtifactValidation.GetContainedPath(root, report)))
                throw new InvalidDataException("Native coverage timings contain a duplicate project.");
        }

        return new(timingsPath, reports);
    }

    /// <summary>Loads the explicit report index used by existing native capture replays.</summary>
    /// <param name="rawIndexPath">Bounded JSON object mapping project identities to original report paths.</param>
    /// <param name="timingsPath">The caller's matching collection timings path.</param>
    /// <returns>The supplied timings path and an ordinal copy of the supplied report index.</returns>
    internal static NativeCoverageCaptureInputs FromReplay(string rawIndexPath, string timingsPath)
    {
        using var index = File.OpenRead(rawIndexPath);
        if (index.Length > 1048576) throw new InvalidDataException();
        var bytes = new byte[checked((int)index.Length)];
        index.ReadExactly(bytes);
        if (index.ReadByte() != -1) throw new InvalidDataException();
        using var indexJson = JsonDocument.Parse(bytes);
        var reports = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var report in indexJson.RootElement.EnumerateObject())
            if (!reports.TryAdd(report.Name, report.Value.GetString() ?? throw new InvalidDataException()))
                throw new InvalidDataException();
        return new(timingsPath, reports);
    }
}
