using ForgeTrust.AppSurface.Config;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Core.Defaults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ConfigPackageConsumer;

/// <summary>Executable public-package proof for metadata inspection and caller-owned policy.</summary>
internal static class EnvironmentCandidateInspectionProof
{
    internal static object Run()
    {
        var activity = new SourceActivityCounters();
        var environment = new CountingEnvironmentProvider();
        var provider = new CountingProvider();
        Action<AppSurfaceEnvironmentConfigOptions> configureMappings = options =>
            options.MapKey("Payments:ApiKey", "Approved_ApiKey")
                .MapKey("Feature:Endpoint", "Feature_Endpoint")
                .MapKey("Service", "Root_Config")
                .MapKey("Service:ApiKey", "Service_ApiKey");
        using var host = CreateHost(environment, activity, provider, configureMappings, out var approvedProviderRegistered);
        var inspector = host.Services.GetRequiredService<IEnvironmentConfigCandidateInspector>();

        var typed = inspector.InspectNativeKey("Production", AppSurfaceConfigKey.Parse("Payments:ApiKey"));
        var unmappedTyped = inspector.InspectNativeKey("Production", AppSurfaceConfigKey.Parse("Service:Api-Key"));
        var strict = inspector.InspectNativeKey("Production", "Payments.ApiKey");
        var composition = inspector.InspectCompositionPath("Production", AppSurfaceConfigKey.Parse("Payments:ApiKey"));
        Equal(typed.Candidates.Select(c => (c.Name, c.Scope, c.IsLegacyAlias, c.IsExplicitMapping)),
            [("PRODUCTION__Approved_ApiKey", EnvironmentCandidateScope.Scoped, false, true),
             ("Approved_ApiKey", EnvironmentCandidateScope.Unscoped, false, true)], "typed-mapped");
        Equal(unmappedTyped.Candidates.Select(c => (c.Name, c.Scope, c.IsLegacyAlias, c.IsExplicitMapping)),
            [("PRODUCTION__SERVICE__API-KEY", EnvironmentCandidateScope.Scoped, false, false),
             ("SERVICE__API-KEY", EnvironmentCandidateScope.Unscoped, false, false)], "typed-unmapped");
        Equal(strict.Candidates.Select(c => (c.Name, c.Scope, c.IsLegacyAlias, c.IsExplicitMapping)),
            [("PRODUCTION__PAYMENTS.APIKEY", EnvironmentCandidateScope.Scoped, false, false),
             ("PAYMENTS.APIKEY", EnvironmentCandidateScope.Unscoped, false, false),
             ("PRODUCTION_PAYMENTS_APIKEY", EnvironmentCandidateScope.Scoped, true, false),
             ("PRODUCTION__PAYMENTS_APIKEY", EnvironmentCandidateScope.Scoped, true, false),
             ("PAYMENTS_APIKEY", EnvironmentCandidateScope.Unscoped, true, false)], "strict-literal-aliases");
        Equal(composition.Candidates.Select(c => (c.Name, c.Scope, c.IsHierarchical)),
            [("PRODUCTION_PAYMENTS_APIKEY", EnvironmentCandidateScope.Scoped, false),
             ("PAYMENTS_APIKEY", EnvironmentCandidateScope.Unscoped, false),
             ("PRODUCTION__PAYMENTS__APIKEY", EnvironmentCandidateScope.Scoped, true),
             ("PAYMENTS__APIKEY", EnvironmentCandidateScope.Unscoped, true)], "direct-composition");

        // Constructor/with copies and generated deconstruction are public package behavior.
        var mutable = new List<EnvironmentNativeCandidate>(typed.Candidates);
        var copied = new EnvironmentNativeKeyPlan(typed.Key, mutable);
        mutable.Clear();
        var replacement = new List<EnvironmentNativeCandidate>(typed.Candidates);
        var changed = copied with { Candidates = replacement };
        replacement.Clear();
        var plainCopy = copied with { };
        var (deconstructedKey, deconstructedCandidates) = plainCopy;
        if (copied.Candidates.Count != 2 || changed.Candidates.Count != 2
            || deconstructedCandidates.Count != 2 || !deconstructedKey.Equals(typed.Key))
            throw new InvalidOperationException("candidate-plan-copy-contract-failed");
        var compositionMutable = new List<EnvironmentCompositionPathCandidate>(composition.Candidates);
        var compositionCopy = new EnvironmentCompositionPathPlan(composition.Path, compositionMutable);
        compositionMutable.Clear();
        var compositionReplacement = new List<EnvironmentCompositionPathCandidate>(composition.Candidates);
        var compositionChanged = compositionCopy with { Candidates = compositionReplacement };
        compositionReplacement.Clear();
        var compositionPlainCopy = compositionCopy with { };
        var (deconstructedPath, deconstructedPathCandidates) = compositionPlainCopy;
        if (compositionCopy.Candidates.Count != 4 || compositionChanged.Candidates.Count != 4
            || deconstructedPathCandidates.Count != 4 || !deconstructedPath.Equals(composition.Path))
            throw new InvalidOperationException("composition-plan-copy-contract-failed");
        var nullCounters = Capture<ArgumentNullException>(() => new EnvironmentNativeKeyPlan(null!, []));
        var nullNativeList = Capture<ArgumentNullException>(() => new EnvironmentNativeKeyPlan(AppSurfaceConfigKey.Parse("Path"), null!));
        var nullList = Capture<ArgumentNullException>(() => new EnvironmentCompositionPathPlan(AppSurfaceConfigKey.Parse("Path"), null!));
        var nullElement = Capture<ArgumentException>(() => new EnvironmentNativeKeyPlan(AppSurfaceConfigKey.Parse("Path"), [null!]));
        var nullCompositionElement = Capture<ArgumentException>(() => new EnvironmentCompositionPathPlan(
            AppSurfaceConfigKey.Parse("Path"), [null!]));
        AssertDiagnostic(nullCounters, "config-candidate-null", "#config-candidate-null", typeof(ArgumentNullException));
        AssertDiagnostic(nullNativeList, "config-candidate-null", "#config-candidate-null", typeof(ArgumentNullException));
        AssertDiagnostic(nullList, "config-candidate-null", "#config-candidate-null", typeof(ArgumentNullException));
        AssertDiagnostic(nullElement, "config-candidate-null", "#config-candidate-null", typeof(ArgumentException));
        AssertDiagnostic(nullCompositionElement, "config-candidate-null", "#config-candidate-null", typeof(ArgumentException));
        var nullNativeWithKey = Capture<ArgumentNullException>(() => _ = copied with { Key = null! });
        var nullCompositionWithPath = Capture<ArgumentNullException>(() => _ = compositionCopy with { Path = null! });
        AssertDiagnostic(nullNativeWithKey, "config-candidate-null", "#config-candidate-null", typeof(ArgumentNullException));
        AssertDiagnostic(nullCompositionWithPath, "config-candidate-null", "#config-candidate-null", typeof(ArgumentNullException));

        // The caller rejects a forbidden override when any planned name appears in its environment inventory.
        var filePlan = inspector.InspectNativeKey("Production", AppSurfaceConfigKey.Parse("Feature:Endpoint"));
        var noFileOverride = new HashSet<string>(StringComparer.Ordinal);
        var fileOverrideNames = new HashSet<string>(["PRODUCTION__Feature_Endpoint", "Feature_Endpoint"], StringComparer.OrdinalIgnoreCase);
        var fileOverrideRejected = HasEnvironmentCandidate(filePlan.Candidates, fileOverrideNames);
        var fileOrdinalMissesMixedCase = !HasEnvironmentCandidate(filePlan.Candidates,
            new HashSet<string>(["production__feature_endpoint"], StringComparer.Ordinal));
        var fileIgnoreCaseRejectsMixedCase = HasEnvironmentCandidate(filePlan.Candidates,
            new HashSet<string>(["production__feature_endpoint"], StringComparer.OrdinalIgnoreCase));
        var fileAllowedWithoutOverride = !HasEnvironmentCandidate(filePlan.Candidates, noFileOverride);

        var providerOverrideNames = new HashSet<string>(["PRODUCTION__Service_ApiKey"], StringComparer.OrdinalIgnoreCase);
        var providerPlan = inspector.InspectNativeKey("Production", AppSurfaceConfigKey.Parse("Service:ApiKey"));
        var approvedProviderConfigured = approvedProviderRegistered;
        var providerOverrideRejected = HasEnvironmentCandidate(providerPlan.Candidates, providerOverrideNames);
        var providerOrdinalMissesMixedCase = !HasEnvironmentCandidate(providerPlan.Candidates,
            new HashSet<string>(["production__service_apikey"], StringComparer.Ordinal));
        var providerIgnoreCaseRejectsMixedCase = HasEnvironmentCandidate(providerPlan.Candidates,
            new HashSet<string>(["production__service_apikey"], StringComparer.OrdinalIgnoreCase));
        var providerAllowedWithoutOverride = approvedProviderConfigured
            && !HasEnvironmentCandidate(providerPlan.Candidates, noFileOverride);

        // This container maps only the Service ancestor. The descendant native plan is independent.
        var ancestorActivity = new SourceActivityCounters();
        var ancestorEnvironment = new CountingEnvironmentProvider();
        var ancestorProvider = new CountingProvider();
        using var ancestorHost = CreateHost(ancestorEnvironment, ancestorActivity, ancestorProvider,
            options => options.MapKey("Service", "Root_Config"), out _);
        var ancestorInspector = ancestorHost.Services.GetRequiredService<IEnvironmentConfigCandidateInspector>();
        var ancestor = ancestorInspector.InspectNativeKey("Production", AppSurfaceConfigKey.Parse("Service"));
        var childWithoutAncestor = ancestorInspector.InspectNativeKey("Production", AppSurfaceConfigKey.Parse("Service:ApiKey"));
        var omittedAncestorMisses = !childWithoutAncestor.Candidates.Any(c => c.Name == "PRODUCTION__Root_Config");
        var restoredAncestorRejects = HasEnvironmentCandidate(ancestor.Candidates,
            new HashSet<string>(["PRODUCTION__Root_Config"], StringComparer.Ordinal));
        if (!fileOverrideRejected || !fileOrdinalMissesMixedCase || !fileIgnoreCaseRejectsMixedCase || !fileAllowedWithoutOverride
            || !providerOverrideRejected || !providerOrdinalMissesMixedCase || !providerIgnoreCaseRejectsMixedCase
            || !providerAllowedWithoutOverride || provider.ResolveCalls != 0
            || ancestorEnvironment.Reads != 0 || ancestorActivity.Total != 0 || ancestorProvider.ResolveCalls != 0
            || !omittedAncestorMisses || !restoredAncestorRejects)
            throw new InvalidOperationException("candidate-caller-policy-controls-failed");

        // Mutating the original options after first inspection cannot refresh the frozen mapping snapshot.
        host.Services.GetRequiredService<IOptions<AppSurfaceEnvironmentConfigOptions>>().Value
            .MapKey("Late:Key", "Late_Suffix");
        var latePlan = inspector.InspectNativeKey("Production", AppSurfaceConfigKey.Parse("Late:Key"));
        if (latePlan.Candidates.Any(c => c.IsExplicitMapping)
            || latePlan.Candidates[0].Name != "PRODUCTION__LATE__KEY")
            throw new InvalidOperationException("candidate-late-options-mutation-changed-snapshot");

        // protects=rebuilt host captures corrected mappings; fails_when=new host remains stale; no_claim=refreshing an existing container.
        var rebuiltActivity = new SourceActivityCounters();
        var rebuiltEnvironment = new CountingEnvironmentProvider();
        var rebuiltProvider = new CountingProvider();
        using var rebuiltHost = CreateHost(rebuiltEnvironment, rebuiltActivity, rebuiltProvider, options =>
        {
            configureMappings(options);
            options.MapKey("Late:Key", "Late_Suffix");
        }, out _);
        var rebuiltInspector = rebuiltHost.Services.GetRequiredService<IEnvironmentConfigCandidateInspector>();
        var rebuiltPlan = rebuiltInspector.InspectNativeKey("Production", AppSurfaceConfigKey.Parse("Late:Key"));
        Equal(rebuiltPlan.Candidates.Select(c => (c.Name, c.Scope, c.IsLegacyAlias, c.IsExplicitMapping)),
            [("PRODUCTION__Late_Suffix", EnvironmentCandidateScope.Scoped, false, true),
             ("Late_Suffix", EnvironmentCandidateScope.Unscoped, false, true)], "rebuilt-mapping");
        Equal(inspector.InspectNativeKey("Production", AppSurfaceConfigKey.Parse("Late:Key")).Candidates,
            latePlan.Candidates, "original-snapshot-after-rebuild");
        var rebuildRequired = rebuiltPlan.Candidates.All(candidate => candidate.IsExplicitMapping)
            && latePlan.Candidates.All(candidate => !candidate.IsExplicitMapping);

        var syntax = Capture<FormatException>(() => inspector.InspectNativeKey("Production", "Payments::ApiKey"));
        var unencodable = Capture<InvalidOperationException>(() => inspector.InspectNativeKey("Production", AppSurfaceConfigKey.Parse("Secret:_sentinel-secret")));
        AssertDiagnostic(syntax, "config-key-invalid", "#config-key-invalid", typeof(FormatException));
        AssertDiagnostic(unencodable, "config-candidate-unencodable", "#config-candidate-unencodable", typeof(InvalidOperationException));
        var invalidOptionsEnvironment = new CountingEnvironmentProvider();
        var invalidOptionsActivity = new SourceActivityCounters();
        var invalidOptionsProvider = new CountingProvider();
        using var invalidOptionsHost = CreateHost(invalidOptionsEnvironment, invalidOptionsActivity, invalidOptionsProvider,
            options => options.MaxAdHocClaims = 0, out _);
        var invalidOptions = Capture<OptionsValidationException>(() => invalidOptionsHost.Services.GetRequiredService<IEnvironmentConfigCandidateInspector>()
            .InspectNativeKey("Production", AppSurfaceConfigKey.Parse("Payments:ApiKey")));
        AssertDiagnostic(invalidOptions, "config-candidate-options-invalid", "#config-candidate-options-invalid",
            typeof(OptionsValidationException));
        if (environment.Reads != 0 || activity.Total != 0 || provider.ResolveCalls != 0
            || rebuiltEnvironment.Reads != 0 || rebuiltActivity.Total != 0 || rebuiltProvider.ResolveCalls != 0
            || invalidOptionsEnvironment.Reads != 0 || invalidOptionsActivity.Total != 0 || invalidOptionsProvider.ResolveCalls != 0)
            throw new InvalidOperationException("candidate-inspection-performed-source-io");

        return new { typed = typed.Candidates.Select(c => new { c.Name, scope = c.Scope.ToString(), c.IsLegacyAlias, c.IsExplicitMapping }),
            unmappedTyped = unmappedTyped.Candidates.Select(c => new { c.Name, scope = c.Scope.ToString(), c.IsLegacyAlias, c.IsExplicitMapping }),
            strict = strict.Candidates.Select(c => new { c.Name, scope = c.Scope.ToString(), c.IsLegacyAlias, c.IsExplicitMapping }),
            composition = composition.Candidates.Select(c => new { c.Name, scope = c.Scope.ToString(), c.IsHierarchical }),
            errors = new { syntax = "config-key-invalid", unencodable = "config-candidate-unencodable",
                invalidOptions = "config-candidate-options-invalid", originalTypesPreserved = true,
                nullDtos = "config-candidate-null", nullElements = "config-candidate-null",
                sixFieldsAndHelpLink = true, safeFixes = true },
            lifecycle = new { lateOptionsMutationIgnored = true, rebuildRequired },
            callerPolicy = new { fileAllowedWithoutOverride, fileOverrideRejected, fileOrdinalMissesMixedCase,
                fileIgnoreCaseRejectsMixedCase, approvedProviderConfigured, providerAllowedWithoutOverride,
                providerOverrideRejected, providerOrdinalMissesMixedCase, providerIgnoreCaseRejectsMixedCase,
                omittedAncestorMisses, restoredAncestorRejects }, environmentReads = environment.Reads,
            sourceFactories = activity.ToEvidence(), invalidOptionsEnvironmentReads = invalidOptionsEnvironment.Reads,
            providerResolveCalls = provider.ResolveCalls, invalidOptionsProviderResolveCalls = invalidOptionsProvider.ResolveCalls,
            ancestorSourceFactories = ancestorActivity.ToEvidence(), invalidOptionsSourceFactories = invalidOptionsActivity.ToEvidence(),
            rebuiltSourceFactories = rebuiltActivity.ToEvidence(), rebuiltEnvironmentReads = rebuiltEnvironment.Reads,
            rebuiltProviderResolveCalls = rebuiltProvider.ResolveCalls };
    }

    private static IHost CreateHost(CountingEnvironmentProvider environment, SourceActivityCounters activity,
        CountingProvider provider, Action<AppSurfaceEnvironmentConfigOptions> configure, out bool approvedProviderRegistered)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IEnvironmentProvider>(_ => { activity.EnvironmentFactoryCalls++; return environment; });
        builder.Services.Configure(configure);
        var context = new StartupContext([], new NoHostModule(), EnvironmentProvider: environment)
        { OverrideEntryPointAssembly = typeof(NoHostModule).Assembly };
        new AppSurfaceConfigModule().ConfigureServices(context, builder.Services);
        foreach (var registration in context.CustomRegistrations) registration(builder.Services);
        builder.Services.AddSingleton<IConfigProvider>(provider);
        builder.Services.AddSingleton<IConfigFileLocationProvider>(_ => activity.CreateFileService());
        builder.Services.AddSingleton<IConfigSecretProvider>(_ => activity.CreateSecretService());
        builder.Services.AddSingleton<IConfigProvider>(_ => activity.CreateProviderService());
        approvedProviderRegistered = builder.Services.Any(descriptor => descriptor.ServiceType == typeof(IConfigProvider)
            && ReferenceEquals(descriptor.ImplementationInstance, provider));
        return builder.Build();
    }

    private static bool HasEnvironmentCandidate(IEnumerable<EnvironmentNativeCandidate> candidates,
        HashSet<string> environmentNames) => candidates.Any(candidate => environmentNames.Contains(candidate.Name));

    private static TException Capture<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException exception) { return exception; }
        throw new InvalidOperationException("candidate-expected-error-not-thrown");
    }

    private static void AssertDiagnostic(Exception failure, string code, string anchor, Type expectedType)
    {
        if (failure.GetType() != expectedType || failure.Data["Code"] as string != code || failure.Data["Operation"] is not string
            || failure.Data["Fix"] is not string || failure.Data["Recovery"] is not string
            || failure.Data["SideEffects"] as string != "none" || failure.Data["HelpUri"] is not string uri
            || !uri.EndsWith(anchor, StringComparison.Ordinal) || failure.HelpLink != uri
            || failure.Data.Count < 6 || failure.ToString().Contains("sentinel-secret", StringComparison.Ordinal)
            || string.Join(" ", failure.Data.Values.Cast<object?>()).Contains("sentinel-secret", StringComparison.Ordinal))
            throw new InvalidOperationException("candidate-error-metadata-contract-failed");
    }

    private static void Equal<T>(IEnumerable<T> actual, IEnumerable<T> expected, string name)
    {
        if (!actual.SequenceEqual(expected)) throw new InvalidOperationException($"candidate-plan-{name}-failed");
    }

    private sealed class CountingEnvironmentProvider : IEnvironmentProvider
    {
        public int Reads { get; private set; }
        public string Environment => "Production";
        public bool IsDevelopment => false;
        public string? GetEnvironmentVariable(string name, string? defaultValue = null) { Reads++; return defaultValue; }
        public IReadOnlyDictionary<string, string> CaptureEnvironmentVariables() { Reads++; return new Dictionary<string, string>(); }
    }

    private sealed class CountingProvider : IConfigProvider
    {
        public int Priority => 5;
        public string Name => "ApprovedProviderFixture";
        public int ResolveCalls { get; private set; }
        public ConfigProviderValueResult<T> Resolve<T>(ConfigProviderRequest request)
        {
            ResolveCalls++;
            return ConfigProviderValueResult<T>.Missing();
        }
    }

    private sealed class SourceActivityCounters
    {
        public int EnvironmentFactoryCalls { get; set; }
        public int FileFactoryCalls { get; set; }
        public int SecretFactoryCalls { get; set; }
        public int ProviderFactoryCalls { get; set; }
        public int Total => EnvironmentFactoryCalls + FileFactoryCalls + SecretFactoryCalls + ProviderFactoryCalls;
        public IConfigFileLocationProvider CreateFileService() { FileFactoryCalls++; throw new InvalidOperationException("file-service-activation-forbidden"); }
        public IConfigSecretProvider CreateSecretService() { SecretFactoryCalls++; throw new InvalidOperationException("secret-service-activation-forbidden"); }
        public IConfigProvider CreateProviderService() { ProviderFactoryCalls++; throw new InvalidOperationException("provider-service-activation-forbidden"); }
        public object ToEvidence() => new { environment = EnvironmentFactoryCalls, file = FileFactoryCalls, secret = SecretFactoryCalls, provider = ProviderFactoryCalls };
    }
}
