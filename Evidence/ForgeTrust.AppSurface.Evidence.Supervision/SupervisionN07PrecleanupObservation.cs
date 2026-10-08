using System.Text.Json;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Bounded fixed-frame observation from one original stderr pump; never an owner or lease.</summary>
/// <remarks>
/// The native pipe factory reserves this task before pump dispatch. Only its original stderr pump
/// feeds bytes after the existing shared quota charge. The first complete line must be the exact
/// CheckParentIdentity/Admission allocation-catch frame. EOF, cancellation and read failure without
/// that line reject. A later pump failure remains sticky and prevents final diagnostic projection.
/// Pure Feed controls exercise data bookkeeping only; they cannot construct LinuxOutputPipes.
/// </remarks>
internal sealed class SupervisionN07PrecleanupObservation
{
    private readonly TaskCompletionSource<byte[]> _observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly byte[] _line = new byte[1024];
    private int _length;
    private bool _lineComplete;
    private int _failed;

    /// <summary>The original one-shot observation, retained before any read begins.</summary>
    internal Task<byte[]> Task => _observed.Task;
    /// <summary>Sticky framing or original-pump failure data; it cannot revoke native ownership.</summary>
    internal bool Failed => Volatile.Read(ref _failed) != 0;

    /// <summary>Consumes original stderr bytes; never throws through or replaces the original pump.</summary>
    internal void Feed(ReadOnlySpan<byte> bytes)
    {
        if (_lineComplete || Failed) return;
        foreach (var value in bytes)
        {
            if (value == (byte)'\n')
            {
                _lineComplete = true;
                try { var line = _line.AsSpan(0, _length).ToArray(); Validate(line); _observed.TrySetResult(line); }
                catch (Exception) { Reject(); }
                return;
            }
            if (_length == _line.Length || value is < 0x20 or > 0x7e) { Reject(); return; }
            _line[_length++] = value;
        }
    }

    /// <summary>Called by the same original pump after its final EOF/failure observations.</summary>
    internal void Complete(bool actualCleanEof)
    {
        if (!actualCleanEof || !_lineComplete || !_observed.Task.IsCompletedSuccessfully) Reject();
        if (_observed.Task.IsFaulted) _ = _observed.Task.Exception; // Observe the retained failed data task even on pre-READY teardown.
    }

    /// <summary>Validates copied closed data, without authenticating a process, descriptor or request.</summary>
    internal static void Validate(byte[] bytes)
    {
        if (bytes.Length is 0 or > 1024) throw Rejected();
        using var json = JsonDocument.Parse(bytes);
        var value = json.RootElement;
        string[] keys = ["schema", "phase", "operation", "error_family", "capture_point", "terminal_observed",
            "observation_only", "native_authority", "native_acceptance"];
        if (value.ValueKind != JsonValueKind.Object) throw Rejected();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!keys.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) throw Rejected();
        if (seen.Count != keys.Length || value.GetProperty("schema").GetString() != "issue779-n07-precleanup-allocation-fault-v1"
            || value.GetProperty("phase").GetString() != "Allocation"
            || value.GetProperty("operation").GetString() != "CheckParentIdentity"
            || value.GetProperty("error_family").GetString() != "Admission"
            || value.GetProperty("capture_point").GetString() != "AllocateCatchBeforeCallbackRethrow"
            || value.GetProperty("terminal_observed").GetBoolean() || !value.GetProperty("observation_only").GetBoolean()
            || value.GetProperty("native_authority").GetBoolean() || value.GetProperty("native_acceptance").GetBoolean())
            throw Rejected();
    }

    private void Reject() { Interlocked.Exchange(ref _failed, 1); _observed.TrySetException(Rejected()); }
    private static InvalidOperationException Rejected() => new("N07 original stderr observation rejected.");
}
