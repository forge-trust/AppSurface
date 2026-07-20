using System.Text.Json;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

[Collection("PostgreSQL reference evidence")]
public sealed class ReferenceWorkloadEvidenceTests
{
    [Fact]
    public async Task WriteAsync_IsDisabledWithoutDirectory()
    {
        var original = Environment.GetEnvironmentVariable(ReferenceWorkloadEvidence.DirectoryEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(ReferenceWorkloadEvidence.DirectoryEnvironmentVariable, null);
            var evidence = new ReferenceWorkloadEvidence("caller-owned-transaction");

            Assert.Null(await evidence.WriteAsync("succeeded"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ReferenceWorkloadEvidence.DirectoryEnvironmentVariable, original);
        }
    }

    [Fact]
    public async Task WriteAsync_RecordsOnlyAllowlistedOperationalEvidence()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"appsurface-reference-evidence-{Guid.NewGuid():N}");
        var originalDirectory = Environment.GetEnvironmentVariable(ReferenceWorkloadEvidence.DirectoryEnvironmentVariable);
        var originalMode = Environment.GetEnvironmentVariable(ReferenceWorkloadEvidence.ModeEnvironmentVariable);
        var originalRunId = Environment.GetEnvironmentVariable(ReferenceWorkloadEvidence.RunIdEnvironmentVariable);
        var originalConnection = Environment.GetEnvironmentVariable("APPSURFACE_POSTGRES_TEST_CONNECTION");
        try
        {
            Environment.SetEnvironmentVariable(ReferenceWorkloadEvidence.DirectoryEnvironmentVariable, directory);
            Environment.SetEnvironmentVariable(ReferenceWorkloadEvidence.ModeEnvironmentVariable, "warm");
            Environment.SetEnvironmentVariable(ReferenceWorkloadEvidence.RunIdEnvironmentVariable, "20260720T120000Z-123");
            Environment.SetEnvironmentVariable(
                "APPSURFACE_POSTGRES_TEST_CONNECTION",
                "Host=secret-host;Password=do-not-record");
            var evidence = new ReferenceWorkloadEvidence("caller-owned-transaction");
            evidence.Record("transaction", "work.accept", "committed", "caller-owned");

            var path = await evidence.WriteAsync("succeeded");
            var json = await File.ReadAllTextAsync(path!);
            var document = JsonSerializer.Deserialize<ReferenceWorkloadEvidenceDocument>(json);

            Assert.NotNull(document);
            Assert.Equal("20260720T120000Z-123", document.RunId);
            Assert.Equal("warm", document.Mode);
            Assert.Equal("external-postgresql-17.5", document.DatabaseSource);
            Assert.Equal("work.accept", Assert.Single(document.Events).Operation);
            Assert.DoesNotContain("secret-host", json, StringComparison.Ordinal);
            Assert.DoesNotContain("do-not-record", json, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ReferenceWorkloadEvidence.DirectoryEnvironmentVariable, originalDirectory);
            Environment.SetEnvironmentVariable(ReferenceWorkloadEvidence.ModeEnvironmentVariable, originalMode);
            Environment.SetEnvironmentVariable(ReferenceWorkloadEvidence.RunIdEnvironmentVariable, originalRunId);
            Environment.SetEnvironmentVariable("APPSURFACE_POSTGRES_TEST_CONNECTION", originalConnection);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Evidence_RejectsValuesOutsideThePrivacyAllowlist()
    {
        Assert.ThrowsAny<ArgumentException>(() => new ReferenceWorkloadEvidence("customer-123"));
        var evidence = new ReferenceWorkloadEvidence("caller-owned-transaction");
        Assert.Throws<ArgumentException>(() => evidence.Record("actor-id", "work.accept", "committed", "caller-owned"));
        Assert.Throws<ArgumentException>(() => evidence.Record("application", "payload.contents", "committed", "caller-owned"));
        Assert.Throws<ArgumentException>(() => evidence.Record("application", "work.accept", "provider-response", "caller-owned"));
        Assert.Throws<ArgumentException>(() => evidence.Record("application", "work.accept", "committed", "connection-string"));

        var originalDirectory = Environment.GetEnvironmentVariable(ReferenceWorkloadEvidence.DirectoryEnvironmentVariable);
        var originalMode = Environment.GetEnvironmentVariable(ReferenceWorkloadEvidence.ModeEnvironmentVariable);
        var originalRunId = Environment.GetEnvironmentVariable(ReferenceWorkloadEvidence.RunIdEnvironmentVariable);
        var directory = Path.Combine(Path.GetTempPath(), $"appsurface-reference-evidence-invalid-{Guid.NewGuid():N}");
        try
        {
            Environment.SetEnvironmentVariable(ReferenceWorkloadEvidence.DirectoryEnvironmentVariable, directory);
            Environment.SetEnvironmentVariable(ReferenceWorkloadEvidence.ModeEnvironmentVariable, "customer-mode");
            Environment.SetEnvironmentVariable(ReferenceWorkloadEvidence.RunIdEnvironmentVariable, "20260720T120000Z-123");
            await Assert.ThrowsAsync<ArgumentException>(async () => await evidence.WriteAsync("succeeded"));

            Environment.SetEnvironmentVariable(ReferenceWorkloadEvidence.ModeEnvironmentVariable, "warm");
            Environment.SetEnvironmentVariable(ReferenceWorkloadEvidence.RunIdEnvironmentVariable, null);
            await Assert.ThrowsAnyAsync<ArgumentException>(async () => await evidence.WriteAsync("succeeded"));

            Environment.SetEnvironmentVariable(ReferenceWorkloadEvidence.RunIdEnvironmentVariable, "customer-secret");
            await Assert.ThrowsAsync<ArgumentException>(async () => await evidence.WriteAsync("succeeded"));

            Environment.SetEnvironmentVariable(ReferenceWorkloadEvidence.RunIdEnvironmentVariable, "20260720T120000Z-123");
            await Assert.ThrowsAsync<ArgumentException>(async () => await evidence.WriteAsync("payload-value"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ReferenceWorkloadEvidence.DirectoryEnvironmentVariable, originalDirectory);
            Environment.SetEnvironmentVariable(ReferenceWorkloadEvidence.ModeEnvironmentVariable, originalMode);
            Environment.SetEnvironmentVariable(ReferenceWorkloadEvidence.RunIdEnvironmentVariable, originalRunId);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void PostgreSqlImage_IsPinnedToAnImmutableMultiPlatformDigest()
    {
        Assert.Matches(
            "^postgres:17\\.5@sha256:[0-9a-f]{64}$",
            PostgreSqlTestContainerImage.Reference);
    }
}

[CollectionDefinition("PostgreSQL reference evidence", DisableParallelization = true)]
public sealed class PostgreSqlReferenceEvidenceCollection;
