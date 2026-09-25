namespace ForgeTrust.AppSurface.Config.Testing;

/// <summary>A provider-independent source arrangement required by the logical-key contract.</summary>
/// <remarks>Fixtures create isolated sources for each case and may use explicit native mappings where required.</remarks>
public enum ConfigProviderContractScenario
{
    /// <summary>Supply Payments:ApiKey with the fixture's expected non-secret marker.</summary>
    CaseIdentity,
    /// <summary>Supply Logging:LogLevel:Microsoft.Hosting.Lifetime without splitting its dotted final segment.</summary>
    DottedSegment,
    /// <summary>Supply different markers at Payments:Api-Key and Payments:Api:Key.</summary>
    HyphenDistinction,
    /// <summary>Leave Absent:Key missing in every source.</summary>
    Missing,
    /// <summary>Return a terminal high-priority source while a lower source has a marker.</summary>
    TerminalPrecedence,
    /// <summary>Supply equivalent source spellings or aliases inside one collision domain.</summary>
    SameLayerCollision,
    /// <summary>Supply the same exact logical spelling in two ordered layers, with the upper marker winning.</summary>
    OrderedOverride,
    /// <summary>Supply distinct case spellings of one identity across ordered layers.</summary>
    OrderedCaseCollision,
    /// <summary>Provide an environment marker and a lower-provider marker for the same logical identity.</summary>
    EnvironmentPrecedence,
    /// <summary>Resolve a convention descendant and leave its text-prefix sibling unclaimed.</summary>
    PrefixBoundary,
    /// <summary>Reject a convention-native encoding that would lose identity before reading a lower source.</summary>
    Unrepresentable,
    /// <summary>Supply Payments:ApiKey and enable train-1 application string translation.</summary>
    LegacyTranslation,
    /// <summary>Supply Payments:ApiKey for simultaneous, repeatable requests.</summary>
    ConcurrentRepeatability,
    /// <summary>Leave Payments:ApiKey missing in the provider under test; a lower provider supplies the expected marker.</summary>
    MissingToLowerFallback
}

/// <summary>One immutable canonical contract case for an isolated provider fixture.</summary>
public sealed record ConfigProviderContractCase
{
    /// <summary>Creates a case with a stable identifier, arrangement, and safe logical input.</summary>
    /// <param name="id">The stable case identifier used in evidence.</param>
    /// <param name="scenario">The fixture arrangement.</param>
    /// <param name="key">The primary key; always strict logical grammar.</param>
    /// <param name="terminalCode">Expected terminal code, or null for non-terminal cases.</param>
    public ConfigProviderContractCase(string id, ConfigProviderContractScenario scenario, AppSurfaceConfigKey key,
        string? terminalCode = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(key);
        if (!Enum.IsDefined(scenario))
        {
            throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        Id = id;
        Scenario = scenario;
        Key = key;
        TerminalCode = terminalCode;
    }

    /// <summary>Gets the stable evidence identifier.</summary>
    public string Id { get; }
    /// <summary>Gets the required isolated source arrangement.</summary>
    public ConfigProviderContractScenario Scenario { get; }
    /// <summary>Gets the primary strict logical key.</summary>
    public AppSurfaceConfigKey Key { get; }
    /// <summary>Gets the expected terminal code, or null for a non-terminal case.</summary>
    public string? TerminalCode { get; }
}

/// <summary>Canonical provider cases independent of any test framework or credentialed service.</summary>
public static class ConfigProviderContractCases
{
    /// <summary>Gets immutable canonical cases. Each fixture must create fresh isolated sources per case.</summary>
    public static IReadOnlyList<ConfigProviderContractCase> All { get; } = Array.AsReadOnly(new[]
    {
        Case("case-identity", ConfigProviderContractScenario.CaseIdentity),
        Case("dotted-segment", ConfigProviderContractScenario.DottedSegment, "Logging:LogLevel:Microsoft.Hosting.Lifetime"),
        Case("hyphen-distinction", ConfigProviderContractScenario.HyphenDistinction, "Payments:Api-Key"),
        Case("missing", ConfigProviderContractScenario.Missing, "Absent:Key"),
        Case("terminal-precedence", ConfigProviderContractScenario.TerminalPrecedence, terminal: "config-provider-failed"),
        Case("same-layer-collision", ConfigProviderContractScenario.SameLayerCollision, terminal: "config-key-collision"),
        Case("ordered-override", ConfigProviderContractScenario.OrderedOverride),
        Case("ordered-case-collision", ConfigProviderContractScenario.OrderedCaseCollision, terminal: "config-key-collision"),
        Case("environment-precedence", ConfigProviderContractScenario.EnvironmentPrecedence),
        Case("prefix-boundary", ConfigProviderContractScenario.PrefixBoundary),
        Case("unrepresentable", ConfigProviderContractScenario.Unrepresentable, "A_:B", "config-key-unrepresentable"),
        Case("legacy-translation", ConfigProviderContractScenario.LegacyTranslation),
        Case("concurrent-repeatability", ConfigProviderContractScenario.ConcurrentRepeatability),
        Case("missing-to-lower-fallback", ConfigProviderContractScenario.MissingToLowerFallback)
    });

    private static ConfigProviderContractCase Case(string id, ConfigProviderContractScenario scenario,
        string key = "Payments:ApiKey", string? terminal = null) => new(id, scenario, AppSurfaceConfigKey.Parse(key), terminal);
}

/// <summary>Creates isolated provider-native sources and mappings for a canonical contract arrangement.</summary>
public interface IConfigProviderContractHarness
{
    /// <summary>Builds one case without reusing source values, claims, caches, or environment snapshots from another.</summary>
    /// <param name="contractCase">The case whose documented arrangement must be supplied.</param>
    /// <returns>A disposable public-API session; fixtures retain ownership of source cleanup.</returns>
    /// <remarks>
    /// Static collisions may throw <see cref="ConfigurationResolutionException"/> during creation with the expected
    /// code. Native options exceptions should be asserted separately by the provider-specific suite.
    /// </remarks>
    ConfigProviderContractSession Create(ConfigProviderContractCase contractCase);
}

/// <summary>Public-API resolution session for one provider fixture with value-safe assertion markers.</summary>
public sealed class ConfigProviderContractSession : IDisposable
{
    private readonly Action? _cleanup;
    private int _disposed;

    /// <summary>Creates an isolated session. Marker values remain private to assertions and never appear in failures.</summary>
    /// <param name="manager">The application's configuration manager.</param>
    /// <param name="environment">The isolated environment name.</param>
    /// <param name="expectedMarker">The non-secret marker supplied by the winning source.</param>
    /// <param name="distinctMarker">A different non-secret marker for Payments:Api:Key in hyphen cases.</param>
    /// <param name="cleanup">Optional idempotently invoked fixture cleanup.</param>
    /// <param name="unrepresentableKey">Optional provider-specific counterexample for the Unrepresentable scenario only.
    /// Null keeps that case's default environment-codec counterexample, A_:B. Google fixtures should instead use a
    /// dotted descendant of a valid convention prefix, such as Payments:Unsupported.Dot.</param>
    public ConfigProviderContractSession(IConfigManager manager, string environment, string expectedMarker,
        string distinctMarker, Action? cleanup = null, AppSurfaceConfigKey? unrepresentableKey = null)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentException.ThrowIfNullOrWhiteSpace(environment);
        ArgumentNullException.ThrowIfNull(expectedMarker);
        ArgumentNullException.ThrowIfNull(distinctMarker);
        if (StringComparer.Ordinal.Equals(expectedMarker, distinctMarker))
        {
            throw new ArgumentException("The distinct fixture marker must differ.", nameof(distinctMarker));
        }

        Manager = manager;
        Environment = environment;
        ExpectedMarker = expectedMarker;
        DistinctMarker = distinctMarker;
        UnrepresentableKey = unrepresentableKey;
        _cleanup = cleanup;
    }

    /// <summary>Gets the public application manager exercised by the conformance runner.</summary>
    public IConfigManager Manager { get; }
    /// <summary>Gets the isolated environment name.</summary>
    public string Environment { get; }
    /// <summary>Gets the fixture's optional native-codec counterexample. The runner rejects its use in other scenarios.</summary>
    public AppSurfaceConfigKey? UnrepresentableKey { get; }
    internal string ExpectedMarker { get; }
    internal string DistinctMarker { get; }

    /// <summary>Releases fixture resources exactly once, including after a contract assertion fails.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _cleanup?.Invoke();
        }
    }
}
