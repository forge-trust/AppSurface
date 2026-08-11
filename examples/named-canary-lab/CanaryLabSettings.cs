using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace NamedCanaryLab;

/// <summary>Local-only configuration used by the named-canary lab.</summary>
internal sealed class CanaryLabSettings
{
    /// <summary>Names the configuration section that supplies the Development-only lab settings.</summary>
    public const string SectionName = "NamedCanaryLab";
    private readonly byte[] _operatorTokenDigest;

    private CanaryLabSettings(
        byte[] operatorTokenDigest,
        CanaryProofIdentity identity,
        CanaryLabScenario scenario)
    {
        _operatorTokenDigest = operatorTokenDigest;
        Identity = identity;
        Scenario = scenario;
    }

    /// <summary>Gets the candidate and environment to which proof must be bound.</summary>
    public CanaryProofIdentity Identity { get; }

    /// <summary>Gets the deterministic demonstration outcome that the trigger records.</summary>
    public CanaryLabScenario Scenario { get; }

    /// <summary>
    /// Creates settings from the host configuration and rejects every environment except Development.
    /// </summary>
    /// <param name="configuration">Configuration containing the required <see cref="SectionName"/> values.</param>
    /// <param name="environment">Host environment that authorizes Development-only use.</param>
    /// <returns>Validated settings with a one-way digest of the operator token.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the host is not Development or a required setting is invalid.</exception>
    public static CanaryLabSettings Create(IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        return Create(configuration, environment.IsDevelopment());
    }

    /// <summary>
    /// Creates settings from configuration after a caller has determined whether the host is Development.
    /// </summary>
    /// <param name="configuration">Configuration containing the required <see cref="SectionName"/> values.</param>
    /// <param name="isDevelopment"><see langword="true"/> only for a Development host.</param>
    /// <returns>Validated settings with a one-way digest of the operator token.</returns>
    /// <exception cref="InvalidOperationException">Thrown when <paramref name="isDevelopment"/> is <see langword="false"/> or a required setting is invalid.</exception>
    public static CanaryLabSettings Create(
        IConfiguration configuration,
        bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (!isDevelopment)
        {
            throw new InvalidOperationException(
                "The named-canary lab may run only in the Development environment.");
        }

        var token = RequireValue(configuration, "OperatorToken");
        var candidate = RequireValue(configuration, "Candidate");
        var deploymentEnvironment = RequireValue(configuration, "Environment");
        var scenarioValue = RequireValue(configuration, "Scenario");
        if (!Enum.TryParse<CanaryLabScenario>(scenarioValue, ignoreCase: true, out var scenario)
            || !Enum.IsDefined(scenario))
        {
            throw new InvalidOperationException(
                "NamedCanaryLab:Scenario must be Pass, Pending, or Stale.");
        }

        return new CanaryLabSettings(
            SHA256.HashData(Encoding.UTF8.GetBytes(token)),
            new CanaryProofIdentity(candidate, deploymentEnvironment),
            scenario);
    }

    /// <summary>Compares a supplied operator token to the configured digest without exposing the configured token.</summary>
    /// <param name="candidateToken">Operator token supplied by the protected request.</param>
    /// <returns><see langword="true"/> when the supplied token matches; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="candidateToken"/> is <see langword="null"/>.</exception>
    public bool MatchesOperatorToken(string candidateToken)
    {
        ArgumentNullException.ThrowIfNull(candidateToken);
        var candidateDigest = SHA256.HashData(Encoding.UTF8.GetBytes(candidateToken));
        return CryptographicOperations.FixedTimeEquals(_operatorTokenDigest, candidateDigest);
    }

    private static string RequireValue(IConfiguration configuration, string name)
    {
        var value = configuration[$"{SectionName}:{name}"];
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"NamedCanaryLab:{name} must be configured.");
        }

        return value;
    }
}

/// <summary>Determines the locally configured demonstration behavior.</summary>
internal enum CanaryLabScenario
{
    /// <summary>Records fresh successful proof.</summary>
    Pass,

    /// <summary>Accepts the trigger without recording proof.</summary>
    Pending,

    /// <summary>Records proof older than the requested freshness boundary.</summary>
    Stale,
}
