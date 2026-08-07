using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace NamedCanaryLab;

/// <summary>Local-only configuration used by the named-canary lab.</summary>
internal sealed class CanaryLabSettings
{
    public const string SectionName = "NamedCanaryLab";

    private CanaryLabSettings(
        byte[] operatorTokenDigest,
        CanaryProofIdentity identity,
        CanaryLabScenario scenario)
    {
        OperatorTokenDigest = operatorTokenDigest;
        Identity = identity;
        Scenario = scenario;
    }

    public byte[] OperatorTokenDigest { get; }

    public CanaryProofIdentity Identity { get; }

    public CanaryLabScenario Scenario { get; }

    public static CanaryLabSettings Create(IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        return Create(configuration, environment.IsDevelopment());
    }

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

    public bool MatchesOperatorToken(string candidateToken)
    {
        ArgumentNullException.ThrowIfNull(candidateToken);
        var candidateDigest = SHA256.HashData(Encoding.UTF8.GetBytes(candidateToken));
        return CryptographicOperations.FixedTimeEquals(OperatorTokenDigest, candidateDigest);
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
    Pass,
    Pending,
    Stale,
}
