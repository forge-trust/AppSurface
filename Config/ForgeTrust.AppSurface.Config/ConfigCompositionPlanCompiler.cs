using System.Text.Json.Nodes;

namespace ForgeTrust.AppSurface.Config;

/// <summary>A host-scoped immutable registration snapshot, shared by runtime, startup and audit.</summary>
internal sealed class ConfigSecretProviderRegistry
{
    /// <summary>The registered providers in canonical id order.</summary>
    internal IReadOnlyList<ConfigSecretRegistration> Providers { get; }
    /// <summary>Value-free registration errors detected before local reference validation or I/O.</summary>
    internal IReadOnlyList<string> Errors { get; }
    /// <summary>Captures canonical identities once. Invalid ids and nonfatal getter failures never retain provider text.</summary>
    internal ConfigSecretProviderRegistry(IEnumerable<IConfigSecretProvider> providers)
    {
        var entries = new List<ConfigSecretRegistration>();
        var errors = new List<string>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            string id;
            try
            {
                id = ConfigSecretSafety.ProviderId(provider.Id);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException
                and not StackOverflowException and not AccessViolationException)
            {
                errors.Add("secret-provider-id-invalid");
                continue;
            }
            if (!ids.Add(id)) errors.Add("secret-provider-id-duplicate");
            else entries.Add(new(id, provider));
        }
        Providers = entries.OrderBy(p => p.Id, StringComparer.Ordinal).ToList().AsReadOnly();
        Errors = errors.Distinct(StringComparer.Ordinal).ToList().AsReadOnly();
    }
}

/// <summary>Captures one trusted provider instance and its validated identity.</summary>
internal sealed record ConfigSecretRegistration(string Id, IConfigSecretProvider Provider);

/// <summary>Compiles value-free plans from host-owned immutable inputs. Payloads and invocation options are never cached.</summary>
internal sealed class ConfigCompositionPlanCompiler
{
    private readonly ConfigCompositionJsonContract _json;
    private readonly ConfigSecretProviderRegistry _registry;
    private readonly IReadOnlyList<IConfigProvider> _bases;
    private readonly IReadOnlyList<IConfigSecretDeclarationSource> _claims;
    /// <summary>Caps retained root plans; additional requests compile with identical semantics without retention.</summary>
    internal const int MaximumCachedPlans = 1024;
    private readonly Dictionary<(string Environment, string Key, Type Type), ConfigCompositionPlan> _plans = new();
    private readonly object _planCacheLock = new();

    /// <summary>Captures one host's structural contract, registration instances, mappings and file snapshot sources.</summary>
    internal ConfigCompositionPlanCompiler(ConfigCompositionJsonContract json, ConfigSecretProviderRegistry registry,
        IReadOnlyList<IConfigProvider> bases, IEnumerable<IConfigSecretDeclarationSource> claims)
    {
        _json = json;
        _registry = registry;
        _bases = bases;
        _claims = claims.Distinct().ToList().AsReadOnly();
    }

    /// <summary>Gets a cached plan. New source/registration/options generations belong to a new host compiler.</summary>
    internal ConfigCompositionPlan Compile(string environment, string key, Type type)
    {
        var identity = (environment, key, type);
        lock (_planCacheLock)
            if (_plans.TryGetValue(identity, out var cached)) return cached;
        // Provider validation can reenter composition, so callbacks must run outside the cache lock.
        var plan = Build(environment, key, type);
        lock (_planCacheLock)
        {
            if (_plans.TryGetValue(identity, out var cached)) return cached;
            if (_plans.Count < MaximumCachedPlans) _plans.Add(identity, plan);
        }
        return plan;
    }

    private ConfigCompositionPlan Build(string environment, string key, Type type)
    {
        var shape = _json.GetShape(type);
        var failures = new List<ConfigCompositionFailure>();
        ConfigLogicalPath root;
        try { root = ConfigLogicalPath.Parse(key); }
        catch (ArgumentException)
        {
            return new(environment, key, type, shape, [], [], [new(key, "secret-path-invalid")]);
        }
        foreach (var error in shape.Failures)
            failures.Add(new(Join(root, error.Members).Canonical, error.Code));
        failures.AddRange(_registry.Errors.Select(code => new ConfigCompositionFailure(root.Canonical, code)));
        var slots = shape.Secrets.Select(s => new ConfigSecretPlanSlot(s, Join(root, s.Members), null, true, null, [], null)).ToList();
        var secretPaths = slots.Select(slot => slot.Path).ToHashSet();
        var declarationHistory = new List<ConfigSecretPlanSlot>();
        if (failures.Count > 0) return Result();

        var candidates = new Dictionary<string, ConfigLogicalPath>(StringComparer.Ordinal);
        foreach (var slot in slots)
        {
            foreach (var candidate in ConfigEnvironmentCandidates.GetPathCandidates(environment, slot.Path.Segments))
            {
                if (candidates.TryGetValue(candidate, out var prior) && !prior.Equals(slot.Path))
                {
                    failures.Add(new(prior.Canonical, "secret-environment-alias-collision", relatedPath: slot.Path.Canonical, environmentVariableName: candidate));
                    failures.Add(new(slot.Path.Canonical, "secret-environment-alias-collision", relatedPath: prior.Canonical, environmentVariableName: candidate));
                }
                else candidates[candidate] = slot.Path;
            }
        }

        var eligibleBases = new List<IConfigProvider>();
        foreach (var provider in _bases)
        {
            if (provider is IConfigCompositionValueProvider) eligibleBases.Add(provider);
            else
            {
                try
                {
                    if (provider is not IConfigProviderClaimInspector inspector
                        || inspector.InspectClaim(environment, key) != ConfigProviderClaim.Unclaimed)
                        failures.Add(new(root.Canonical, "config-composition-provider-unsupported"));
                }
                catch (Exception ex) when (!IsFatalInspectionException(ex))
                {
                    failures.Add(new(root.Canonical, "config-composition-provider-unsupported"));
                }
            }
        }
        // Preserve every declaration event. Invalid lower declarations cannot disappear behind later files.
        foreach (var file in _bases.OfType<FileBasedConfigProvider>())
        {
            foreach (var fileEvent in file.Snapshot.LoadEvents.Where(e => e.Environment == "*" || string.Equals(e.Environment, environment, StringComparison.OrdinalIgnoreCase)))
            {
                if (fileEvent is ConfigFileLoadFailure failure)
                {
                    failures.Add(new(root.Canonical, "config-composition-file-invalid", source: FileSource(failure.FilePath, root, null)));
                    continue;
                }
                var layer = (ConfigFileLayer)fileEvent;
                var nodes = new Dictionary<ConfigLogicalPath, JsonNode?>();
                ReadNodes(layer.Document, null, nodes, layer);
                for (var i = 0; i < slots.Count; i++)
                {
                    var slot = slots[i];
                    if (!nodes.TryGetValue(slot.Path, out var node)) continue;
                    var source = FileSource(layer.FilePath, slot.Path, layer.SourceLocationMap.Value.GetLocation(slot.Path.Dotted));
                    if (!TryDescriptor(node, out var descriptor, out var code))
                    {
                        failures.Add(new(slot.Path.Canonical, code, source: source));
                        continue;
                    }
                    slots[i] = slot with
                    {
                        Reference = new(environment, slot.Path.Canonical, descriptor!.Key, descriptor.Version),
                        Enabled = descriptor.Enabled,
                        ProviderConstraint = descriptor.Provider,
                        Source = source
                    };
                    declarationHistory.Add(slots[i]);
                }
            }
        }
        var destinations = slots.Select(s => s.Path.Canonical).ToList().AsReadOnly();
        var mapped = new List<(ConfigSecretConfiguredClaim Claim, ConfigLogicalPath Path)>();
        foreach (var source in _claims)
        {
            try
            {
                foreach (var claim in source.InspectClaims(key, destinations))
                {
                    var path = ConfigLogicalPath.Parse(claim.LogicalPath);
                    if (!path.Overlaps(root)) continue;
                    ConfigSecretSafety.ProviderId(claim.ProviderId);
                    mapped.Add((claim, path));
                }
            }
            catch { failures.Add(new(root.Canonical, "secret-claim-overlap")); }
        }
        foreach (var (claim, path) in mapped)
        {
            var index = slots.FindIndex(s => s.Path.Equals(path));
            if (index >= 0 && claim.Kind == ConfigSecretConfiguredClaimKind.ExactMapping)
            {
                var slot = slots[index];
                if (slot.Reference is not null || mapped.Count(m => m.Path.Overlaps(path)) > 1)
                    failures.Add(new(path.Canonical, "secret-claim-overlap", claim.ProviderId));
                else slots[index] = slot with
                {
                    Reference = new(environment, path.Canonical, claim.Key, claim.Version),
                    ProviderConstraint = claim.ProviderId,
                    Source = new()
                    {
                        Kind = ConfigAuditSourceKind.Provider,
                        ProviderName = claim.ProviderId,
                        ConfigPath = path.Canonical,
                        AppliedToPath = path.Canonical,
                        Role = ConfigAuditSourceRole.Base,
                        Sensitivity = ConfigAuditSensitivity.Sensitive
                    }
                };
            }
            else if (slots.Any(s => s.Reference is not null && s.Path.Overlaps(path)) || root.IsAncestorOrEqual(path) && !root.Equals(path))
                failures.Add(new(path.Canonical, "secret-claim-overlap", claim.ProviderId));
        }
        if (failures.Count > 0) return Result(eligibleBases);
        // All structural/file/claim checks precede provider callbacks. Every local validation finishes before Resolve.
        var referencesToValidate = declarationHistory.Concat(slots.Where(slot => slot.Reference is not null
            && !declarationHistory.Any(declaration => ReferenceEquals(declaration.Reference, slot.Reference)))).ToList();
        foreach (var slot in referencesToValidate)
        {
            if (slot.Reference is null) continue;
            var registrations = slot.ProviderConstraint is null ? _registry.Providers
                : _registry.Providers.Where(p => string.Equals(p.Id, slot.ProviderConstraint, StringComparison.OrdinalIgnoreCase)).ToList();
            if (registrations.Count == 0)
            {
                failures.Add(new(slot.Path.Canonical, "secret-provider-not-registered", source: slot.Source));
                continue;
            }
            var compatible = new List<ConfigSecretRegistration>();
            foreach (var registration in registrations)
            {
                try
                {
                    var validation = registration.Provider.ValidateReference(slot.Reference);
                    if (validation.Status == ConfigSecretReferenceValidationStatus.Supported) compatible.Add(registration);
                    else if (validation.Status != ConfigSecretReferenceValidationStatus.Unclaimed)
                        failures.Add(new(slot.Path.Canonical, "secret-reference-invalid", registration.Id, source: slot.Source));
                }
                catch { failures.Add(new(slot.Path.Canonical, "secret-reference-invalid", registration.Id, source: slot.Source)); }
            }
            if (compatible.Count == 0 && !failures.Any(f => f.Path == slot.Path.Canonical))
                failures.Add(new(slot.Path.Canonical, "secret-reference-unsupported", source: slot.Source));
            var finalIndex = slots.FindIndex(finalSlot => ReferenceEquals(finalSlot.Reference, slot.Reference));
            if (finalIndex >= 0) slots[finalIndex] = slot with { Providers = compatible.AsReadOnly() };
        }
        return Result(eligibleBases);

        ConfigCompositionPlan Result(IReadOnlyList<IConfigProvider>? bases = null) => new(environment, key, type, shape,
            slots.OrderBy(s => s.Path.Canonical, StringComparer.OrdinalIgnoreCase).ToList().AsReadOnly(),
            bases ?? [], failures.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Code, StringComparer.Ordinal).ToList().AsReadOnly());

        void ReadNodes(JsonObject obj, ConfigLogicalPath? parent, Dictionary<ConfigLogicalPath, JsonNode?> nodes, ConfigFileLayer layer)
        {
            foreach (var pair in obj)
            {
                ConfigLogicalPath path;
                try { path = ConfigLogicalPath.Parse(parent is null ? pair.Key : $"{parent.Canonical}:{pair.Key}"); }
                catch (ArgumentException) { failures.Add(new(root.Canonical, "secret-path-invalid")); continue; }
                if (!path.Overlaps(root)) continue;
                if (!nodes.TryAdd(path, pair.Value)) failures.Add(new(path.Canonical, "secret-path-collision", source: FileSource(layer.FilePath, path, null)));
                if (pair.Value is JsonObject child && !secretPaths.Contains(path)) ReadNodes(child, path, nodes, layer);
            }
        }
    }

    /// <summary>Identifies process-level failures that claim inspection must not turn into plan diagnostics.</summary>
    private static bool IsFatalInspectionException(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException
            or AppDomainUnloadedException or BadImageFormatException or CannotUnloadAppDomainException
            or InvalidProgramException or ThreadAbortException;

    /// <summary>Appends serialized member segments to the requested root.</summary>
    internal static ConfigLogicalPath Join(ConfigLogicalPath root, IReadOnlyList<string> members)
    {
        foreach (var member in members) root = root.Append(member);
        return root;
    }

    private static ConfigAuditSourceRecord FileSource(string file, ConfigLogicalPath path, ConfigAuditSourceLocation? location) => new()
    {
        Kind = ConfigAuditSourceKind.File,
        ProviderName = nameof(FileBasedConfigProvider),
        ProviderPriority = 1,
        FilePath = file,
        ConfigPath = path.Canonical,
        AppliedToPath = path.Canonical,
        Location = location,
        Role = ConfigAuditSourceRole.Base,
        Sensitivity = ConfigAuditSensitivity.Sensitive
    };

    private static bool TryDescriptor(JsonNode? node, out Descriptor? descriptor, out string code)
    {
        descriptor = null;
        code = "secret-descriptor-invalid";
        if (node is not JsonObject obj) return false;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? key = null, version = null, provider = null;
        var enabled = true;
        foreach (var pair in obj)
        {
            if (!names.Add(pair.Key)) return false;
            switch (pair.Key.ToLowerInvariant())
            {
                case "key":
                    if (pair.Value is not JsonValue k || !k.TryGetValue(out key)) return false;
                    break;
                case "version":
                    if (pair.Value is not JsonValue v || !v.TryGetValue(out version) || string.IsNullOrWhiteSpace(version)) return false;
                    break;
                case "provider":
                    if (pair.Value is not JsonValue p || !p.TryGetValue(out provider) || string.IsNullOrWhiteSpace(provider)) return false;
                    break;
                case "enabled":
                    if (pair.Value is not JsonValue e || !e.TryGetValue(out enabled)) return false;
                    break;
                default: return false;
            }
        }
        if (string.IsNullOrWhiteSpace(key)) { code = "secret-descriptor-incomplete"; return false; }
        descriptor = new(key, version, provider, enabled);
        return true;
    }

    /// <summary>A parsed policy declaration. It is never used as an effective value.</summary>
    private sealed record Descriptor(string Key, string? Version, string? Provider, bool Enabled)
    {
        /// <inheritdoc />
        public override string ToString() => "Secret declaration";
    }
}

/// <summary>An immutable value-free execution plan scoped to one host and source generation.</summary>
internal sealed record ConfigCompositionPlan(string Environment, string Key, Type ValueType, ConfigCompositionShape Shape,
    IReadOnlyList<ConfigSecretPlanSlot> Slots, IReadOnlyList<IConfigProvider> Bases, IReadOnlyList<ConfigCompositionFailure> Failures);
/// <summary>One compiled scalar destination and its locally validated reference policy.</summary>
internal sealed record ConfigSecretPlanSlot(ConfigSecretDestination Destination, ConfigLogicalPath Path,
    ConfigSecretReference? Reference, bool Enabled, string? ProviderConstraint, IReadOnlyList<ConfigSecretRegistration> Providers,
    ConfigAuditSourceRecord? Source);
