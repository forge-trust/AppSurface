using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using ForgeTrust.AppSurface.Deployment;

namespace ForgeTrust.AppSurface.Deployment.GcpCloudRun;

/// <summary>A closed, non-secret mapping from logical deployment ids to externally provisioned GCP resources.</summary>
/// <param name="Environment">Exact Aspire environment selected for rendering.</param>
/// <param name="Project">Existing Google Cloud project id.</param>
/// <param name="Region">Existing Cloud Run region.</param>
/// <param name="Jobs">Logical job ids mapped to distinct physical Cloud Run Job names.</param>
/// <param name="CloudSqlInstanceConnectionName">Existing Cloud SQL `project:region:instance` name.</param>
/// <param name="Network">Existing Direct VPC network binding.</param>
/// <param name="ServiceAccounts">Logical identities mapped to user-managed service-account emails.</param>
/// <param name="Secrets">Logical secrets mapped to Secret Manager identifiers and version modes.</param>
public sealed record GcpCloudRunBindingProfile(
    string Environment,
    string Project,
    string Region,
    IReadOnlyDictionary<string, string> Jobs,
    string CloudSqlInstanceConnectionName,
    GcpCloudRunNetworkBinding Network,
    IReadOnlyDictionary<string, string> ServiceAccounts,
    IReadOnlyDictionary<string, GcpCloudRunSecretBinding> Secrets)
{
    /// <summary>Gets the only schema version supported by this package.</summary>
    public const string SupportedSchemaVersion = "1.0";

    /// <summary>Loads and strictly validates a profile without resolving any secret value.</summary>
    /// <param name="path">Path to a regular JSON file. Symbolic links are rejected.</param>
    /// <param name="expectedEnvironment">Aspire environment selected for publishing.</param>
    /// <param name="cancellationToken">Cancellation observed while reading and parsing the profile.</param>
    public static async Task<GcpCloudRunBindingProfile> LoadAsync(string path, string expectedEnvironment, CancellationToken cancellationToken = default)
        => await LoadCoreAsync(path, expectedEnvironment, trustedRoot: null, cancellationToken).ConfigureAwait(false);

    /// <summary>Loads a profile and rejects symbolic links in every path component beneath a trusted root.</summary>
    /// <param name="path">Path to the profile file.</param>
    /// <param name="expectedEnvironment">Aspire environment selected for publishing.</param>
    /// <param name="trustedRoot">Trusted AppHost root that must lexically contain the profile.</param>
    /// <param name="cancellationToken">Cancellation observed while reading and parsing the profile.</param>
    public static async Task<GcpCloudRunBindingProfile> LoadAsync(string path, string expectedEnvironment, string trustedRoot, CancellationToken cancellationToken = default)
        => await LoadCoreAsync(path, expectedEnvironment, trustedRoot, cancellationToken).ConfigureAwait(false);

    private static async Task<GcpCloudRunBindingProfile> LoadCoreAsync(string path, string expectedEnvironment, string? trustedRoot, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedEnvironment);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw Failure("ASDEPLOY130", "GCP binding profile was not found.", "The configured profile path does not name an existing file.", "Provide a checked-in non-secret GcpCloudRunBindingProfile.v1 JSON file.");
        }

        RejectSymbolicLinkChain(fullPath, trustedRoot);

        JsonDocument document;
        try
        {
            await using var stream = File.OpenRead(fullPath);
            document = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false }, cancellationToken);
        }
        catch (JsonException)
        {
            throw Failure("ASDEPLOY132", "GCP binding profile is malformed.", "The file is not valid closed-schema JSON.", "Fix the JSON and validate it against GcpCloudRunBindingProfile.v1.");
        }

        using (document)
        {
            var root = RequireObject(document.RootElement, "$", "ASDEPLOY132");
            RejectSecretShapedProperties(root, "$");
            RejectUnknown(root, "$", "schemaVersion", "environment", "project", "region", "jobs", "cloudSqlInstanceConnectionName", "network", "serviceAccounts", "secrets");
            var schema = RequiredString(root, "schemaVersion", "$");
            if (!string.Equals(schema, SupportedSchemaVersion, StringComparison.Ordinal))
            {
                throw Failure("ASDEPLOY133", "Unsupported GCP binding schema.", $"Schema '{schema}' is not supported.", $"Use schemaVersion '{SupportedSchemaVersion}'.");
            }

            var environment = RequiredString(root, "environment", "$");
            if (!string.Equals(environment, expectedEnvironment, StringComparison.Ordinal))
            {
                throw Failure("ASDEPLOY134", "GCP binding environment mismatch.", $"Profile environment '{environment}' differs from Aspire environment '{expectedEnvironment}'.", "Select the matching profile or Aspire environment.");
            }

            var project = RequiredString(root, "project", "$");
            var region = RequiredString(root, "region", "$");
            ValidateProjectId(project, "project");
            ValidateRegion(region, "region");
            var jobs = StringMap(root, "jobs", "$", ValidateCloudRunJobName);
            var serviceAccounts = StringMap(root, "serviceAccounts", "$", ValidateServiceAccount);
            var cloudSql = RequiredString(root, "cloudSqlInstanceConnectionName", "$");
            ValidateCloudSqlConnectionName(cloudSql);

            var networkElement = RequiredObject(root, "network", "$");
            RejectUnknown(networkElement, "$.network", "network", "subnetwork", "egress");
            var networkName = RequiredString(networkElement, "network", "$.network");
            var subnetworkName = RequiredString(networkElement, "subnetwork", "$.network");
            ValidateNetworkReference(networkName, regional: false);
            ValidateNetworkReference(subnetworkName, regional: true);
            var network = new GcpCloudRunNetworkBinding(
                networkName,
                subnetworkName,
                RequiredString(networkElement, "egress", "$.network"));
            if (network.Egress is not ("PRIVATE_RANGES_ONLY" or "ALL_TRAFFIC"))
            {
                throw Failure("ASDEPLOY136", "Direct VPC egress is invalid.", $"Egress '{network.Egress}' is unsupported.", "Use PRIVATE_RANGES_ONLY or ALL_TRAFFIC.");
            }

            var secretElement = RequiredObject(root, "secrets", "$");
            var secrets = new SortedDictionary<string, GcpCloudRunSecretBinding>(StringComparer.Ordinal);
            foreach (var property in secretElement.EnumerateObject())
            {
                ValidateLogicalKey(property.Name, "$.secrets");
                var item = RequireObject(property.Value, $"$.secrets.{property.Name}", "ASDEPLOY132");
                RejectUnknown(item, $"$.secrets.{property.Name}", "secretId", "versionMode");
                var secretId = RequiredString(item, "secretId", $"$.secrets.{property.Name}");
                ValidateSecretId(secretId, "secretId");
                var mode = RequiredString(item, "versionMode", $"$.secrets.{property.Name}");
                if (!string.Equals(mode, "latest", StringComparison.Ordinal))
                {
                    throw Failure("ASDEPLOY137", "Secret version mode is unsupported.", $"Version mode '{mode}' is not supported in v1.", "Use the explicit latest rotation mode.");
                }

                secrets.Add(property.Name, new GcpCloudRunSecretBinding(secretId, mode));
            }

            return new GcpCloudRunBindingProfile(environment, project, region, ReadOnly(jobs), cloudSql, network, ReadOnly(serviceAccounts), new ReadOnlyDictionary<string, GcpCloudRunSecretBinding>(secrets));
        }
    }

    private static SortedDictionary<string, string> StringMap(JsonElement parent, string name, string path, Action<string, string> validate)
    {
        var element = RequiredObject(parent, name, path);
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            ValidateLogicalKey(property.Name, $"{path}.{name}");
            var value = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
            if (string.IsNullOrWhiteSpace(value)) throw Failure("ASDEPLOY132", "GCP binding profile is malformed.", $"'{path}.{name}.{property.Name}' must be a non-empty string.", "Fix the binding profile.");
            validate(value, property.Name);
            result.Add(property.Name, value);
        }

        return result;
    }

    private static ReadOnlyDictionary<string, string> ReadOnly(SortedDictionary<string, string> source) => new(source);

    private static void RejectSecretShapedProperties(JsonElement element, string path)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Contains("value", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("password", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("connectionString", StringComparison.OrdinalIgnoreCase))
            {
                throw Failure("ASDEPLOY139", "Secret-bearing profile field rejected.", $"'{path}.{property.Name}' is shaped like secret material.", "Store only physical secret identifiers in the binding profile.");
            }

            if (property.Value.ValueKind == JsonValueKind.Object) RejectSecretShapedProperties(property.Value, $"{path}.{property.Name}");
        }
    }

    private static void RejectUnknown(JsonElement element, string path, params string[] allowed)
    {
        var names = allowed.ToHashSet(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Contains(property.Name)) throw Failure("ASDEPLOY140", "Unknown GCP binding property.", $"'{path}.{property.Name}' is not part of the closed v1 schema.", "Remove the property or upgrade to a schema that defines it.");
        }
    }

    private static JsonElement RequireObject(JsonElement element, string path, string code)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Failure(code, "GCP binding profile is malformed.", $"'{path}' must be an object.", "Fix the binding profile JSON shape.");
        var duplicate = element.EnumerateObject().GroupBy(property => property.Name, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw Failure("ASDEPLOY143", "Duplicate GCP binding property.", $"'{path}.{duplicate.Key}' appears more than once.", "Keep exactly one value for every profile property.");
        return element;
    }

    private static JsonElement RequiredObject(JsonElement element, string name, string path) => element.TryGetProperty(name, out var value) ? RequireObject(value, $"{path}.{name}", "ASDEPLOY132") : throw Failure("ASDEPLOY132", "GCP binding profile is malformed.", $"'{path}.{name}' is required.", "Add the required object.");

    private static string RequiredString(JsonElement element, string name, string path)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) throw Failure("ASDEPLOY132", "GCP binding profile is malformed.", $"'{path}.{name}' must be a non-empty string.", "Add the required value.");
        return value.GetString()!;
    }

    private static void RejectSymbolicLinkChain(string fullPath, string? trustedRoot)
    {
        var root = trustedRoot is null ? Path.GetDirectoryName(fullPath)! : Path.GetFullPath(trustedRoot);
        var relative = Path.GetRelativePath(root, fullPath);
        if (Path.IsPathFullyQualified(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw Failure("ASDEPLOY131", "GCP binding profile escapes its trusted root.", $"'{fullPath}' is outside '{root}'.", "Keep the checked-in profile beneath the AppHost directory.");
        }

        FileSystemInfo? current = new FileInfo(fullPath);
        while (current is not null)
        {
            if (current.LinkTarget is not null || current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw Failure("ASDEPLOY131", "GCP binding profile symlinks are forbidden.", $"'{current.FullName}' is a symbolic link or reparse point.", "Use a regular checked-in JSON file and directory chain.");
            }

            current = current switch
            {
                FileInfo file => file.Directory,
                DirectoryInfo directory => directory.Parent,
                _ => null,
            };
            if (current is DirectoryInfo trustedDirectory && string.Equals(trustedDirectory.FullName, root, StringComparison.Ordinal))
            {
                if (trustedDirectory.LinkTarget is not null || trustedDirectory.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw Failure("ASDEPLOY131", "GCP binding profile symlinks are forbidden.", $"'{trustedDirectory.FullName}' is a symbolic link or reparse point.", "Use a real AppHost directory and checked-in profile path.");
                break;
            }
        }
    }

    private static void ValidateProjectId(string value, string field) => ValidatePattern(value, field, "^[a-z][a-z0-9-]{4,28}[a-z0-9]$", "a 6-30 character lowercase Google Cloud project id");

    private static void ValidateRegion(string value, string field) => ValidatePattern(value, field, "^[a-z]+(?:-[a-z0-9]+)+[0-9]$", "a canonical Google Cloud region such as us-central1");

    private static void ValidateCloudRunJobName(string value, string field) => ValidatePattern(value, field, "^[a-z](?:[a-z0-9-]{0,61}[a-z0-9])?$", "a lowercase Cloud Run Job name of at most 63 characters");

    private static void ValidateSecretId(string value, string field) => ValidatePattern(value, field, "^[A-Za-z0-9_-]{1,255}$", "a Secret Manager secret id containing letters, digits, hyphens, or underscores");

    private static void ValidateServiceAccount(string value, string field)
    {
        ValidateLiteral(value, field);
        if (!Regex.IsMatch(value, "^[a-z][a-z0-9-]{4,28}[a-z0-9]@[a-z][a-z0-9-]{4,28}[a-z0-9]\\.iam\\.gserviceaccount\\.com$", RegexOptions.CultureInvariant)) throw Failure("ASDEPLOY142", "Malformed service account email.", $"'{field}' is not a canonical user-managed service account email.", "Use SERVICE_ACCOUNT_NAME@PROJECT_ID.iam.gserviceaccount.com.");
    }

    private static void ValidateCloudSqlConnectionName(string value)
    {
        ValidateLiteral(value, "cloudSqlInstanceConnectionName");
        var segments = value.Split(':');
        if (segments.Length != 3) throw Failure("ASDEPLOY135", "Cloud SQL connection name is malformed.", "The value must have project:region:instance form.", "Use the instance connection name shown by Cloud SQL.");
        ValidateProjectId(segments[0], "cloudSqlInstanceConnectionName project");
        ValidateRegion(segments[1], "cloudSqlInstanceConnectionName region");
        ValidatePattern(segments[2], "cloudSqlInstanceConnectionName instance", "^[a-z](?:[a-z0-9-]{0,96}[a-z0-9])?$", "a canonical Cloud SQL instance id");
    }

    private static void ValidateNetworkReference(string value, bool regional)
    {
        ValidateLiteral(value, regional ? "subnetwork" : "network");
        var pattern = regional
            ? "^projects/[a-z][a-z0-9-]{4,28}[a-z0-9]/regions/[a-z]+(?:-[a-z0-9]+)+[0-9]/subnetworks/[a-z](?:[a-z0-9-]{0,61}[a-z0-9])?$"
            : "^projects/[a-z][a-z0-9-]{4,28}[a-z0-9]/global/networks/[a-z](?:[a-z0-9-]{0,61}[a-z0-9])?$";
        ValidatePattern(value, regional ? "subnetwork" : "network", pattern, regional ? "a full regional subnetwork resource reference" : "a full global network resource reference");
    }

    private static void ValidatePattern(string value, string field, string pattern, string expected)
    {
        ValidateLiteral(value, field);
        if (!Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant)) throw Failure("ASDEPLOY141", "Malformed GCP identifier.", $"'{field}' is not {expected}.", "Use the canonical GCP resource identifier.");
    }

    private static void ValidateLiteral(string value, string field)
    {
        if (value.Contains("${", StringComparison.Ordinal) || value.Contains("%{", StringComparison.Ordinal)) throw Failure("ASDEPLOY141", "Terraform expression in GCP binding rejected.", $"'{field}' contains Terraform template syntax.", "Use a literal canonical GCP resource identifier.");
    }

    private static void ValidateLogicalKey(string value, string path)
    {
        try
        {
            _ = new DeploymentLogicalId(value);
        }
        catch (ArgumentException)
        {
            throw Failure("ASDEPLOY141", "Malformed logical binding id.", $"'{path}.{value}' is not a canonical AppSurface logical id.", "Use a lowercase logical id beginning with a letter.");
        }
    }

    private static DeploymentValidationException Failure(string code, string problem, string cause, string fix) => new(DeploymentDiagnostic.Create(code, problem, cause, fix));
}

/// <summary>Externally provisioned Direct VPC binding.</summary>
/// <param name="Network">Full global VPC network resource reference.</param>
/// <param name="Subnetwork">Full regional subnetwork resource reference.</param>
/// <param name="Egress">`PRIVATE_RANGES_ONLY` or `ALL_TRAFFIC`.</param>
public sealed record GcpCloudRunNetworkBinding(string Network, string Subnetwork, string Egress);

/// <summary>Physical Secret Manager reference and explicit rotation mode.</summary>
/// <param name="SecretId">Existing Secret Manager secret id.</param>
/// <param name="VersionMode">Explicit version mode; version 1 supports `latest`.</param>
public sealed record GcpCloudRunSecretBinding(string SecretId, string VersionMode);
