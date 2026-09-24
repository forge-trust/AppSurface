namespace ForgeTrust.AppSurface.Config.Testing;

/// <summary>Value-safe assertion failure identifying a canonical case without exposing configuration values.</summary>
public sealed class ConfigProviderContractAssertionException : Exception
{
    /// <summary>Creates a failure with a canonical identifier and reviewed failure category.</summary>
    /// <param name="caseId">The canonical case identifier.</param>
    /// <param name="reason">A value-free failure category; never pass an exception or configuration value.</param>
    public ConfigProviderContractAssertionException(string caseId, string reason)
        : base($"Configuration provider contract case '{caseId}' failed: {reason}.")
    {
        CaseId = caseId;
    }

    /// <summary>Gets the canonical contract case identifier.</summary>
    public string CaseId { get; }
}

/// <summary>Framework-neutral checks using only the public application and provider contract.</summary>
public static class ConfigProviderContractAssert
{
    /// <summary>Runs every canonical case, returning only case identifiers after successful verification.</summary>
    /// <param name="harness">The provider-specific isolated source factory.</param>
    /// <returns>Passed case identifiers; no values or raw provider exceptions.</returns>
    /// <exception cref="ConfigProviderContractAssertionException">A canonical case fails.</exception>
    public static IReadOnlyList<string> All(IConfigProviderContractHarness harness)
    {
        ArgumentNullException.ThrowIfNull(harness);
        var passed = new List<string>();
        foreach (var contractCase in ConfigProviderContractCases.All)
        {
            Case(harness, contractCase);
            passed.Add(contractCase.Id);
        }

        return passed.AsReadOnly();
    }

    /// <summary>Runs one case without exposing actual or expected values in an assertion failure.</summary>
    /// <param name="harness">The provider-specific isolated source factory.</param>
    /// <param name="contractCase">A canonical case with its arrangement and expected outcome.</param>
    /// <exception cref="ConfigProviderContractAssertionException">Resolution violates the case contract.</exception>
    public static void Case(IConfigProviderContractHarness harness, ConfigProviderContractCase contractCase)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(contractCase);
        var key = contractCase.Key;
        try
        {
            using var session = harness.Create(contractCase);
            var manager = session.Manager;
            var environment = session.Environment;
            if (session.UnrepresentableKey is { } counterexample)
            {
                if (contractCase.Scenario != ConfigProviderContractScenario.Unrepresentable)
                {
                    Fail(contractCase, "counterexample-outside-unrepresentable-scenario");
                }
                key = counterexample;
            }
            var value = manager.GetValue<string>(environment, key);
            if (contractCase.TerminalCode is not null)
            {
                Fail(contractCase, "expected-terminal");
            }

            if (contractCase.Scenario == ConfigProviderContractScenario.Missing)
            {
                if (value is not null) { Fail(contractCase, "expected-missing"); }
                return;
            }

            Equal(contractCase, session.ExpectedMarker, value);
            Equal(contractCase, session.ExpectedMarker,
                manager.GetValue<string>(environment, AppSurfaceConfigKey.Parse(key.Value.ToLowerInvariant())));
            if (contractCase.Scenario == ConfigProviderContractScenario.HyphenDistinction)
            {
                Equal(contractCase, session.DistinctMarker,
                    manager.GetValue<string>(environment, AppSurfaceConfigKey.Parse("Payments:Api:Key")));
            }

            if (contractCase.Scenario == ConfigProviderContractScenario.PrefixBoundary
                && manager.GetValue<string>(environment, AppSurfaceConfigKey.Parse("PaymentsArchive:ApiKey")) is not null)
            {
                Fail(contractCase, "claimed-text-prefix-sibling");
            }

            if (contractCase.Scenario == ConfigProviderContractScenario.LegacyTranslation)
            {
                Equal(contractCase, session.ExpectedMarker, manager.GetValue<string>(environment, "Payments.ApiKey"));
            }

            if (contractCase.Scenario == ConfigProviderContractScenario.ConcurrentRepeatability)
            {
                var results = Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
                    manager.GetValue<string>(environment, key)))).GetAwaiter().GetResult();
                foreach (var result in results) { Equal(contractCase, session.ExpectedMarker, result); }
            }
        }
        catch (ConfigurationResolutionException exception)
        {
            if (!StringComparer.Ordinal.Equals(contractCase.TerminalCode, exception.Diagnostic.Code))
            {
                Fail(contractCase, "unexpected-terminal-code");
            }
            if (!key.Equals(exception.LogicalKey)) { Fail(contractCase, "unexpected-terminal-key"); }
        }
        catch (ConfigProviderContractAssertionException) { throw; }
        catch (Exception)
        {
            Fail(contractCase, "unexpected-provider-failure");
        }
    }

    private static void Equal(ConfigProviderContractCase contractCase, string expected, string? actual)
    {
        if (!StringComparer.Ordinal.Equals(expected, actual)) { Fail(contractCase, "value-selection-mismatch"); }
    }

    private static void Fail(ConfigProviderContractCase contractCase, string reason) =>
        throw new ConfigProviderContractAssertionException(contractCase.Id, reason);
}
