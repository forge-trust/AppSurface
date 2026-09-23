using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config;

/// <summary>Resolves environment values and isolated child patches from one operation snapshot.</summary>
/// <remarks>
/// Canonical and compatibility candidates use the same source projection for values and audit evidence.
/// Native claims are checked before reading values. Getter-only aggregates are copied through their public members
/// into isolated constructor-created objects. If public construction cannot retain read-only state, the entire patch
/// is terminal; private fields and serialization are never used to silently discard that state. Getter-only structs
/// cannot publish child writes. Missing effective descendants do not trigger construction or getter invocation.
/// </remarks>
/// <seealso href="https://appsurface.dev/guides/config-logical-keys">Logical configuration keys and the environment contract</seealso>
internal sealed class EnvironmentConfigProvider : IEnvironmentConfigProvider, IConfigValuePatcher,
    IConfigDiagnosticProvider, IConfigDiagnosticPatcher, IConfigProviderAuditKeyEnumerator
{
    private readonly IEnvironmentProvider _environmentProvider;
    private readonly ConfigResourceOptions _limits;
    private readonly IReadOnlyDictionary<AppSurfaceConfigKey, string> _mappings;
    private readonly AppSurfaceConfigKey[] _knownKeys;
    private readonly EnvironmentNativeClaims _claims;

    /// <summary>Creates a provider with frozen mappings and finalized declaration claims.</summary>
    /// <param name="environmentProvider">Snapshot source; point reads are never used for resolution.</param>
    /// <param name="environmentOptions">Exact suffix mappings and the ad-hoc claim capacity.</param>
    /// <param name="resourceOptions">Positive snapshot and binding limits.</param>
    /// <param name="declarationRegistry">Final declarations used for startup reverse-claim validation.</param>
    public EnvironmentConfigProvider(IEnvironmentProvider environmentProvider,
        IOptions<AppSurfaceEnvironmentConfigOptions>? environmentOptions = null,
        IOptions<ConfigResourceOptions>? resourceOptions = null,
        ConfigDeclarationRegistry? declarationRegistry = null)
    {
        _environmentProvider = environmentProvider ?? throw new ArgumentNullException(nameof(environmentProvider));
        _limits = (resourceOptions?.Value ?? new ConfigResourceOptions()).Snapshot();
        var options = environmentOptions?.Value ?? new AppSurfaceEnvironmentConfigOptions();
        _mappings = options.Snapshot();
        _knownKeys = (declarationRegistry?.Keys ?? []).Concat(_mappings.Keys).Distinct().ToArray();
        _claims = new EnvironmentNativeClaims(_knownKeys, _mappings, options.MaxAdHocClaims);
        _claims.Validate(environmentProvider.Environment);
    }

    /// <inheritdoc />
    public int Priority => -1;
    /// <inheritdoc />
    public string Name => nameof(EnvironmentConfigProvider);
    /// <inheritdoc />
    public string Environment => _environmentProvider.Environment;
    /// <inheritdoc />
    public bool IsDevelopment => _environmentProvider.IsDevelopment;
    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> CaptureEnvironmentVariables() => _environmentProvider.CaptureEnvironmentVariables();
    /// <inheritdoc />
    public string? GetEnvironmentVariable(string name, string? defaultValue = null) => _environmentProvider.GetEnvironmentVariable(name, defaultValue);

    /// <inheritdoc />
    public ConfigProviderValueResult<T> Resolve<T>(ConfigProviderRequest request)
    {
        var outcome = ResolveCore(request, typeof(T), ConfigAuditSourceRole.Override);
        return outcome.Code is not null
            ? ConfigProviderValueResult<T>.Terminal(outcome.TerminalDiagnostic!)
            : outcome.Value is null ? ConfigProviderValueResult<T>.Missing()
            : ConfigProviderValueResult<T>.Found((T)outcome.Value, outcome.Notices.ToArray());
    }

    /// <summary>Strict compatibility helper; typed requests have no historical aliases.</summary>
    [Obsolete("Use ConfigProviderRequest and Resolve<T>.")]
    public T? GetValue<T>(string environment, string key)
    {
        var logicalKey = AppSurfaceConfigKey.Parse(key);
        var result = Resolve<T>(new ConfigProviderRequest(environment, logicalKey));
        return result.Status == ConfigProviderValueStatus.Terminal
            ? throw new ConfigurationResolutionException(environment, logicalKey, Name, result.Diagnostic!)
            : result.Value;
    }

    ConfigValueResolution IConfigDiagnosticProvider.Resolve(ConfigProviderRequest request, Type valueType, ConfigAuditSourceRole role)
    {
        var outcome = ResolveCore(request, valueType, role);
        PublishNotices(request.Scope, outcome);
        return outcome.Code is not null
            ? new(request.Key, ConfigAuditEntryState.Invalid, null, outcome.Sources, outcome.Diagnostics)
            : outcome.Value is null ? ConfigValueResolution.Missing(request.Key)
            : new(request.Key, ConfigAuditEntryState.Resolved, outcome.Value, outcome.Sources, outcome.Diagnostics);
    }

    IReadOnlyList<ConfigAuditDiagnostic> IConfigDiagnosticProvider.GetReportDiagnostics(string environment) => [];

    /// <summary>Inventories only declared and explicitly mapped keys, never arbitrary process variables.</summary>
    public IReadOnlyList<ConfigProviderAuditDiscoveredKey> EnumerateKeys(string environment)
    {
        using var scope = new ConfigResolutionScope();
        return EnumerateKeys(environment, scope);
    }

    /// <summary>Inventories forward declarations using the audit report's existing immutable snapshot.</summary>
    internal IReadOnlyList<ConfigProviderAuditDiscoveredKey> EnumerateKeys(string environment, ConfigResolutionScope scope)
    {
        return _knownKeys.Select(key =>
        {
            var outcome = ResolveCore(new ConfigProviderRequest(environment, key, scope), typeof(string), ConfigAuditSourceRole.Base);
            PublishNotices(scope, outcome);
            return new ConfigProviderAuditDiscoveredKey(key, outcome.Value, ConfigAuditDiscoveredValueKind.Scalar,
                outcome.Sources, outcome.Diagnostics);
        }).ToArray();
    }

    ConfigPatchResult<T> IConfigValuePatcher.Patch<T>(ConfigProviderRequest request, T? currentValue) where T : default
    {
        var outcome = PatchCore(request, currentValue, typeof(T));
        return outcome.Code is not null
            ? ConfigPatchResult<T>.Terminal(outcome.TerminalDiagnostic!)
            : outcome.Value is null ? ConfigPatchResult<T>.NotApplied()
            : ConfigPatchResult<T>.Applied((T)outcome.Value);
    }

    ConfigPatchDiagnosticResult IConfigDiagnosticPatcher.TracePatch(ConfigProviderRequest request, object? currentValue, Type valueType)
    {
        var outcome = PatchCore(request, currentValue, valueType);
        return new(outcome.Value is not null, outcome.Value, outcome.Sources, outcome.Diagnostics) { Facts = outcome.Facts };
    }

    private Outcome ResolveCore(ConfigProviderRequest request, Type type, ConfigAuditSourceRole role)
    {
        var outcome = new Outcome();
        var snapshot = Begin(request, outcome);
        if (snapshot is not null)
        {
            Read(request, type, snapshot, Candidates(request), request.Key, role, outcome);
        }

        return outcome;
    }

    private ConfigEnvironmentSnapshot? Begin(ConfigProviderRequest request, Outcome outcome)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Scope.CancellationToken.ThrowIfCancellationRequested();
        var code = _claims.Claim(request);
        if (code is not null)
        {
            Fail(outcome, code, request.Key);
            return null;
        }

        try
        {
            var snapshot = request.Scope.GetEnvironmentSnapshot(_environmentProvider, _limits);
            request.Scope.CancellationToken.ThrowIfCancellationRequested();
            return snapshot;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Fail(outcome, exception is ConfigResourceLimitException limit ? limit.Code : "config-environment-snapshot-failed", request.Key);
            return null;
        }
    }

    private NativeCandidate[] Candidates(ConfigProviderRequest request) => EnvironmentConfigCodec.Candidates(request, _mappings)
        .Select(candidate => new NativeCandidate(candidate.Layer, candidate.Name, candidate.Legacy)).ToArray();

    private void Read(ConfigProviderRequest request, Type type, ConfigEnvironmentSnapshot snapshot,
        NativeCandidate[] candidates, AppSurfaceConfigKey key, ConfigAuditSourceRole role, Outcome outcome)
    {
        request.Scope.CancellationToken.ThrowIfCancellationRequested();
        var projected = Project(snapshot, candidates, key, descendants: false, outcome);
        if (outcome.Code is not null) return;
        if (projected?.Winner is { } winner)
        {
            if (!TryConvertStringToType(winner.Metadata[0], type, out var value))
            {
                Fail(outcome, "config-environment-conversion-failed", key, winner.NativeIdentifier);
                return;
            }

            outcome.Value = value;
            AddSources(outcome, projected, key, role, indexed: false);
            return;
        }

        var elementType = EnvironmentBindingPlan.CollectionElementType(type);
        if (elementType is null) return;
        projected = Project(snapshot, candidates, key, descendants: true, outcome);
        if (outcome.Code is not null || projected?.Winner is not { } collection) return;
        var items = new SortedDictionary<int, (string Name, string Value)>();
        foreach (var name in collection.Metadata)
        {
            request.Scope.CancellationToken.ThrowIfCancellationRequested();
            var suffix = name[(collection.NativeIdentifier.Length + 2)..];
            if (!int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                || !suffix.Equals(index.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            {
                Fail(outcome, "config-patch-failed", key, name);
                return;
            }

            items.Add(index, (name, snapshot.Entries[name]));
        }

        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;
        foreach (var (index, entry) in items)
        {
            if (!TryConvertStringToType(entry.Value, elementType, out var value))
            {
                Fail(outcome, "config-environment-conversion-failed", Append(key, index.ToString(CultureInfo.InvariantCulture)), entry.Name);
                return;
            }

            list.Add(value);
        }

        outcome.Value = EnvironmentBindingPlan.MaterializeCollection(type, elementType, list);
        if (outcome.Value is null)
        {
            Fail(outcome, "config-patch-failed", key);
            return;
        }

        AddSources(outcome, projected, key, role, indexed: true);
    }

    private ConfigSourceProjectionResult<string[]>? Project(ConfigEnvironmentSnapshot snapshot, NativeCandidate[] candidates,
        AppSurfaceConfigKey key, bool descendants, Outcome outcome)
    {
        var raw = new List<ConfigSourceEntry<string[]>>();
        var wrongCase = false;
        string? expectedName = null;
        foreach (var candidate in candidates)
        {
            var names = descendants ? snapshot.GetDescendantNames(candidate.Name + "__").ToArray()
                : snapshot.GetMatchingNames(candidate.Name).ToArray();
            if (names.Length == 0) continue;
            if (!descendants && names.Length > 1)
            {
                Fail(outcome, "config-key-collision", key);
                return null;
            }

            foreach (var name in names)
            {
                if (snapshot.GetMatchingNames(name).Count > 1)
                {
                    Fail(outcome, "config-key-collision", key);
                    return null;
                }

                wrongCase |= !name.StartsWith(candidate.Name, StringComparison.Ordinal);
                if (wrongCase) expectedName = candidate.Name;
            }

            raw.Add(new(key, key.Value, candidate.Layer, candidate.Name,
                descendants ? names : [snapshot.Entries[names[0]]], candidate.Legacy));
        }

        var projection = new ConfigSourceProjection<string[]>(raw, ["unscoped", "scoped"]);
        projection.TryGet(key, out var result);
        if (result?.IsTerminal == true || (wrongCase && raw.Count > 1))
        {
            Fail(outcome, "config-key-collision", key);
        }
        else if (wrongCase)
        {
            Fail(outcome, "config-key-environment-name-case", key, expectedName);
        }

        return result;
    }

    private void AddSources(Outcome outcome, ConfigSourceProjectionResult<string[]> projected, AppSurfaceConfigKey key,
        ConfigAuditSourceRole role, bool indexed)
    {
        foreach (var entry in projected.Entries)
        {
            var sourceRole = ReferenceEquals(entry, projected.Winner) ? role : ConfigAuditSourceRole.Base;
            if (indexed)
            {
                foreach (var name in entry.Metadata)
                {
                    var index = name[(entry.NativeIdentifier.Length + 2)..];
                    outcome.Sources.Add(Source(name, Append(key, index), sourceRole));
                }
            }
            else
            {
                outcome.Sources.Add(Source(entry.NativeIdentifier, key, sourceRole));
            }
        }

        if (projected.Winner!.IsLegacyAlias)
        {
            var notice = ConfigDiagnosticCatalog.LegacyAlias(projected.Winner.NativeIdentifier, EnvironmentConfigCodec.Encode(key));
            outcome.Notices.Add(notice);
            outcome.PatchNotices.Add((key, notice));
        }
    }

    private Outcome PatchCore(ConfigProviderRequest request, object? current, Type declaredType)
    {
        var outcome = new Outcome();
        var snapshot = Begin(request, outcome);
        if (snapshot is null) return outcome;
        var type = current?.GetType() ?? declaredType;
        if (!EnvironmentBindingPlan.IsComplex(type)) return outcome;
        var candidates = Candidates(request);
        if (!PresentChildren(request, snapshot, candidates, request.Key).Any()) return outcome;
        try
        {
            var clone = new EnvironmentObjectClone(_limits.MaxBindingDepth, request.Scope.CancellationToken);
            var target = current is null ? EnvironmentBindingPlan.Create(type) : clone.Copy(current, type);
            if (target is null) return outcome;
            if (PatchObject(request, snapshot, target, current, candidates, request.Key, outcome, 0,
                    new HashSet<object>(ReferenceEqualityComparer.Instance)) && outcome.Code is null)
            {
                outcome.Value = target;
                PublishNotices(request.Scope, outcome);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException && exception is not OutOfMemoryException)
        {
            Fail(outcome, exception is EnvironmentCloneException ? "config-patch-clone-failed" : "config-patch-failed", request.Key);
        }

        if (outcome.Code is not null)
        {
            outcome.Value = null;
            outcome.Sources.Clear();
            outcome.Facts.Clear();
        }

        return outcome;
    }

    private void PublishNotices(ConfigResolutionScope scope, Outcome outcome)
    {
        if (outcome.Code is not null || outcome.Value is null || outcome.PatchNotices.Count == 0) return;
        // The scope deduplicates rich resolution and inventory before checking its notice capacity.
        foreach (var (key, notice) in outcome.PatchNotices)
            scope.AddNotice(Name, key, notice, _limits.MaxNoticeIdentities);
    }

    /// <summary>Finds present child segments through native prefixes and the frozen forward mapping index.</summary>
    /// <remarks>Mapped descendants can live outside every parent native prefix; their exact suffix still replaces all child conventions.</remarks>
    private IEnumerable<string> PresentChildren(ConfigProviderRequest request, ConfigEnvironmentSnapshot snapshot,
        NativeCandidate[] candidates, AppSurfaceConfigKey key)
    {
        foreach (var candidate in candidates)
            foreach (var name in snapshot.GetDescendantNames(candidate.Name + "__"))
            {
                var segments = name[(candidate.Name.Length + 2)..].Split("__", StringSplitOptions.None);
                var logical = key;
                var replaced = false;
                foreach (var segment in segments)
                {
                    // Malformed present suffixes remain visible so binding can reject them transactionally.
                    if (!AppSurfaceConfigKey.TryParse(segment, out var part) || part.Segments.Length != 1) break;
                    logical = Append(logical, segment);
                    if (_mappings.ContainsKey(logical)) { replaced = true; break; }
                }
                if (!replaced) yield return segments[0];
            }

        foreach (var mapped in _mappings.Keys.Where(mapped => mapped.Segments.Length > key.Segments.Length && mapped.IsSameOrDescendantOf(key)))
        {
            request.Scope.CancellationToken.ThrowIfCancellationRequested();
            if (Candidates(new(request.Environment, mapped, request.Scope)).Any(candidate =>
                    snapshot.GetMatchingNames(candidate.Name).Count != 0 || snapshot.GetDescendantNames(candidate.Name + "__").Any()))
                yield return mapped.Segments[key.Segments.Length];
        }
    }

    private bool PatchObject(ConfigProviderRequest request, ConfigEnvironmentSnapshot snapshot, object target, object? evidence,
        NativeCandidate[] candidates, AppSurfaceConfigKey key, Outcome outcome, int depth, HashSet<object> visiting)
    {
        request.Scope.CancellationToken.ThrowIfCancellationRequested();
        var childNames = PresentChildren(request, snapshot, candidates, key)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (childNames.Length == 0) return false;
        if (depth >= _limits.MaxBindingDepth || !visiting.Add(target))
        {
            Fail(outcome, "config-patch-failed", key);
            return false;
        }

        var patched = false;
        var plan = EnvironmentBindingPlan.For(target.GetType());
        foreach (var childName in childNames)
        {
            request.Scope.CancellationToken.ThrowIfCancellationRequested();
            var members = plan.Members.Where(member => member.NativeName.Equals(childName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (members.Length != 1)
            {
                Fail(outcome, members.Length == 0 ? "config-patch-failed" : "config-key-collision", key);
                break;
            }

            var member = members[0];
            var childKey = Append(key, member.Name);
            if (!member.CanWrite && member.Type.IsValueType && EnvironmentBindingPlan.IsComplex(member.Type))
            {
                // A getter returns a boxed copy of a struct. Public child setters cannot publish it back to the aggregate.
                Fail(outcome, "config-patch-failed", childKey);
                break;
            }
            var prior = member.Read(target);
            var canMutateCollection = EnvironmentBindingPlan.IsMutableList(prior);
            var childCandidates = _mappings.ContainsKey(childKey)
                ? Candidates(new(request.Environment, childKey, request.Scope))
                : candidates.Select(candidate => candidate with { Name = candidate.Name + "__" + member.NativeName }).ToArray();
            if (!member.CanWrite && !canMutateCollection && (prior is null || !EnvironmentBindingPlan.IsComplex(member.Type)))
            {
                if (PresentChildren(request, snapshot, childCandidates, childKey).Any())
                    Fail(outcome, "config-patch-failed", childKey);
                if (outcome.Code is not null) break;
                continue;
            }
            var claim = _claims.Claim(new(request.Environment, childKey, request.Scope), childCandidates.Select(candidate => candidate.Name));
            if (claim is not null)
            {
                Fail(outcome, claim, childKey);
                break;
            }
            if (!EnvironmentBindingPlan.IsComplex(member.Type) && EnvironmentBindingPlan.CollectionElementType(member.Type) is null
                && PresentChildren(request, snapshot, childCandidates, childKey).Any())
            {
                Fail(outcome, "config-patch-failed", childKey);
                break;
            }
            var read = new Outcome();
            Read(request, member.Type, snapshot, childCandidates, childKey, ConfigAuditSourceRole.Patch, read);
            if (read.Code is not null)
            {
                outcome.Code = read.Code;
                outcome.TerminalDiagnostic = read.TerminalDiagnostic;
                outcome.Diagnostics.AddRange(read.Diagnostics);
                break;
            }

            var providerPrior = evidence is null ? null : member.Read(evidence);
            var child = prior;
            if (read.Value is not null)
            {
                if (member.CanWrite) member.Write(target, read.Value);
                else if (canMutateCollection) EnvironmentBindingPlan.ReplaceList((IList)prior!, (IEnumerable)read.Value);
                else continue;
                child = member.CanWrite ? read.Value : prior;
                patched = true;
                outcome.Sources.AddRange(read.Sources);
                outcome.Diagnostics.AddRange(read.Diagnostics);
                outcome.Notices.AddRange(read.Notices);
                outcome.PatchNotices.AddRange(read.PatchNotices);
                AddFacts(outcome, read.Sources, member.CanWrite ? ConfigPatchProvenanceAction.ReplacedCollection
                    : ConfigPatchProvenanceAction.PatchedExistingCollection, providerPrior, evidence is null ? 0 : null);
            }

            if (EnvironmentBindingPlan.IsComplex(member.Type))
            {
                child ??= member.CanWrite ? EnvironmentBindingPlan.Create(member.Type) : null;
                if (child is null && PresentChildren(request, snapshot, childCandidates, childKey).Any())
                {
                    Fail(outcome, "config-patch-failed", childKey);
                    break;
                }
                if (child is not null && PatchObject(request, snapshot, child, providerPrior, childCandidates, childKey,
                        outcome, depth + 1, visiting))
                {
                    if (member.CanWrite) member.Write(target, child);
                    patched = true;
                }
            }

            if (outcome.Code is not null) break;
        }

        visiting.Remove(target);
        return patched;
    }

    private static void AddFacts(Outcome outcome, IEnumerable<ConfigAuditSourceRecord> sources,
        ConfigPatchProvenanceAction action, object? prior, int? priorCount)
    {
        priorCount ??= EnvironmentBindingPlan.CollectionCount(prior);
        foreach (var source in sources.Where(source => source.Role == ConfigAuditSourceRole.Patch))
        {
            var key = AppSurfaceConfigKey.Parse(source.ConfigPath!);
            if (!int.TryParse(key.Segments[^1], out var index)) continue;
            outcome.Facts.Add(new(source.ConfigPath!, source, action, priorCount is null ? ConfigAuditPriorPresence.Unknown
                : index < priorCount ? ConfigAuditPriorPresence.Present : ConfigAuditPriorPresence.Missing));
        }
    }

    private ConfigAuditSourceRecord Source(string name, AppSurfaceConfigKey key, ConfigAuditSourceRole role) => new()
    {
        Kind = ConfigAuditSourceKind.EnvironmentVariable,
        ProviderName = Name,
        ProviderPriority = Priority,
        EnvironmentVariableName = name,
        ConfigPath = key.Value,
        AppliedToPath = key.Value,
        Role = role
    };

    private void Fail(Outcome outcome, string code, AppSurfaceConfigKey key, string? name = null)
    {
        outcome.Code = code;
        var terminalCode = code is "config-environment-conversion-failed" or "config-patch-clone-failed" ? "config-patch-failed" : code;
        outcome.TerminalDiagnostic = ConfigDiagnosticCatalog.Terminal(terminalCode, name is null ? [key.Value] : [key.Value, name]);
        outcome.Diagnostics.Add(new()
        {
            Code = code,
            Key = key.Value,
            ConfigPath = key.Value,
            Severity = ConfigAuditDiagnosticSeverity.Error,
            Source = name is null ? null : Source(name, key, ConfigAuditSourceRole.Patch),
            Message = outcome.TerminalDiagnostic.Problem
        });
    }

    private static AppSurfaceConfigKey Append(AppSurfaceConfigKey key, string segment) => AppSurfaceConfigKey.FromSegments([.. key.Segments, segment]);

    private sealed record NativeCandidate(string Layer, string Name, bool Legacy);

    private sealed class Outcome
    {
        internal object? Value { get; set; }
        internal string? Code { get; set; }
        internal ConfigProviderTerminalDiagnostic? TerminalDiagnostic { get; set; }
        internal List<ConfigProviderNotice> Notices { get; } = [];
        internal List<(AppSurfaceConfigKey Key, ConfigProviderNotice Notice)> PatchNotices { get; } = [];
        internal List<ConfigAuditSourceRecord> Sources { get; } = [];
        internal List<ConfigAuditDiagnostic> Diagnostics { get; } = [];
        internal List<ConfigPatchProvenanceFact> Facts { get; } = [];
    }
    private static bool TryConvertStringToType(string value, Type targetType, out object? parsed)
    {
        var nullableUnderlying = Nullable.GetUnderlyingType(targetType);
        if (nullableUnderlying != null)
        {
            if (string.IsNullOrEmpty(value))
            {
                parsed = null;
                return false;
            }

            targetType = nullableUnderlying;
        }

        if (targetType == typeof(string))
        {
            parsed = value;
            return true;
        }

        if (targetType.IsEnum)
        {
            if (Enum.TryParse(targetType, value, true, out var enumValue))
            {
                parsed = enumValue;
                return true;
            }

            parsed = null;
            return false;
        }

        if (targetType == typeof(Guid))
        {
            if (Guid.TryParse(value, out var guid))
            {
                parsed = guid;
                return true;
            }

            parsed = null;
            return false;
        }

        if (targetType == typeof(DateTimeOffset))
        {
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
            {
                parsed = dto;
                return true;
            }

            parsed = null;
            return false;
        }

        if (targetType == typeof(TimeSpan))
        {
            if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var ts))
            {
                parsed = ts;
                return true;
            }

            parsed = null;
            return false;
        }

        try
        {
            if (IsSimpleType(targetType))
            {
                parsed = Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
                return true;
            }

            parsed = JsonSerializer.Deserialize(value, targetType);
            return parsed != null;
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException
                                       or ArgumentException or JsonException or NotSupportedException)
        {
            parsed = null;
            return false;
        }
    }

    private static bool IsSimpleType(Type targetType) =>
        targetType.IsPrimitive
        || targetType == typeof(decimal)
        || targetType == typeof(DateTime);


}
