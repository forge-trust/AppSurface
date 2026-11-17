namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>One v3 invocation's checked, thread-safe logical admission accounting.</summary>
/// <remarks>Byte and record counters describe admitted work; staged counters describe admitted output files and bytes.
/// They do not measure decoded DOM heap or process working set. V2 continues using its original IO accounting.</remarks>
internal sealed class CoverageFanInBudget(long maximumBytes)
{
    private readonly object _gate = new();
    private readonly Dictionary<GeneratedOutputKey, long> _generatedOutputBytes = [];
    private long _used;
    private long _xmlBytes;
    private long _jsonBytes;
    private long _lineRecords;
    private long _branchRecords;
    private long _stagedBytes;
    private long _stagedFiles;

    /// <summary>Gets the remaining admitted logical bytes, including concurrent pipe charges.</summary>
    internal long Remaining { get { lock (_gate) return maximumBytes - _used; } }
    /// <summary>Snapshots actual charged logical bytes and the fixed limit for current-invocation proof tooling.</summary>
    internal CoverageFanInBudgetSnapshot Snapshot()
    {
        lock (_gate)
            return new(_used, maximumBytes, _xmlBytes, _jsonBytes, _lineRecords, _branchRecords, _stagedBytes, _stagedFiles);
    }

    /// <summary>Records successfully retained XML or JSON input bytes without charging cached reads again.</summary>
    internal void AdmitInput(long bytes, bool xml, bool json)
    {
        lock (_gate)
        {
            if (bytes < 0 || xml && json) throw new ArgumentOutOfRangeException(nameof(bytes));
            var xmlBytes = xml ? checked(_xmlBytes + bytes) : _xmlBytes;
            var jsonBytes = json ? checked(_jsonBytes + bytes) : _jsonBytes;
            _xmlBytes = xmlBytes;
            _jsonBytes = jsonBytes;
        }
    }

    /// <summary>Records structural XML records once, after the document passes admission.</summary>
    internal void AdmitXmlRecords(long lineRecords, long branchRecords)
    {
        lock (_gate)
        {
            if (lineRecords < 0 || branchRecords < 0) throw new ArgumentOutOfRangeException(nameof(lineRecords));
            var lines = checked(_lineRecords + lineRecords);
            var branches = checked(_branchRecords + branchRecords);
            _lineRecords = lines;
            _branchRecords = branches;
        }
    }

    /// <summary>Admits one staged payload or metadata file to the shared byte budget and staged counters.</summary>
    internal void AdmitStagedFile(long bytes)
    {
        lock (_gate)
        {
            if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
            var stagedBytes = checked(_stagedBytes + bytes);
            var stagedFiles = checked(_stagedFiles + 1);
            ChargeUnsafe(bytes);
            _stagedBytes = stagedBytes;
            _stagedFiles = stagedFiles;
        }
    }

    /// <summary>Charges nonnegative work exactly once at its owning boundary, throwing before excess allocation.</summary>
    internal void Charge(long bytes)
    {
        lock (_gate)
        {
            ChargeUnsafe(bytes);
        }
    }

    /// <summary>Charges observed generator output growth and makes that charge available to its later readback once.</summary>
    internal void ChargeGeneratedOutput(string path, string identity, long bytes)
    {
        lock (_gate)
        {
            var key = new GeneratedOutputKey(path, identity);
            var total = checked(_generatedOutputBytes.GetValueOrDefault(key) + bytes);
            ChargeUnsafe(bytes);
            _generatedOutputBytes[key] = total;
        }
    }

    /// <summary>Consumes the portion of a generated file already charged before a later bounded read.</summary>
    internal long ConsumeGeneratedOutputReadback(string path, string identity, long readLength)
    {
        lock (_gate)
        {
            if (!_generatedOutputBytes.Remove(new GeneratedOutputKey(path, identity), out var charged)) return 0;
            return Math.Min(charged, readLength);
        }
    }

    private void ChargeUnsafe(long bytes)
    {
        if (bytes < 0 || bytes > maximumBytes - _used)
            throw new CoverageFanInEvidenceException("limit-exceeded", "budget",
                "Preserve the fixed operation limits; reduce supported input work or correct the implementation before a fresh retry.");
        _used = checked(_used + bytes);
    }

    private readonly record struct GeneratedOutputKey(string Path, string Identity);
}

/// <summary>Logical budget and admission counters; none measures decoded DOM heap or process working set.</summary>
internal sealed record CoverageFanInBudgetSnapshot(long UsedBytes, long LimitBytes, long XmlBytes, long JsonBytes,
    long LineRecords, long BranchRecords, long StagedBytes, long StagedFiles);

/// <summary>Safe closed diagnostic cause at a v3 evidence boundary; never carries raw dependency output.</summary>
internal sealed class CoverageFanInEvidenceException(string cause, string role, string recovery)
    : IOException("Fan-in evidence could not be verified.")
{
    internal string Cause { get; } = cause;
    internal string Role { get; } = role;
    internal string Recovery { get; } = recovery;
}
