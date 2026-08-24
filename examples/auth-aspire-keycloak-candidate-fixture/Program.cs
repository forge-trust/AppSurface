using AuthAspireKeycloakLocalSeedStore;

namespace AuthAspireKeycloakCandidateFixture;

/// <summary>
/// Runs one bounded candidate-fixture convergence operation.
/// </summary>
public static class Program
{
    /// <summary>
    /// Validates the identity bootstrap output and upserts the founder candidate fixture.
    /// </summary>
    /// <returns>Zero on success and a nonzero process code for every failure.</returns>
    public static int Main()
    {
        try
        {
            var storePath = Required("LOCAL_SEED_STORE_PATH");
            var store = new LocalSeedStore(storePath);
            var snapshot = store.ReadSnapshot();
            var aliases = snapshot.BrokerAliases.Where(alias => alias.Alias == "local-broker").ToList();
            var mappings = snapshot.IdentitySubjectMaps
                .Where(mapping => mapping.NaturalKey == "founder")
                .ToList();
            if (snapshot.BrokerAliases.Count != 1
                || aliases.Count != 1
                || mappings.Count != 1
                || mappings[0].Subject != "subject-founder-001")
            {
                return 1;
            }

            if (IsTrue(Environment.GetEnvironmentVariable("LOCAL_SEED_INJECT_FIXTURE_FAILURE")))
            {
                return 1;
            }

            store.UpsertCandidateFixture("candidate:founder", mappings[0].Subject);
            var finalSnapshot = store.ReadSnapshot();
            return finalSnapshot.CandidateFixtures.Count == 1
                && finalSnapshot.CandidateFixtures[0]
                    == new CandidateFixtureRecord("candidate:founder", "subject-founder-001")
                ? 0
                : 1;
        }
        catch (Exception)
        {
            return 1;
        }
    }

    private static string Required(string name) =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))
            ? throw new InvalidDataException("A required local seed value is missing.")
            : Environment.GetEnvironmentVariable(name)!;

    private static bool IsTrue(string? value) =>
        bool.TryParse(value, out var result) && result;
}
