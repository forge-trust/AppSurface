using System.Collections.Frozen;
using System.Text;
using ForgeTrust.AppSurface.Core;

namespace ForgeTrust.AppSurface.Config;

/// <summary>One parsed logical-key request within a top-level resolution or audit scope.</summary>
/// <remarks>
/// Providers consume requests from the manager; they never translate application input or create a second scope.
/// Environment spelling is independent of logical-key identity.
/// </remarks>
public sealed class ConfigProviderRequest
{
    /// <summary>Constructs a request, sharing an existing operation scope when supplied.</summary>
    internal ConfigProviderRequest(string environment, AppSurfaceConfigKey key, ConfigResolutionScope? scope = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(key);
        Environment = environment;
        Key = key;
        Scope = scope ?? new ConfigResolutionScope();
    }

    /// <summary>Gets the exact active environment name.</summary>
    public string Environment { get; }
    /// <summary>Gets the parsed logical identity, preserving its chosen spelling.</summary>
    public AppSurfaceConfigKey Key { get; }
    /// <summary>Gets the operation-owned snapshot and notice collector.</summary>
    internal ConfigResolutionScope Scope { get; }
    /// <summary>Gets the application parser origin, which does not affect key equality.</summary>
    internal ConfigKeyInputOrigin InputOrigin => Key.InputOrigin;
    /// <summary>Gets the original input only for train-1 native alias generation.</summary>
    internal string? OriginalInput => Key.OriginalInput;
}

/// <summary>Owns one environment snapshot, notice collector, and optional aggregate audit budget.</summary>
/// <remarks>The lazy snapshot caches success and failure. Dispose an audit scope after every remote waiter finishes.</remarks>
internal sealed class ConfigResolutionScope : IDisposable
{
    private Lazy<ConfigEnvironmentSnapshot>? _environment;
    private readonly object _environmentGate = new();
    private readonly List<ConfigResolutionNotice> _notices = [];
    private readonly HashSet<string> _noticeIdentities = new(StringComparer.Ordinal);
    private readonly object _noticeGate = new();
    private readonly CancellationToken _callerCancellation;
    private readonly CancellationTokenSource? _auditDeadline;
    private readonly SemaphoreSlim? _remoteConcurrency;
    private readonly int _remoteLookupLimit;
    private int _remoteLookups;
    private ConfigProviderTerminalDiagnostic? _incompleteAuditDiagnostic;

    /// <summary>Creates a scope; audit options enable a shared deadline, lookup limit, and concurrency limit.</summary>
    internal ConfigResolutionScope(CancellationToken cancellationToken = default, ConfigResourceOptions? auditOptions = null)
    {
        _callerCancellation = cancellationToken;
        if (auditOptions is not null)
        {
            var limits = auditOptions.Snapshot();
            _auditDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _auditDeadline.CancelAfter(limits.AuditTimeout);
            _remoteConcurrency = new SemaphoreSlim(limits.MaxAuditConcurrency, limits.MaxAuditConcurrency);
            _remoteLookupLimit = limits.MaxAuditRemoteLookups;
        }

        CancellationToken = _auditDeadline?.Token ?? cancellationToken;
    }

    /// <summary>Gets cancellation for the caller's wait, never ownership of a shared remote fetch.</summary>
    internal CancellationToken CancellationToken { get; }

    /// <summary>Gets whether this operation owns an aggregate audit budget.</summary>
    internal bool IsAudit => _auditDeadline is not null;

    /// <summary>Gets the first budget failure so a report cannot claim complete resolution or notice evidence.</summary>
    internal ConfigProviderTerminalDiagnostic? IncompleteAuditDiagnostic => Volatile.Read(ref _incompleteAuditDiagnostic);

    /// <summary>
    /// Reserves one uncached remote lookup and a concurrency slot within the operation deadline.
    /// Dispose the lease after the caller stops waiting; this never cancels a shared fetch.
    /// Cached reads need no reservation. A rejected reservation must not start a remote operation.
    /// </summary>
    internal bool TryAcquireRemoteLookup(out IDisposable? lease, out ConfigProviderTerminalDiagnostic? diagnostic)
    {
        lease = null;
        diagnostic = null;
        _callerCancellation.ThrowIfCancellationRequested();
        if (!IsAudit)
        {
            return true;
        }

        if (CancellationToken.IsCancellationRequested)
        {
            diagnostic = MarkAuditDeadline();
            return false;
        }

        while (true)
        {
            var current = Volatile.Read(ref _remoteLookups);
            if (current >= _remoteLookupLimit)
            {
                diagnostic = MarkIncomplete("config-audit-remote-lookup-limit");
                return false;
            }

            if (Interlocked.CompareExchange(ref _remoteLookups, current + 1, current) == current)
            {
                break;
            }
        }

        try
        {
            _remoteConcurrency!.Wait(CancellationToken);
            lease = new RemoteLookupLease(_remoteConcurrency);
            return true;
        }
        catch (OperationCanceledException) when (!_callerCancellation.IsCancellationRequested)
        {
            diagnostic = MarkAuditDeadline();
            return false;
        }
    }

    /// <summary>Records a deadline without converting explicit caller cancellation to an audit failure.</summary>
    internal ConfigProviderTerminalDiagnostic MarkAuditDeadline()
    {
        _callerCancellation.ThrowIfCancellationRequested();
        return MarkIncomplete("config-audit-deadline");
    }

    private ConfigProviderTerminalDiagnostic MarkIncomplete(string code)
    {
        var diagnostic = ConfigDiagnosticCatalog.Terminal(code);
        Interlocked.CompareExchange(ref _incompleteAuditDiagnostic, diagnostic, null);
        return diagnostic;
    }

    /// <summary>Gets the same captured snapshot or failure for every request in this scope.</summary>
    internal ConfigEnvironmentSnapshot GetEnvironmentSnapshot(IEnvironmentProvider provider, ConfigResourceOptions options)
    {
        Lazy<ConfigEnvironmentSnapshot> holder;
        lock (_environmentGate)
        {
            holder = _environment ??= new Lazy<ConfigEnvironmentSnapshot>(
                () => ConfigEnvironmentSnapshot.Capture(provider, options), LazyThreadSafetyMode.ExecutionAndPublication);
        }

        return holder.Value;
    }

    /// <summary>Collects bounded value-free notices; saturation marks audit evidence incomplete.</summary>
    internal void AddNotice(string provider, AppSurfaceConfigKey key, ConfigProviderNotice notice, int capacity)
    {
        var identity = ConfigNoticeHistoryStore.CreateIdentity(notice.Code, provider, "", key, notice.SafeSourceIdentifier);
        lock (_noticeGate)
        {
            if (_noticeIdentities.Contains(identity)) { return; }
            if (_notices.Count < capacity)
            {
                _noticeIdentities.Add(identity);
                _notices.Add(new ConfigResolutionNotice(provider, key, notice));
            }
            else
            {
                MarkIncomplete("config-audit-notice-limit");
            }
        }
    }

    /// <summary>Returns a point-in-time view of collected notices.</summary>
    internal IReadOnlyList<ConfigResolutionNotice> Notices
    {
        get
        {
            lock (_noticeGate)
            {
                return _notices.ToArray();
            }
        }
    }

    /// <summary>Gets the current notice count without allocating a snapshot before a patch transaction.</summary>
    internal int NoticeCount
    {
        get
        {
            lock (_noticeGate)
            {
                return _notices.Count;
            }
        }
    }

    /// <summary>Releases audit timer and semaphore resources after all report work and leases finish.</summary>
    public void Dispose()
    {
        _auditDeadline?.Dispose();
        _remoteConcurrency?.Dispose();
    }

    /// <summary>Releases a remote concurrency slot at most once.</summary>
    private sealed class RemoteLookupLease(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? _semaphore = semaphore;
        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}

/// <summary>A value-free notice with the provider and logical identity that produced it.</summary>
internal sealed record ConfigResolutionNotice(string Provider, AppSurfaceConfigKey Key, ConfigProviderNotice Notice);

/// <summary>An immutable exact-name, folded-name, and prefix index over one environment capture.</summary>
internal sealed class ConfigEnvironmentSnapshot
{
    private readonly FrozenDictionary<string, string[]> _foldedNames;
    private readonly string[] _orderedNames;

    private ConfigEnvironmentSnapshot(Dictionary<string, string> entries)
    {
        Entries = entries.ToFrozenDictionary(StringComparer.Ordinal);
        _foldedNames = entries.Keys.GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToFrozenDictionary(group => group.Key,
                group => group.Order(StringComparer.Ordinal).ToArray(), StringComparer.OrdinalIgnoreCase);
        _orderedNames = entries.Keys.Order(StringComparer.OrdinalIgnoreCase).ThenBy(name => name, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Gets exact native names and sensitive values; never serialize or log this dictionary.</summary>
    internal FrozenDictionary<string, string> Entries { get; }

    /// <summary>Captures all entries once, rejecting an oversized snapshot before index publication.</summary>
    internal static ConfigEnvironmentSnapshot Capture(IEnvironmentProvider provider, ConfigResourceOptions options)
    {
        var entries = provider.CaptureEnvironmentVariables();
        if (entries.Count > options.MaxEnvironmentEntries)
        {
            throw new ConfigResourceLimitException("config-environment-entry-limit");
        }

        var copy = new Dictionary<string, string>(StringComparer.Ordinal);
        long bytes = 0;
        foreach (var entry in entries)
        {
            bytes += Encoding.UTF8.GetByteCount(entry.Key) + (long)Encoding.UTF8.GetByteCount(entry.Value);
            if (bytes > options.MaxEnvironmentBytes || copy.Count >= options.MaxEnvironmentEntries)
            {
                throw new ConfigResourceLimitException("config-environment-snapshot-limit");
            }

            copy.Add(entry.Key, entry.Value);
        }

        return new ConfigEnvironmentSnapshot(copy);
    }

    /// <summary>Finds every exact native spelling equal to the expected name under ordinal ignore-case comparison.</summary>
    internal IReadOnlyList<string> GetMatchingNames(string expectedName) =>
        _foldedNames.TryGetValue(expectedName, out var names) ? Array.AsReadOnly(names) : [];

    /// <summary>Enumerates only present names below a native prefix, using binary search instead of speculative probes.</summary>
    internal IEnumerable<string> GetDescendantNames(string prefix)
    {
        // Find the first folded match. Array.BinarySearch may return the middle of a case-collision group.
        var start = 0;
        var end = _orderedNames.Length;
        while (start < end)
        {
            var middle = start + (end - start) / 2;
            if (StringComparer.OrdinalIgnoreCase.Compare(_orderedNames[middle], prefix) < 0)
            {
                start = middle + 1;
            }
            else
            {
                end = middle;
            }
        }

        for (var index = start; index < _orderedNames.Length; index++)
        {
            var name = _orderedNames[index];
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                yield break;
            }

            yield return name;
        }
    }
}

/// <summary>Signals a bounded-input failure without carrying values or raw external exception text.</summary>
internal sealed class ConfigResourceLimitException(string code) : Exception(code)
{
    /// <summary>Gets the stable value-safe resource diagnostic code.</summary>
    internal string Code { get; } = code;
}
