using CliFx;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Config.LocalSecrets;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class SecretsKeyRecoveryCommandTests
{
    [Fact]
    public async Task RecoveryCommand_RequiresPreviewedStateAndDoctorWarnsWithoutLeakingValue()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cli-key-recovery-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "secrets.json");
        try
        {
            var store = new FileAppSurfaceLocalSecretStore(path);
            var normalizer = new AppSurfaceLocalSecretIdentityNormalizer();
            var other = normalizer.Normalize("App", "Development", null, "Other:Key").Identity!;
            var destination = normalizer.Normalize("App", "Development", null, "Payments:ApiKey").Identity!;
            Assert.Equal(LocalSecretResultStatus.Found, store.Set(other, "secret-value-sentinel").Status);
            var failed = store.MigrateKey("App", "Development", null, "legacy-missing", destination.Key);
            Assert.Equal("local-secret-migration-source-missing", failed.Diagnostic?.Code);

            var failedCommand = new SecretsMigrateKeyCommand
            {
                ApplicationName = "App",
                EnvironmentName = "Development",
                StoreFile = path,
                SourceStoredKey = "legacy-missing",
                DestinationKey = destination.Key.Value,
                Apply = true
            };
            using var failedConsole = new FakeInMemoryConsole();
            var failedException = await Assert.ThrowsAsync<CommandException>(() => failedCommand.ExecuteAsync(failedConsole).AsTask());
            Assert.Contains("local-secret-migration-source-missing", failedException.Message, StringComparison.Ordinal);
            Assert.Contains($"Migration id: '{failed.MigrationId}'", failedConsole.ReadOutputString(), StringComparison.Ordinal);
            Assert.DoesNotContain("secret-value-sentinel", failedConsole.ReadOutputString(), StringComparison.Ordinal);

            var preview = Command(path, failed.MigrationId);
            using var previewConsole = new FakeInMemoryConsole();
            await preview.ExecuteAsync(previewConsole);
            Assert.Contains("State: Prepared", previewConsole.ReadOutputString(), StringComparison.Ordinal);
            Assert.Contains("Source present: False", previewConsole.ReadOutputString(), StringComparison.Ordinal);
            Assert.DoesNotContain("secret-value-sentinel", previewConsole.ReadOutputString(), StringComparison.Ordinal);

            var apply = Command(path, failed.MigrationId);
            apply.Apply = true;
            using var applyConsole = new FakeInMemoryConsole();
            var missingState = await Assert.ThrowsAsync<CommandException>(() => apply.ExecuteAsync(applyConsole).AsTask());
            Assert.Contains("--state", missingState.Message, StringComparison.Ordinal);
            apply.ExpectedState = "Prepared";
            await apply.ExecuteAsync(applyConsole);
            Assert.Contains("Retained unresolved guard: True", applyConsole.ReadOutputString(), StringComparison.Ordinal);

            var doctor = new SecretsDoctorCommand { ApplicationName = "App", EnvironmentName = "Development", StoreFile = path };
            using var doctorConsole = new FakeInMemoryConsole();
            await doctor.ExecuteAsync(doctorConsole);
            Assert.Contains("local-secret-migration-recovery-pending", doctorConsole.ReadOutputString(), StringComparison.Ordinal);
            Assert.Contains(failed.MigrationId, doctorConsole.ReadOutputString(), StringComparison.Ordinal);
            Assert.DoesNotContain("secret-value-sentinel", doctorConsole.ReadOutputString(), StringComparison.Ordinal);

            var release = Command(path, failed.MigrationId);
            release.Release = true;
            release.Apply = true;
            release.ExpectedState = "Prepared";
            using var releaseConsole = new FakeInMemoryConsole();
            await release.ExecuteAsync(releaseConsole);
            Assert.Contains("Retained unresolved guard: False", releaseConsole.ReadOutputString(), StringComparison.Ordinal);
            Assert.Equal("secret-value-sentinel", store.Get(other).Value);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static SecretsMigrateKeyRecoverCommand Command(string path, string migrationId) => new()
    {
        ApplicationName = "App",
        EnvironmentName = "Development",
        StoreFile = path,
        MigrationId = migrationId
    };
}
