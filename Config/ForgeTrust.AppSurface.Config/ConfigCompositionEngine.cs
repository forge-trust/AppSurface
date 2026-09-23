using System.Text.Json;
using System.Text.Json.Nodes;

namespace ForgeTrust.AppSurface.Config;

/// <summary>The single composition authority for runtime, effective audit, and local-only startup validation.</summary>
internal sealed class ConfigCompositionEngine
{
    private readonly IEnvironmentConfigProvider _environment;
    private readonly ConfigCompositionJsonContract _json;
    private readonly ConfigCompositionPlanCompiler _compiler;
    private readonly ConfigCompositionExecutor _executor;
    /// <summary>The immutable registration snapshot shared by all entry points.</summary>
    internal ConfigSecretProviderRegistry Registry { get; }

    /// <summary>Captures one host's providers and positive limits. Payloads remain invocation-owned.</summary>
    public ConfigCompositionEngine(IEnvironmentConfigProvider environment, IEnumerable<IConfigProvider> bases,
        IEnumerable<IConfigSecretProvider> secretProviders, IEnumerable<IConfigSecretDeclarationSource> declarations,
        AppSurfaceConfigOptions options, TimeProvider time)
    {
        var valid = new AppSurfaceConfigOptionsValidator().Validate(null, options);
        if (valid.Failed) throw new Microsoft.Extensions.Options.OptionsValidationException(
            nameof(AppSurfaceConfigOptions), typeof(AppSurfaceConfigOptions), valid.Failures);
        // Snapshot mutable options so registration/configuration callbacks cannot change an existing plan's meaning.
        var snapshot = new AppSurfaceConfigOptions
        {
            ProviderlessResolutionBudget = options.ProviderlessResolutionBudget,
            MaxCompositionGraphDepth = options.MaxCompositionGraphDepth,
            MaxCompositionGraphNodes = options.MaxCompositionGraphNodes,
            MaxSecretDestinationsPerRoot = options.MaxSecretDestinationsPerRoot
        };
        var providers = bases.Where(p => p is not IConfigManager and not IEnvironmentConfigProvider)
            .OrderByDescending(p => p.Priority).ToList().AsReadOnly();
        _environment = environment;
        _json = new(snapshot);
        Registry = new(secretProviders);
        _compiler = new(_json, Registry, providers, declarations.Concat(providers.OfType<IConfigSecretDeclarationSource>()));
        _executor = new(environment, _json, snapshot, time);
    }

    /// <summary>Detects the explicit typed opt-in without resolving any provider.</summary>
    internal bool ContainsSecrets(Type type) => _json.ContainsSecrets(type);

    /// <summary>Gets declared secret paths for structural audit redaction, even when execution fails.</summary>
    internal IReadOnlyList<string> GetSecretPaths(string key, Type type)
    {
        try
        {
            var root = ConfigLogicalPath.Parse(key);
            var shape = _json.GetShape(type);
            // An unsupported contract cannot prove where its secret leaves end. Redact the whole inventory root.
            if (shape.Failures.Count > 0) return [root.Canonical];
            return shape.Secrets.Select(slot => ConfigCompositionPlanCompiler.Join(root, slot.Members).Canonical)
                .ToList().AsReadOnly();
        }
        catch (ArgumentException) { return []; }
    }

    /// <summary>Executes one observation. Runtime and audit receive identical semantics for identical inputs.</summary>
    internal ConfigCompositionExecutionResult Execute(string environment, string key, Type type)
    {
        var direct = _executor.TryDirect(environment, key, type, out var directDiagnostics);
        if (direct is not null) return direct;
        var result = _executor.Execute(_compiler.Compile(environment, key, type));
        return directDiagnostics.Count == 0 ? result : new(result.State, result.Value, result.Failures, result.Slots,
            result.Sources, directDiagnostics.Concat(result.Diagnostics).ToList().AsReadOnly(), result.DirectRoot);
    }

    /// <summary>Compiles known roots at startup without enabled secret reads or activating application wrappers.</summary>
    internal void ValidatePlan(string environment, string key, Type type)
    {
        if (!ContainsSecrets(type)) return;
        if (_executor.TryDirect(environment, key, type, out _) is { State: ConfigCompositionRootState.Resolved }) return;
        var plan = _compiler.Compile(environment, key, type);
        if (plan.Failures.Count > 0) throw new ConfigurationCompositionException(environment, key, plan.Failures);
    }
}

/// <summary>Owns raw payloads, source observations and opaque binding slots for one execution only.</summary>
internal sealed class ConfigCompositionExecutor(IEnvironmentConfigProvider environment, ConfigCompositionJsonContract json,
    AppSurfaceConfigOptions options, TimeProvider time)
{
    /// <summary>Probes every legacy direct-root candidate before compiling file policy or resolving providers.</summary>
    internal ConfigCompositionExecutionResult? TryDirect(string environmentName, string key, Type type,
        out IReadOnlyList<ConfigAuditDiagnostic> conversionDiagnostics)
    {
        var diagnostics = new List<ConfigAuditDiagnostic>();
        conversionDiagnostics = diagnostics.AsReadOnly();
        IReadOnlyList<string> candidates;
        try { candidates = ConfigEnvironmentCandidates.GetPathCandidates(environmentName, ConfigLogicalPath.Parse(key).Segments); }
        catch (ArgumentException) { return null; }
        foreach (var candidate in candidates)
        {
            var raw = environment.GetEnvironmentVariable(candidate);
            if (raw is null) continue;
            var wrappers = new List<IConfigSecretValue>();
            var traces = new List<ConfigSecretSlotTrace>();
            try
            {
                var shape = json.GetShape(type);
                if (shape.Failures.Count > 0) continue;
                if (JsonNode.Parse(raw) is not JsonObject buffer)
                {
                    diagnostics.Add(ConversionDiagnostic(key, candidate));
                    continue;
                }
                var root = ConfigLogicalPath.Parse(key);
                var invalid = false;
                foreach (var destination in shape.Secrets)
                {
                    object? value = null;
                    // A partial object must not silently erase an omitted secret declaration. Explicit null
                    // deliberately supplies an empty destination; every destination must still be present.
                    if (!ConfigCompositionTree.TryGet(buffer, destination.Members, out var node)
                        || (node is not null && !TryScalar(node, destination.InnerType, out value)))
                    { invalid = true; break; }
                    var path = ConfigCompositionPlanCompiler.Join(root, destination.Members).Canonical;
                    var secret = ConfigCompositionJsonContract.CreateSecret(destination.InnerType, true, value, environment.Name);
                    ConfigCompositionTree.Set(buffer, destination.Members, JsonValue.Create(wrappers.Count));
                    wrappers.Add(secret);
                    traces.Add(new(path, true, value is not null, secret.ResolvedProvider, null, "secret-direct-environment-root", [],
                        [EnvironmentSource(candidate, path)], null));
                }
                if (invalid) { diagnostics.Add(ConversionDiagnostic(key, candidate)); continue; }
                var bound = json.Bind(type, buffer, wrappers);
                if (bound is not null)
                    return new(ConfigCompositionRootState.Resolved, bound, [], traces.AsReadOnly(),
                        [EnvironmentSource(candidate, key)], diagnostics.AsReadOnly(), true);
            }
            catch { diagnostics.Add(ConversionDiagnostic(key, candidate)); }
            finally { wrappers.Clear(); }
        }
        return null;
    }

    /// <summary>Resolves one validated plan, with providerless uniqueness, exact rescue, and one final bind.</summary>
    internal ConfigCompositionExecutionResult Execute(ConfigCompositionPlan plan)
    {
        if (plan.Failures.Count > 0) return new(ConfigCompositionRootState.Failed, null, plan.Failures,
            plan.Slots.Select(slot => UnresolvedTrace(slot, plan.Failures)).ToList().AsReadOnly(), [], [], false);
        var failures = new List<ConfigCompositionFailure>();
        var sources = new List<ConfigAuditSourceRecord>();
        var diagnostics = new List<ConfigAuditDiagnostic>();
        var traces = new List<ConfigSecretSlotTrace>();
        var wrappers = new List<IConfigSecretValue>();
        var buffer = new JsonObject();
        var hasContribution = plan.Slots.Any(s => s.Reference is not null);
        var sensitiveBase = false;
        string? baseProvider = null;
        var root = ConfigLogicalPath.Parse(plan.Key);
        // The monotonic root deadline spans all unconstrained slots and includes preceding root work.
        var budget = new ConfigSecretResolutionContext(time, options.ProviderlessResolutionBudget);
        try
        {
            foreach (var provider in plan.Bases)
            {
                ConfigCompositionValueResolution raw;
                try { raw = ((IConfigCompositionValueProvider)provider).ResolveRaw(plan.Environment, plan.Key); }
                catch
                {
                    failures.Add(new(root.Canonical, "config-composition-base-failed"));
                    return Result();
                }
                if (raw.Status is ConfigCompositionValueResolutionStatus.Missing or ConfigCompositionValueResolutionStatus.Unclaimed) continue;
                if (raw.Status != ConfigCompositionValueResolutionStatus.Resolved)
                {
                    failures.Add(new(root.Canonical, "config-composition-base-failed", retryable: raw.Retryable));
                    return Result();
                }
                hasContribution = true;
                sensitiveBase = raw.IsSensitive;
                baseProvider = raw.ProviderName;
                try
                {
                    var parsed = JsonNode.Parse(raw.ReadRaw()!);
                    if (parsed is not JsonObject rawObject) throw new JsonException();
                    buffer = rawObject;
                }
                catch (JsonException) { failures.Add(new(root.Canonical, "config-composition-bind-failed")); return Result(); }
                sources.Add(new()
                {
                    Kind = provider is FileBasedConfigProvider ? ConfigAuditSourceKind.File : ConfigAuditSourceKind.Provider,
                    ProviderName = raw.ProviderName,
                    ProviderPriority = raw.Priority,
                    ConfigPath = root.Canonical,
                    AppliedToPath = root.Canonical,
                    Role = ConfigAuditSourceRole.Base,
                    Sensitivity = raw.IsSensitive ? ConfigAuditSensitivity.Sensitive : ConfigAuditSensitivity.NonSensitive
                });
                break;
            }

            foreach (var slot in plan.Slots)
            {
                object? value = null;
                string? winner = null;
                ConfigCompositionFailure? failure = null;
                var observed = new List<ConfigSecretProviderObservation>();
                var slotSources = new List<ConfigAuditSourceRecord>();
                var code = slot.Reference is null ? "secret-descriptor-absent" : slot.Enabled ? "secret-declared-enabled" : "secret-declared-disabled";
                if (sensitiveBase && ConfigCompositionTree.TryGet(buffer, slot.Destination.Members, out var lower) && lower is not null)
                {
                    if (TryScalar(lower, slot.Destination.InnerType, out value))
                    {
                        winner = baseProvider;
                        slotSources.Add(sources[0]);
                    }
                    else failure = new(slot.Path.Canonical, "secret-value-conversion-failed");
                }
                if (slot.Reference is not null && slot.Enabled)
                {
                    // An enabled reference claims its destination even when lower sensitive material exists.
                    value = null;
                    winner = null;
                    slotSources.Clear();
                    (value, winner, failure) = ResolveSecret(slot, budget, observed);
                    if (winner is not null)
                    {
                        code = "secret-resolved";
                        slotSources.Add(new()
                        {
                            Kind = ConfigAuditSourceKind.Provider,
                            ProviderName = winner,
                            ConfigPath = slot.Path.Canonical,
                            AppliedToPath = slot.Path.Canonical,
                            Role = ConfigAuditSourceRole.Patch,
                            Sensitivity = ConfigAuditSensitivity.Sensitive
                        });
                    }
                }
                if (TryEnvironmentScalar(plan.Environment, slot.Path, slot.Destination.InnerType, diagnostics, out var envValue, out var candidate))
                {
                    code = failure is null ? "secret-environment-supplied" : "secret-environment-rescued";
                    value = envValue;
                    winner = environment.Name;
                    failure = null;
                    slotSources.Add(EnvironmentSource(candidate!, slot.Path.Canonical));
                    hasContribution = true;
                }
                if (failure is not null) failures.Add(failure);
                var wrapper = ConfigCompositionJsonContract.CreateSecret(slot.Destination.InnerType, slot.Enabled, value, winner);
                wrappers.Add(wrapper);
                traces.Add(new(slot.Path.Canonical, slot.Enabled, wrapper.HasValue, wrapper.ResolvedProvider,
                    slot.ProviderConstraint, failure?.Code ?? code, observed.AsReadOnly(), slotSources.AsReadOnly(), slot.Source));
            }

            // Ordinary descendants still use the existing candidate/conversion machinery. Parent values cannot
            // supply or rescue a secret child: each typed slot is replaced by its separately classified opaque id.
            foreach (var member in plan.Shape.Members.OrderBy(m => m.Members.Count))
            {
                var path = ConfigCompositionPlanCompiler.Join(root, member.Members);
                if (!json.ContainsSecrets(member.ValueType) && environment is IConfigDiagnosticProvider diagnosticEnvironment)
                {
                    // Reuse indexed-array/dictionary support and first-parseable candidates from the existing
                    // environment implementation; ordinary subtrees contain no opaque secret destinations.
                    var resolved = diagnosticEnvironment.Resolve(plan.Environment, path.Dotted, member.ValueType, ConfigAuditSourceRole.Patch);
                    diagnostics.AddRange(resolved.Diagnostics);
                    if (resolved.State == ConfigAuditEntryState.Resolved)
                    {
                        try
                        {
                            ConfigCompositionTree.Set(buffer, member.Members, JsonSerializer.SerializeToNode(resolved.Value, member.ValueType));
                            sources.AddRange(resolved.Sources);
                            hasContribution = true;
                        }
                        catch { diagnostics.Add(ConversionDiagnostic(path.Canonical, "environment")); }
                    }
                    continue;
                }
                foreach (var candidate in ConfigEnvironmentCandidates.GetPathCandidates(plan.Environment, path.Segments))
                {
                    var raw = environment.GetEnvironmentVariable(candidate);
                    if (raw is null) continue;
                    if (!json.ContainsSecrets(member.ValueType)
                        && EnvironmentConfigProvider.TryConvertStringToType(raw, member.ValueType, out var converted))
                    {
                        try
                        {
                            ConfigCompositionTree.Set(buffer, member.Members, JsonSerializer.SerializeToNode(converted, member.ValueType));
                            sources.Add(EnvironmentSource(candidate, path.Canonical));
                            hasContribution = true;
                            break;
                        }
                        catch { diagnostics.Add(ConversionDiagnostic(path.Canonical, candidate)); }
                    }
                    else if (json.ContainsSecrets(member.ValueType))
                    {
                        try
                        {
                            if (JsonNode.Parse(raw) is not JsonObject obj) throw new JsonException();
                            ConfigCompositionTree.Set(buffer, member.Members, obj);
                            sources.Add(EnvironmentSource(candidate, path.Canonical));
                            hasContribution = true;
                            break;
                        }
                        catch { diagnostics.Add(ConversionDiagnostic(path.Canonical, candidate)); }
                    }
                    else diagnostics.Add(ConversionDiagnostic(path.Canonical, candidate));
                }
            }
            if (!hasContribution) return new(ConfigCompositionRootState.Missing, null, [], traces.AsReadOnly(), sources.AsReadOnly(), diagnostics.AsReadOnly(), false);
            for (var i = 0; i < plan.Slots.Count; i++)
                ConfigCompositionTree.Set(buffer, plan.Slots[i].Destination.Members, JsonValue.Create(i));
            if (failures.Count > 0) return Result();
            try
            {
                var bound = json.Bind(plan.ValueType, buffer, wrappers);
                if (bound is null) { failures.Add(new(root.Canonical, "config-composition-bind-failed")); return Result(); }
                return Result(bound);
            }
            catch { failures.Add(new(root.Canonical, "config-composition-bind-failed")); return Result(); }
        }
        finally
        {
            wrappers.Clear();
            buffer.Clear();
        }

        ConfigCompositionExecutionResult Result(object? value = null) => new(
            failures.Count > 0 ? ConfigCompositionRootState.Failed : ConfigCompositionRootState.Resolved,
            value, failures.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList().AsReadOnly(),
            traces.Concat(plan.Slots.Where(slot => !traces.Any(trace => trace.Path == slot.Path.Canonical))
                .Select(slot => UnresolvedTrace(slot, failures))).ToList().AsReadOnly(),
            sources.AsReadOnly(), diagnostics.AsReadOnly(), false);
    }

    private static ConfigSecretSlotTrace UnresolvedTrace(ConfigSecretPlanSlot slot, IReadOnlyList<ConfigCompositionFailure> failures) =>
        new(slot.Path.Canonical, slot.Enabled, false, null, slot.ProviderConstraint,
            failures.FirstOrDefault(failure => ConfigLogicalPath.Parse(failure.Path).Overlaps(slot.Path))?.Code
                ?? "secret-not-evaluated", [], [], slot.Source);

    private (object? Value, string? Provider, ConfigCompositionFailure? Failure) ResolveSecret(ConfigSecretPlanSlot slot,
        ConfigSecretResolutionContext budget, List<ConfigSecretProviderObservation> observations)
    {
        ConfigSecretProviderResolution? success = null;
        var successes = 0;
        var missing = false;
        foreach (var registration in slot.Providers)
        {
            var context = slot.ProviderConstraint is null ? budget : new ConfigSecretResolutionContext(time, TimeSpan.MaxValue);
            if (context.Remaining <= TimeSpan.Zero) return Failed("secret-providerless-resolution-budget-exceeded", retryable: true);
            ConfigSecretProviderResolution result;
            try
            {
                result = registration.Provider.Resolve(slot.Reference!, context);
                if (!string.Equals(result.ProviderId, registration.Id, StringComparison.Ordinal)) throw new InvalidOperationException();
            }
            catch (Exception exception) when (!IsFatalProviderException(exception))
            {
                result = ConfigSecretProviderResolution.ProviderFailed(registration.Id);
            }
            observations.Add(new(registration.Id, result.Status, result.Retryable));
            if (slot.ProviderConstraint is null && budget.Remaining <= TimeSpan.Zero)
                return Failed("secret-providerless-resolution-budget-exceeded", retryable: true);
            switch (result.Status)
            {
                case ConfigSecretProviderResolutionStatus.Resolved:
                    successes++;
                    if (successes == 1) success = result;
                    else success = null; // competing payloads are discarded without conversion
                    break;
                case ConfigSecretProviderResolutionStatus.Unclaimed: break;
                case ConfigSecretProviderResolutionStatus.Missing: missing = true; break;
                case ConfigSecretProviderResolutionStatus.AccessDenied: return Failed("secret-provider-access-denied", registration.Id);
                case ConfigSecretProviderResolutionStatus.Unavailable: return Failed("secret-provider-unavailable", registration.Id, result.Retryable);
                case ConfigSecretProviderResolutionStatus.InvalidReference: return Failed("secret-reference-invalid", registration.Id);
                default: return Failed("secret-provider-failed", registration.Id, result.Retryable);
            }
        }
        if (successes > 1) return Failed("secret-provider-ambiguous");
        if (success is null) return Failed(missing ? "secret-not-found" : "secret-reference-unsupported");
        if (!ConfigValueConverter.TryConvert(success.ReadSensitiveValue()!, slot.Destination.InnerType, out var value) || value is null)
            return Failed("secret-value-conversion-failed", success.ProviderId);
        return (value, success.ProviderId, null);

        (object?, string?, ConfigCompositionFailure) Failed(string code, string? provider = null, bool retryable = false) =>
            (null, null, new(slot.Path.Canonical, code, provider, retryable, slot.Source));
    }

    /// <summary>Identifies process-level failures that must escape the provider redaction boundary.</summary>
    private static bool IsFatalProviderException(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private bool TryEnvironmentScalar(string environmentName, ConfigLogicalPath path, Type type,
        List<ConfigAuditDiagnostic> diagnostics, out object? value, out string? candidateName)
    {
        foreach (var candidate in ConfigEnvironmentCandidates.GetPathCandidates(environmentName, path.Segments))
        {
            var raw = environment.GetEnvironmentVariable(candidate);
            if (raw is null) continue;
            if (ConfigValueConverter.TryConvert(raw, type, out value) && value is not null)
            { candidateName = candidate; return true; }
            diagnostics.Add(ConversionDiagnostic(path.Canonical, candidate));
        }
        value = null;
        candidateName = null;
        return false;
    }

    private static bool TryScalar(JsonNode node, Type type, out object? value)
    {
        value = null;
        if (node is not JsonValue scalar) return false;
        var text = scalar.TryGetValue<string>(out var s) ? s : scalar.ToJsonString();
        // Date/URI/time types use JSON string input in the existing converter; ordinary strings stay unquoted.
        if (type != typeof(string) && !type.IsPrimitive && !type.IsEnum && type != typeof(decimal) && type != typeof(Guid)
            && scalar.TryGetValue<string>(out _) && Nullable.GetUnderlyingType(type) is null)
            text = scalar.ToJsonString();
        return ConfigValueConverter.TryConvert(text, type, out value) && value is not null;
    }

    private ConfigAuditSourceRecord EnvironmentSource(string candidate, string path) => new()
    {
        Kind = ConfigAuditSourceKind.EnvironmentVariable,
        ProviderName = environment.Name,
        EnvironmentVariableName = candidate,
        ConfigPath = path,
        AppliedToPath = path,
        Role = ConfigAuditSourceRole.Override,
        Sensitivity = ConfigAuditSensitivity.Sensitive
    };

    private ConfigAuditDiagnostic ConversionDiagnostic(string path, string candidate) => new()
    {
        Severity = ConfigAuditDiagnosticSeverity.Warning,
        Code = "config-environment-conversion-failed",
        Key = path,
        ConfigPath = path,
        Message = "An environment candidate could not be converted; trying the next candidate.",
        Source = EnvironmentSource(candidate, path)
    };
}

/// <summary>Mutable JSON operations confined to the current invocation; never operate on a cached snapshot.</summary>
internal static class ConfigCompositionTree
{
    /// <summary>Finds a relative path case-insensitively while preserving explicit null presence.</summary>
    internal static bool TryGet(JsonObject root, IReadOnlyList<string> path, out JsonNode? node)
    {
        node = root;
        foreach (var segment in path)
        {
            if (node is not JsonObject obj) return false;
            var matches = obj.Where(p => string.Equals(p.Key, segment, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1) { node = null; return false; }
            node = matches[0].Value;
        }
        return true;
    }
    /// <summary>Replaces a relative path, creating object ancestors and removing case-only spellings.</summary>
    internal static void Set(JsonObject root, IReadOnlyList<string> path, JsonNode? value)
    {
        var current = root;
        for (var i = 0; i < path.Count; i++)
        {
            var existing = current.FirstOrDefault(p => string.Equals(p.Key, path[i], StringComparison.OrdinalIgnoreCase));
            if (i == path.Count - 1)
            {
                if (existing.Key is not null) current.Remove(existing.Key);
                current[path[i]] = value;
            }
            else
            {
                if (existing.Value is not JsonObject child)
                {
                    child = new JsonObject();
                    if (existing.Key is not null) current.Remove(existing.Key);
                    current[path[i]] = child;
                }
                else if (!string.Equals(existing.Key, path[i], StringComparison.Ordinal))
                {
                    current.Remove(existing.Key!);
                    current[path[i]] = child;
                }
                current = child;
            }
        }
    }
}

/// <summary>The root result classification shared by runtime and audit.</summary>
internal enum ConfigCompositionRootState { Missing, Resolved, Failed }
/// <summary>A value-free observed provider outcome.</summary>
internal sealed record ConfigSecretProviderObservation(string ProviderId, ConfigSecretProviderResolutionStatus Status, bool Retryable);
/// <summary>Opaque slot provenance. It never contains a payload, reference key or version.</summary>
internal sealed record ConfigSecretSlotTrace(string Path, bool Enabled, bool HasValue, string? ResolvedProvider,
    string? ProviderConstraint, string Code, IReadOnlyList<ConfigSecretProviderObservation> Providers,
    IReadOnlyList<ConfigAuditSourceRecord> Sources, ConfigAuditSourceRecord? DeclarationSource);
/// <summary>One invocation's result; only the explicit internal Value accessor carries the transient application value.</summary>
internal sealed class ConfigCompositionExecutionResult(ConfigCompositionRootState state, object? value,
    IReadOnlyList<ConfigCompositionFailure> failures, IReadOnlyList<ConfigSecretSlotTrace> slots,
    IReadOnlyList<ConfigAuditSourceRecord> sources, IReadOnlyList<ConfigAuditDiagnostic> diagnostics, bool directRoot)
{
    /// <summary>The root outcome.</summary>
    internal ConfigCompositionRootState State { get; } = state;
    /// <summary>The transient bound root; consumers must never format it for diagnostics.</summary>
    internal object? Value { get; } = value;
    /// <summary>Every unresolved ordered failure.</summary>
    internal IReadOnlyList<ConfigCompositionFailure> Failures { get; } = failures;
    /// <summary>The value-free slot trace.</summary>
    internal IReadOnlyList<ConfigSecretSlotTrace> Slots { get; } = slots;
    /// <summary>The observed root source contributions.</summary>
    internal IReadOnlyList<ConfigAuditSourceRecord> Sources { get; } = sources;
    /// <summary>Safe conversion warnings.</summary>
    internal IReadOnlyList<ConfigAuditDiagnostic> Diagnostics { get; } = diagnostics;
    /// <summary>Whether a direct environment root bypassed plan compilation.</summary>
    internal bool DirectRoot { get; } = directRoot;
    /// <inheritdoc />
    public override string ToString() => $"Composition: {State}";
}
