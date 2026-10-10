using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Closed same-image N12 child procedure; never a producer, application or admitted worker.</summary>
/// <remarks>
/// Only a compiled N12 image can dispatch it. After the genuine worker finishes, the parent starts the
/// same pinned host/entry, accepts its actual PID acknowledgement, and then exits normally. The child
/// retains inherited stdout until the original systemd worker unit stops. Its private stderr ACK has
/// a separate parent-owned reader; it cannot consume the supervisor's original output pipe. The root
/// must independently bind the observed child PID to actual UID/GID/starttime/cgroup, observe unfinished
/// output before stop, and stop/join the original unit and output. No checkpoint byte establishes authority.
/// </remarks>
internal static partial class LinuxN12Descendant
{
    /// <summary>Reserved private role; aliases reject before ordinary CLI composition.</summary>
    internal const string Role = "n12-output-holder";
    /// <summary>Maximum complete private child ACK or supervisor-pump frame.</summary>
    internal const int MaximumFrameBytes = 64;
    private const string ReadyPrefix = "N12-child-ready:";
    internal const string FramePrefix = "N12-descendant:";

    /// <summary>Parses a canonical positive process ID as data, without a process handle or admission.</summary>
    internal static int ParsePid(string value)
    {
        if (string.IsNullOrEmpty(value) || value[0] == '0' || value.Length > 10
            || value.Any(static character => character is < '0' or > '9')
            || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
            throw Rejected();
        return pid;
    }

    /// <summary>Builds fixed argument data only; supplied paths in portable tests establish no pinned deployment.</summary>
    /// <remarks>Runtime callers supply only the actual current host and managed entry. No shell, case, endpoint or environment override is accepted.</remarks>
    internal static ProcessStartInfo CreateStartInfo(string host, string entry, int parentPid)
    {
        foreach (var path in new[] { host, entry })
            if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path) || path.Length > 4096
                || path.Any(char.IsControl) || path.Split('/').Skip(1).Any(static part => part is "" or "." or ".."))
                throw Rejected();
        if (parentPid <= 0 || !entry.EndsWith(".dll", StringComparison.Ordinal)) throw Rejected();
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = false, // Retain the actual root-owned stdout pipe in the child.
            RedirectStandardInput = false,
        };
        foreach (var argument in new[] { entry, "evidence", Role, "--parent", parentPid.ToString(CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(argument);
        return start;
    }

    /// <summary>Starts only the current image after the genuine worker procedure returned, then publishes the acknowledged PID.</summary>
    /// <remarks>
    /// The process object is retained before Start. A five-second ACK cap is linked to the original caller;
    /// the already armed original worker-unit/job deadline remains the independent bound. Neither deadline
    /// is renewed. On rejection the original root still owns and stops the complete unit; Dispose closes
    /// only local process/ACK handles and never claims physical child exit. On success the child deliberately
    /// outlives this leader, so the root's ordinary stop/group/pump joins must finish containment.
    /// </remarks>
    internal static async Task StartAfterWorkerAsync(CancellationToken token)
    {
        try { await StartCoreAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (EvidenceAdmissionException) { throw; }
        catch (Exception error) when (SupervisionOutputPipeOwnership.Recoverable(error)) { throw Rejected(); }
    }

    private static async Task StartCoreAsync(CancellationToken token)
    {
        RequirePrivatePlatform();
        token.ThrowIfCancellationRequested();
        using var child = new Process
        {
            StartInfo = CreateStartInfo(Environment.ProcessPath ?? throw Rejected(),
                Assembly.GetEntryAssembly()?.Location ?? throw Rejected(), Environment.ProcessId)
        };
        if (!child.Start()) throw Rejected();
        using var acknowledgement = CancellationTokenSource.CreateLinkedTokenSource(token);
        acknowledgement.CancelAfter(TimeSpan.FromSeconds(5));
        await ReadReadyAsync(child.StandardError.BaseStream, child.Id, acknowledgement.Token).ConfigureAwait(false);
        if (child.HasExited) throw Rejected();
        token.ThrowIfCancellationRequested();
        using var error = Console.OpenStandardError();
        var frame = Encoding.ASCII.GetBytes(FramePrefix + child.Id.ToString(CultureInfo.InvariantCulture) + "\n");
        await error.WriteAsync(frame, token).ConfigureAwait(false);
        await error.FlushAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }

    /// <summary>Reads one bounded child ACK from the parent-owned private pipe; cancellation/error never supplies an ACK.</summary>
    internal static async Task ReadReadyAsync(Stream stream, int expectedPid, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try { await ReadReadyCoreAsync(stream, expectedPid, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (EvidenceAdmissionException) { throw; }
        catch (Exception error) when (SupervisionOutputPipeOwnership.Recoverable(error)) { throw Rejected(); }
    }

    private static async Task ReadReadyCoreAsync(Stream stream, int expectedPid, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (expectedPid <= 0) throw Rejected();
        var frame = new byte[MaximumFrameBytes];
        for (var length = 0; length < frame.Length; length++)
        {
            var count = await stream.ReadAsync(frame.AsMemory(length, 1), token).ConfigureAwait(false);
            if (count != 1) throw Rejected();
            if (frame[length] != (byte)'\n') continue;
            var expected = Encoding.ASCII.GetBytes(ReadyPrefix + expectedPid.ToString(CultureInfo.InvariantCulture) + "\n");
            if (!frame.AsSpan(0, length + 1).SequenceEqual(expected)) throw Rejected();
            token.ThrowIfCancellationRequested();
            return;
        }
        throw Rejected();
    }

    /// <summary>Checks its actual parent and holds inherited output until the original OS unit terminates it.</summary>
    /// <remarks>The role reads no descriptor, performs no ready/control request, allocates no artifacts and returns no claim.</remarks>
    internal static async Task HoldOutputAsync(string parentPid, CancellationToken token)
    {
        try { await HoldCoreAsync(parentPid, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (EvidenceAdmissionException) { throw; }
        catch (Exception error) when (SupervisionOutputPipeOwnership.Recoverable(error)) { throw Rejected(); }
    }

    private static async Task HoldCoreAsync(string parentPid, CancellationToken token)
    {
        RequirePrivatePlatform();
        if (GetParentPid() != ParsePid(parentPid)) throw Rejected();
        token.ThrowIfCancellationRequested();
        using var output = Console.OpenStandardOutput();
        using var error = Console.OpenStandardError();
        var ready = Encoding.ASCII.GetBytes(ReadyPrefix + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "\n");
        await error.WriteAsync(ready, token).ConfigureAwait(false);
        await error.FlushAsync(token).ConfigureAwait(false);
        await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
    }

    private static void RequirePrivatePlatform()
    {
        if (!EvidenceNativeQualification.DescendantEnabled || !OperatingSystem.IsLinux()
            || RuntimeInformation.ProcessArchitecture != Architecture.X64 || RuntimeInformation.OSArchitecture != Architecture.X64
            || GetUid() is 0 or uint.MaxValue || GetGid() is 0 or uint.MaxValue) throw Rejected();
    }

    internal static EvidenceAdmissionException Rejected() => new("ASEVD410", "The private descendant checkpoint could not complete.");

    [LibraryImport("libc", EntryPoint = "getppid")] private static partial int GetParentPid();
    [LibraryImport("libc", EntryPoint = "getuid")] private static partial uint GetUid();
    [LibraryImport("libc", EntryPoint = "getgid")] private static partial uint GetGid();
}

/// <summary>Bounded first stderr-frame data from the original received-byte-charged pump, never a second reader.</summary>
internal sealed class SupervisionDescendantObservation
{
    private readonly byte[] _frame = new byte[LinuxN12Descendant.MaximumFrameBytes];
    private readonly TaskCompletionSource<int> _pid = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _length;
    private bool _ended;

    /// <summary>Gets parsed PID data only; root kernel/process identity is still mandatory.</summary>
    internal Task<int> Observed => _pid.Task;

    /// <summary>Feeds already charged bytes; malformed, oversized or noncanonical frames fail permanently.</summary>
    internal void Feed(ReadOnlySpan<byte> bytes)
    {
        if (_ended) return;
        foreach (var value in bytes)
        {
            if (_length == _frame.Length) { Reject(); return; }
            _frame[_length++] = value;
            if (value != (byte)'\n') continue;
            _ended = true;
            try
            {
                var prefix = Encoding.ASCII.GetBytes(LinuxN12Descendant.FramePrefix);
                if (!_frame.AsSpan(0, _length - 1).StartsWith(prefix)) throw LinuxN12Descendant.Rejected();
                var data = _frame.AsSpan(prefix.Length, _length - prefix.Length - 1);
                if (data.ContainsAnyExceptInRange((byte)'0', (byte)'9')) throw LinuxN12Descendant.Rejected();
                _pid.TrySetResult(LinuxN12Descendant.ParsePid(Encoding.ASCII.GetString(data)));
            }
            catch (EvidenceAdmissionException) { Reject(); }
            return;
        }
    }

    /// <summary>Actual EOF/read failure before a complete frame rejects, and is never an observed descendant.</summary>
    internal void Complete() { if (!_ended) Reject(); }

    private void Reject()
    {
        _ended = true;
        _pid.TrySetException(LinuxN12Descendant.Rejected());
        _ = _pid.Task.Exception;
    }
}
