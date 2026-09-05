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
        return Run(Environment.GetEnvironmentVariable, Console.Error);
    }

    internal static int Run(Func<string, string?> getEnvironmentVariable, TextWriter standardError)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        ArgumentNullException.ThrowIfNull(standardError);

        try
        {
            var storePath = Required(getEnvironmentVariable, "LOCAL_SEED_STORE_PATH");
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
                standardError.WriteLine(
                    "candidate-fixture: identity-bootstrap output is missing or invalid; expected exactly one 'local-broker' alias and one 'founder' subject map.");
                return 1;
            }

            if (IsTrue(getEnvironmentVariable("LOCAL_SEED_INJECT_FIXTURE_FAILURE")))
            {
                standardError.WriteLine("candidate-fixture: fixture failure injection is enabled.");
                return 1;
            }

            store.UpsertCandidateFixture("candidate:founder", mappings[0].Subject);
            var finalSnapshot = store.ReadSnapshot();
            if (finalSnapshot.CandidateFixtures.Count == 1
                && finalSnapshot.CandidateFixtures[0]
                    == new CandidateFixtureRecord("candidate:founder", "subject-founder-001"))
            {
                return 0;
            }

            standardError.WriteLine(
                "candidate-fixture: fixture convergence failed; expected exactly one 'candidate:founder' record.");
            return 1;
        }
        catch (Exception exception)
        {
            standardError.WriteLine($"candidate-fixture: identity-bootstrap stage failed ({exception.GetType().Name}).");
            return 1;
        }
    }

    private static string Required(Func<string, string?> getEnvironmentVariable, string name)
    {
        var value = getEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException("A required local seed value is missing.")
            : value;
    }

    private static bool IsTrue(string? value) =>
        bool.TryParse(value, out var result) && result;
}
