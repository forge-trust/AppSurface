using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Describes one ordered, checksum-verified PostgreSQL migration and any client execution override it owns.</summary>
/// <param name="Version">The contiguous, one-based schema version.</param>
/// <param name="Name">The stable migration name parsed from the embedded resource.</param>
/// <param name="Sql">The normalized migration SQL.</param>
/// <param name="Sha256">The lowercase SHA-256 digest of <paramref name="Sql"/>.</param>
/// <param name="CommandTimeoutSeconds">
/// An optional positive client command timeout for migration SQL whose bounded server-side work exceeds the data
/// source default. <see langword="null"/> preserves the configured data-source timeout.
/// </param>
internal sealed record DurablePostgreSqlMigration(
    int Version,
    string Name,
    string Sql,
    string Sha256,
    int? CommandTimeoutSeconds = null);

internal static partial class DurablePostgreSqlMigrationCatalog
{
    private const string ResourceMarker = ".Migrations.";
    private static readonly IReadOnlyList<DurablePostgreSqlMigration> DefaultMigrations =
        LoadValidated(typeof(DurablePostgreSqlMigrationCatalog).Assembly);

    internal static int RequiredVersion => DefaultMigrations.Count;

    internal static IReadOnlyList<DurablePostgreSqlMigration> Load(Assembly? assembly = null) =>
        assembly is null ? DefaultMigrations : LoadValidated(assembly);

    private static IReadOnlyList<DurablePostgreSqlMigration> LoadValidated(Assembly assembly)
    {
        var migrations = assembly.GetManifestResourceNames()
            .Where(name => name.Contains(ResourceMarker, StringComparison.Ordinal))
            .Select(name => LoadMigration(assembly, name))
            .OrderBy(migration => migration.Version)
            .ToArray();

        if (migrations.Length == 0)
        {
            throw new InvalidOperationException("The durable PostgreSQL package contains no embedded migrations.");
        }

        for (var index = 0; index < migrations.Length; index++)
        {
            var expected = index + 1;
            if (migrations[index].Version != expected)
            {
                throw new InvalidOperationException(
                    $"Durable PostgreSQL migrations must be contiguous from version 1. Expected {expected:D4}, but found {migrations[index].Version:D4}.");
            }
        }

        return Array.AsReadOnly(migrations);
    }

    private static DurablePostgreSqlMigration LoadMigration(Assembly assembly, string resourceName)
    {
        var markerIndex = resourceName.LastIndexOf(ResourceMarker, StringComparison.Ordinal);
        var fileName = resourceName[(markerIndex + ResourceMarker.Length)..];
        var match = MigrationFileName().Match(fileName);
        if (!match.Success || !int.TryParse(match.Groups["version"].Value, out var version))
        {
            throw new InvalidOperationException(
                $"Embedded durable migration '{resourceName}' must use the NNNN_name.sql naming convention.");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded durable migration '{resourceName}' could not be read.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var sql = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
        int? commandTimeoutSeconds =
            version == PostgreSqlDurableRuntimeSchemaManager.ExtendedDeadlineMigrationVersion
                ? PostgreSqlDurableRuntimeSchemaManager.ExtendedMigrationCommandTimeoutSeconds
                : null;
        return new DurablePostgreSqlMigration(
            version,
            match.Groups["name"].Value,
            sql,
            hash,
            commandTimeoutSeconds);
    }

    [GeneratedRegex(@"^(?<version>\d{4})_(?<name>[a-z0-9_]+)\.sql$", RegexOptions.CultureInvariant)]
    private static partial Regex MigrationFileName();
}
