using AuthAspireKeycloakCandidateFixture;
using AuthAspireKeycloakLocalSeedStore;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests;

public sealed class CandidateFixtureTests
{
    [Fact]
    public void Run_WhenIdentityBootstrapOutputIsMissing_WritesASafeDiagnostic()
    {
        using var directory = new TempDirectory();
        var storePath = TestPathUtils.PathUnder(directory.Path, "local-seed-store.json");
        var environment = CreateEnvironment(storePath);
        using var standardError = new StringWriter();

        var exitCode = Program.Run(name => environment.GetValueOrDefault(name), standardError);

        Assert.Equal(1, exitCode);
        Assert.Equal(
            "candidate-fixture: identity-bootstrap output is missing or invalid; expected exactly one 'local-broker' alias and one 'founder' subject map."
                + Environment.NewLine,
            standardError.ToString());
    }

    [Fact]
    public void Run_WhenFixtureFailureIsInjected_WritesASafeDiagnostic()
    {
        using var directory = new TempDirectory();
        var storePath = TestPathUtils.PathUnder(directory.Path, "local-seed-store.json");
        var store = CreateBootstrapStore(storePath);
        var environment = CreateEnvironment(storePath);
        environment["LOCAL_SEED_INJECT_FIXTURE_FAILURE"] = "true";
        using var standardError = new StringWriter();

        var exitCode = Program.Run(name => environment.GetValueOrDefault(name), standardError);

        Assert.Equal(1, exitCode);
        Assert.Equal("candidate-fixture: fixture failure injection is enabled." + Environment.NewLine, standardError.ToString());
        Assert.Empty(store.ReadSnapshot().CandidateFixtures);
    }

    [Fact]
    public void Run_WhenBootstrapStateIsValid_ConvergesTheCandidateFixture()
    {
        using var directory = new TempDirectory();
        var storePath = TestPathUtils.PathUnder(directory.Path, "local-seed-store.json");
        var store = CreateBootstrapStore(storePath);
        var environment = CreateEnvironment(storePath);
        using var standardError = new StringWriter();

        var exitCode = Program.Run(name => environment.GetValueOrDefault(name), standardError);

        Assert.Equal(0, exitCode);
        Assert.Empty(standardError.ToString());
        Assert.Equal(
            [new CandidateFixtureRecord("candidate:founder", "subject-founder-001")],
            store.ReadSnapshot().CandidateFixtures);
    }

    [Fact]
    public void Run_WhenRequiredInputIsMissing_RedactsTheFailureDetails()
    {
        using var standardError = new StringWriter();

        var exitCode = Program.Run(_ => null, standardError);

        Assert.Equal(1, exitCode);
        Assert.Equal(
            "candidate-fixture: identity-bootstrap stage failed (InvalidDataException)." + Environment.NewLine,
            standardError.ToString());
    }

    private static LocalSeedStore CreateBootstrapStore(string storePath)
    {
        var store = new LocalSeedStore(storePath);
        store.UpsertBrokerAlias("local-broker", "https://localhost:8443/realms/appsurface-dev", "appsurface-web");
        store.UpsertIdentitySubjectMap("founder", "subject-founder-001");
        return store;
    }

    private static Dictionary<string, string?> CreateEnvironment(string storePath) =>
        new(StringComparer.Ordinal)
        {
            ["LOCAL_SEED_STORE_PATH"] = storePath,
        };
}
