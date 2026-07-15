using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ForgeTrust.AppSurface.Deployment;

/// <summary>Identifies the phase in which a deployment resource participates.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeploymentPhase>))]
public enum DeploymentPhase
{
    /// <summary>Prepares an immutable release candidate before application promotion.</summary>
    [JsonStringEnumMemberName("candidate-preparation")]
    CandidatePreparation = 0,
}

/// <summary>Identifies a capability that a deployment target must support.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeploymentCapability>))]
public enum DeploymentCapability
{
    /// <summary>Runs bounded work to completion instead of hosting a long-lived service.</summary>
    [JsonStringEnumMemberName("run-to-completion-job")]
    RunToCompletionJob = 0,

    /// <summary>Connects the workload to an externally provisioned private network.</summary>
    [JsonStringEnumMemberName("private-network")]
    PrivateNetwork = 1,

    /// <summary>Connects the workload to a relational database.</summary>
    [JsonStringEnumMemberName("relational-connection")]
    RelationalConnection = 2,
}

/// <summary>Identifies whether parity is evaluated before or after the generated configuration becomes authoritative.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeploymentParityMode>))]
public enum DeploymentParityMode
{
    /// <summary>Compares operational configuration while a legacy writer remains authoritative.</summary>
    [JsonStringEnumMemberName("shadow")]
    Shadow = 0,

    /// <summary>Also compares AppSurface-owned provenance after the generated configuration becomes authoritative.</summary>
    [JsonStringEnumMemberName("owned")]
    Owned = 1,
}

/// <summary>Identifies how a secret reference is delivered to a workload.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SecretDeliveryKind>))]
public enum SecretDeliveryKind
{
    /// <summary>Delivers a provider reference through an environment variable without resolving its value.</summary>
    [JsonStringEnumMemberName("environment-variable")]
    EnvironmentVariable = 0,
}

/// <summary>A validated logical identifier used by deployment resources and bindings.</summary>
public readonly record struct DeploymentLogicalId
{
    private static readonly Regex Pattern = new("^[a-z][a-z0-9-]{0,62}$", RegexOptions.CultureInvariant);

    /// <summary>Initializes a logical identifier.</summary>
    /// <param name="value">Lowercase identifier beginning with a letter and containing only letters, digits, and hyphens.</param>
    /// <exception cref="ArgumentException">Thrown when the value is not a canonical logical identifier.</exception>
    public DeploymentLogicalId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Pattern.IsMatch(value))
        {
            throw new ArgumentException("ASDEPLOY101: logical ids must begin with a lowercase letter, contain only lowercase letters, digits, or hyphens, and be at most 63 characters.", nameof(value));
        }

        Value = value;
    }

    /// <summary>Gets the canonical identifier value.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>An immutable container image reference pinned to a SHA-256 digest.</summary>
public readonly record struct ImmutableImageReference
{
    private static readonly Regex Pattern = new(
        "^[a-z0-9][a-z0-9._:/-]*@sha256:[0-9a-f]{64}$",
        RegexOptions.CultureInvariant);

    /// <summary>Initializes an immutable image reference.</summary>
    /// <param name="value">Full registry and repository followed by a lowercase SHA-256 digest.</param>
    /// <exception cref="ArgumentException">Thrown for a mutable tag, digest-only value, or malformed digest.</exception>
    public ImmutableImageReference(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Pattern.IsMatch(value))
        {
            throw new ArgumentException("ASDEPLOY102: image identity must be a full registry/repository@sha256 reference with 64 lowercase hexadecimal digits; mutable tags and digest-only values are not supported.", nameof(value));
        }

        Value = value;
    }

    /// <summary>Gets the full immutable image identity.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>A full lowercase source-control commit used only for traceability.</summary>
public readonly record struct SourceRevision
{
    private static readonly Regex Pattern = new("^[0-9a-f]{40}$", RegexOptions.CultureInvariant);

    /// <summary>Initializes a source revision.</summary>
    /// <param name="value">A 40-character lowercase hexadecimal commit.</param>
    /// <exception cref="ArgumentException">Thrown when the revision is abbreviated, uppercase, or malformed.</exception>
    public SourceRevision(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Pattern.IsMatch(value))
        {
            throw new ArgumentException("ASDEPLOY103: source revision must be a full 40-character lowercase hexadecimal commit.", nameof(value));
        }

        Value = value;
    }

    /// <summary>Gets the full source revision.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Defines explicit, provider-neutral run-to-completion bounds.</summary>
public sealed record DeploymentExecutionPolicy
{
    /// <summary>Initializes an execution policy.</summary>
    /// <param name="tasks">Number of tasks; must be positive.</param>
    /// <param name="parallelism">Maximum concurrent tasks; must be positive and no greater than <paramref name="tasks"/>.</param>
    /// <param name="retries">Retry count; must not be negative.</param>
    /// <param name="timeout">Task timeout; must be positive and no greater than 24 hours.</param>
    public DeploymentExecutionPolicy(int tasks, int parallelism, int retries, TimeSpan timeout)
    {
        if (tasks <= 0) throw new ArgumentOutOfRangeException(nameof(tasks), "ASDEPLOY104: task count must be positive.");
        if (parallelism <= 0 || parallelism > tasks) throw new ArgumentOutOfRangeException(nameof(parallelism), "ASDEPLOY105: parallelism must be positive and no greater than task count.");
        if (retries < 0) throw new ArgumentOutOfRangeException(nameof(retries), "ASDEPLOY106: retries must not be negative.");
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromHours(24)) throw new ArgumentOutOfRangeException(nameof(timeout), "ASDEPLOY107: timeout must be positive and no greater than 24 hours.");

        Tasks = tasks;
        Parallelism = parallelism;
        Retries = retries;
        Timeout = timeout;
    }

    /// <summary>Gets the number of tasks.</summary>
    public int Tasks { get; }

    /// <summary>Gets maximum concurrent tasks.</summary>
    public int Parallelism { get; }

    /// <summary>Gets the retry count.</summary>
    public int Retries { get; }

    /// <summary>Gets the task timeout.</summary>
    public TimeSpan Timeout { get; }
}

/// <summary>Describes a logical secret reference without carrying or resolving its value.</summary>
public sealed record SecretBinding
{
    /// <summary>Initializes a logical secret binding.</summary>
    /// <param name="id">Logical secret-binding id.</param>
    /// <param name="parameter">Logical authoring-host parameter id retained without reading its value.</param>
    /// <param name="environmentVariable">Environment name that receives the provider secret reference.</param>
    public SecretBinding(DeploymentLogicalId id, DeploymentLogicalId parameter, string environmentVariable)
    {
        if (id.Value is null) throw new ArgumentException("ASDEPLOY101: secret binding id must be initialized.", nameof(id));
        if (parameter.Value is null) throw new ArgumentException("ASDEPLOY101: secret parameter id must be initialized.", nameof(parameter));
        if (string.IsNullOrWhiteSpace(environmentVariable) || environmentVariable.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '_')))
        {
            throw new ArgumentException("ASDEPLOY108: secret environment variable names must contain only ASCII letters, digits, or underscores.", nameof(environmentVariable));
        }

        Id = id;
        Parameter = parameter;
        EnvironmentVariable = environmentVariable;
    }

    /// <summary>Gets the logical binding id.</summary>
    public DeploymentLogicalId Id { get; }

    /// <summary>Gets the logical Aspire parameter id; its value must never be evaluated by deployment publishing.</summary>
    public DeploymentLogicalId Parameter { get; }

    /// <summary>Gets the environment variable used for reference delivery.</summary>
    public string EnvironmentVariable { get; }

    /// <summary>Gets the only secret-delivery mode supported in v1.</summary>
    public SecretDeliveryKind Delivery => SecretDeliveryKind.EnvironmentVariable;
}

/// <summary>Describes a relational database requirement and its connection-secret reference.</summary>
public sealed record DatabaseBinding
{
    /// <summary>Initializes a database binding.</summary>
    /// <param name="id">Logical database id.</param>
    /// <param name="configurationKey">Application configuration key for the connection.</param>
    /// <param name="connectionSecret">Logical secret binding that supplies the connection reference.</param>
    /// <param name="requireUnixSocket">Whether version 1 Unix-socket delivery is required.</param>
    /// <param name="migrationOwner">Whether this job explicitly owns migrations for the database.</param>
    public DatabaseBinding(DeploymentLogicalId id, string configurationKey, DeploymentLogicalId connectionSecret, bool requireUnixSocket = true, bool migrationOwner = true)
    {
        if (id.Value is null) throw new ArgumentException("ASDEPLOY101: database binding id must be initialized.", nameof(id));
        if (connectionSecret.Value is null) throw new ArgumentException("ASDEPLOY101: database connection-secret id must be initialized.", nameof(connectionSecret));
        if (string.IsNullOrWhiteSpace(configurationKey)) throw new ArgumentException("ASDEPLOY109: database configuration key is required.", nameof(configurationKey));
        Id = id;
        ConfigurationKey = configurationKey;
        ConnectionSecret = connectionSecret;
        RequireUnixSocket = requireUnixSocket;
        MigrationOwner = migrationOwner;
    }

    /// <summary>Gets the logical database id.</summary>
    public DeploymentLogicalId Id { get; }

    /// <summary>Gets the application configuration key.</summary>
    public string ConfigurationKey { get; }

    /// <summary>Gets the referenced logical secret binding.</summary>
    public DeploymentLogicalId ConnectionSecret { get; }

    /// <summary>Gets whether Unix-socket delivery is required.</summary>
    public bool RequireUnixSocket { get; }

    /// <summary>Gets whether the job owns migrations for this database.</summary>
    public bool MigrationOwner { get; }
}

/// <summary>Portable intent for one explicitly bounded migration job.</summary>
public sealed record MigrationJobIntent
{
    private static readonly string[] SecretNameFragments =
    [
        "secret",
        "password",
        "token",
        "credential",
        "connectionstring",
        "databaseurl",
        "apikey",
        "privatekey",
        "accesskey",
    ];

    /// <summary>Initializes migration-job intent and validates all cross-field references.</summary>
    /// <param name="id">Canonical logical job id.</param>
    /// <param name="phase">Explicit deployment phase.</param>
    /// <param name="image">Full immutable image identity.</param>
    /// <param name="command">Executable without shell parsing.</param>
    /// <param name="arguments">Ordered executable arguments.</param>
    /// <param name="execution">Bounded run-to-completion policy.</param>
    /// <param name="connectionSecret">Logical connection-secret binding.</param>
    /// <param name="database">Logical database binding referencing the same secret.</param>
    /// <param name="serviceIdentity">Logical runtime identity.</param>
    /// <param name="requirePrivateNetwork">Whether the required version 1 private-network constraint is declared.</param>
    /// <param name="environment">Optional non-secret application settings.</param>
    public MigrationJobIntent(
        DeploymentLogicalId id,
        DeploymentPhase phase,
        ImmutableImageReference image,
        string command,
        IEnumerable<string>? arguments,
        DeploymentExecutionPolicy execution,
        SecretBinding connectionSecret,
        DatabaseBinding database,
        DeploymentLogicalId serviceIdentity,
        bool requirePrivateNetwork = true,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(connectionSecret);
        ArgumentNullException.ThrowIfNull(database);
        if (!Enum.IsDefined(phase)) throw new ArgumentOutOfRangeException(nameof(phase));
        if (id.Value is null) throw new ArgumentException("ASDEPLOY101: migration job id must be initialized.", nameof(id));
        if (image.Value is null) throw new ArgumentException("ASDEPLOY102: migration image must be initialized.", nameof(image));
        if (serviceIdentity.Value is null) throw new ArgumentException("ASDEPLOY101: service identity id must be initialized.", nameof(serviceIdentity));
        if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("ASDEPLOY110: migration command is required.", nameof(command));
        if (database.ConnectionSecret != connectionSecret.Id) throw new DeploymentValidationException(DeploymentDiagnostic.Create("ASDEPLOY111", "Database connection binding is ambiguous.", "The database references a different logical secret binding.", "Reference the same secret binding from the database and job."));
        if (!database.MigrationOwner) throw new DeploymentValidationException(DeploymentDiagnostic.Create("ASDEPLOY112", "Migration ownership is missing.", "A migration job must explicitly own migrations for its database.", "Set migration ownership on the database binding."));
        if (!requirePrivateNetwork || !database.RequireUnixSocket) throw new DeploymentValidationException(DeploymentDiagnostic.Create("ASDEPLOY128", "Version 1 migration connectivity is incomplete.", "Migration jobs require explicit private networking and Unix-socket database delivery.", "Require private networking and Unix-socket delivery for the version 1 migration-job target."));

        Id = id;
        Phase = phase;
        Image = image;
        Command = command;
        Arguments = Array.AsReadOnly((arguments ?? []).Select(RequireArgument).ToArray());
        Execution = execution;
        ConnectionSecret = connectionSecret;
        Database = database;
        ServiceIdentity = serviceIdentity;
        RequiredCapabilities = Array.AsReadOnly(new[] { DeploymentCapability.PrivateNetwork, DeploymentCapability.RelationalConnection, DeploymentCapability.RunToCompletionJob });
        RequirePrivateNetwork = requirePrivateNetwork;
        if (environment?.ContainsKey(connectionSecret.EnvironmentVariable) is true) throw new DeploymentValidationException(DeploymentDiagnostic.Create("ASDEPLOY129", "Environment setting collides with the connection secret.", $"'{connectionSecret.EnvironmentVariable}' is declared as both plaintext and secret-backed configuration.", "Remove the plaintext setting and keep only the logical secret binding."));
        var environmentCopy = CopyEnvironment(environment);
        Environment = new ReadOnlyDictionary<string, string>(environmentCopy);
    }

    /// <summary>Gets the logical job id.</summary>
    public DeploymentLogicalId Id { get; }

    /// <summary>Gets the deployment phase.</summary>
    public DeploymentPhase Phase { get; }

    /// <summary>Gets the immutable image.</summary>
    public ImmutableImageReference Image { get; }

    /// <summary>Gets the executable command.</summary>
    public string Command { get; }

    /// <summary>Gets ordered command arguments.</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>Gets execution bounds.</summary>
    public DeploymentExecutionPolicy Execution { get; }

    /// <summary>Gets the connection-secret reference.</summary>
    public SecretBinding ConnectionSecret { get; }

    /// <summary>Gets the database binding.</summary>
    public DatabaseBinding Database { get; }

    /// <summary>Gets the logical runtime service identity.</summary>
    public DeploymentLogicalId ServiceIdentity { get; }

    /// <summary>Gets required target capabilities in canonical order.</summary>
    public IReadOnlyList<DeploymentCapability> RequiredCapabilities { get; }

    /// <summary>Gets whether private networking is required.</summary>
    public bool RequirePrivateNetwork { get; }

    /// <summary>Gets sorted artifact-visible environment settings after secret-shaped names are rejected.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; }

    private static string RequireArgument(string argument) => argument ?? throw new ArgumentException("ASDEPLOY113: command arguments cannot be null.", nameof(argument));

    private static SortedDictionary<string, string> CopyEnvironment(IReadOnlyDictionary<string, string>? source)
    {
        var copy = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in source ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null) throw new ArgumentException("ASDEPLOY114: environment names and values cannot be null or blank.", nameof(source));
            if (IsSecretShapedName(pair.Key)) throw new DeploymentValidationException(DeploymentDiagnostic.Create("ASDEPLOY115", "Plaintext secret-shaped environment setting rejected.", "Deployment intent environment is non-secret evidence.", "Use a logical SecretBinding instead."));
            copy.Add(pair.Key, pair.Value);
        }

        return copy;
    }

    private static bool IsSecretShapedName(string name)
    {
        var normalized = string.Concat(name.Where(char.IsAsciiLetterOrDigit)).ToLowerInvariant();
        return SecretNameFragments.Any(normalized.Contains);
    }
}

/// <summary>The schema-versioned provider-neutral deployment artifact.</summary>
public sealed record DeploymentIntent
{
    /// <summary>Initializes deployment intent and sorts jobs by logical id.</summary>
    public DeploymentIntent(string environment, SourceRevision sourceRevision, IEnumerable<MigrationJobIntent> migrationJobs)
    {
        if (string.IsNullOrWhiteSpace(environment)) throw new ArgumentException("ASDEPLOY116: Aspire environment is required.", nameof(environment));
        if (sourceRevision.Value is null) throw new ArgumentException("ASDEPLOY103: source revision must be initialized.", nameof(sourceRevision));
        var jobs = (migrationJobs ?? throw new ArgumentNullException(nameof(migrationJobs))).OrderBy(job => job.Id.Value, StringComparer.Ordinal).ToArray();
        var duplicate = jobs.GroupBy(job => job.Id).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new DeploymentValidationException(DeploymentDiagnostic.Create("ASDEPLOY117", "Duplicate deployment logical name.", $"More than one migration job uses '{duplicate.Key}'.", "Assign each annotated resource a unique logical name."));
        if (jobs.Length == 0) throw new DeploymentValidationException(DeploymentDiagnostic.Create("ASDEPLOY118", "No migration jobs were assigned.", "The target found no explicit migration annotations.", "Annotate and explicitly assign at least one project resource."));

        Environment = environment;
        SourceRevision = sourceRevision;
        MigrationJobs = Array.AsReadOnly(jobs);
    }

    /// <summary>Gets the schema identifier.</summary>
    [JsonPropertyName("$schema")]
    public string Schema => "https://appsurface.dev/schemas/deployment-intent.v1.json";

    /// <summary>Gets the schema version.</summary>
    public string SchemaVersion => "1.0";

    /// <summary>Gets the Aspire environment.</summary>
    public string Environment { get; }

    /// <summary>Gets the explicit source revision.</summary>
    public SourceRevision SourceRevision { get; }

    /// <summary>Gets migration jobs in canonical logical-id order.</summary>
    public IReadOnlyList<MigrationJobIntent> MigrationJobs { get; }
}

/// <summary>A stable safe deployment diagnostic with problem, cause, fix, and documentation.</summary>
/// <param name="Code">Stable `ASDEPLOY` code.</param>
/// <param name="Problem">Safe one-line failure description.</param>
/// <param name="Cause">Safe explanation without secret values or raw provider output.</param>
/// <param name="Fix">Concrete remediation.</param>
/// <param name="Documentation">Versioned troubleshooting link.</param>
public sealed record DeploymentDiagnostic(string Code, string Problem, string Cause, string Fix, Uri Documentation)
{
    /// <summary>Creates a diagnostic linked to the deployment troubleshooting reference.</summary>
    public static DeploymentDiagnostic Create(string code, string problem, string cause, string fix) =>
        new(code, problem, cause, fix, new Uri($"https://appsurface.dev/docs/deployment/diagnostics#{code.ToLowerInvariant()}"));
}

/// <summary>Thrown when deployment intent or provider inputs violate a stable deployment contract.</summary>
public sealed class DeploymentValidationException : InvalidOperationException
{
    /// <summary>Initializes an exception from a safe diagnostic.</summary>
    /// <param name="diagnostic">Structured safe diagnostic exposed to callers.</param>
    public DeploymentValidationException(DeploymentDiagnostic diagnostic)
        : base($"{diagnostic.Code}: {diagnostic.Problem} Cause: {diagnostic.Cause} Fix: {diagnostic.Fix} Docs: {diagnostic.Documentation}")
    {
        Diagnostic = diagnostic;
    }

    /// <summary>Gets the structured diagnostic.</summary>
    public DeploymentDiagnostic Diagnostic { get; }
}

/// <summary>Serializes portable deployment intent to deterministic UTF-8 JSON and hashes artifact bytes.</summary>
public static class DeploymentCanonicalJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>Serializes a value as UTF-8 without a BOM, with stable indentation and one trailing LF.</summary>
    public static byte[] Serialize<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var json = JsonSerializer.Serialize(value, Options).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n') + "\n";
        return new UTF8Encoding(false).GetBytes(json);
    }

    /// <summary>Computes a lowercase SHA-256 hash for exact artifact bytes.</summary>
    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

/// <summary>A named deterministic deployment artifact whose content cannot be mutated after creation.</summary>
public sealed class DeploymentArtifact
{
    private static readonly HashSet<string> WindowsReservedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CLOCK$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly byte[] _content;

    private DeploymentArtifact(string fileName, byte[] content, string sha256)
    {
        FileName = fileName;
        _content = content;
        Sha256 = sha256;
    }

    /// <summary>Gets the safe single-segment artifact file name.</summary>
    public string FileName { get; }

    /// <summary>Gets a defensive copy of the exact artifact bytes.</summary>
    public byte[] Content => _content.ToArray();

    /// <summary>Gets the lowercase SHA-256 hash of the exact artifact bytes.</summary>
    public string Sha256 { get; }

    /// <summary>Creates an artifact and computes its content hash.</summary>
    /// <param name="fileName">Portable single-segment artifact name.</param>
    /// <param name="content">Exact bytes copied into the immutable artifact.</param>
    /// <returns>An artifact with defensively copied content and its lowercase SHA-256 hash.</returns>
    public static DeploymentArtifact Create(string fileName, byte[] content)
    {
        if (!IsPortableFileName(fileName))
        {
            throw new ArgumentException("ASDEPLOY119: artifact name must be one portable safe file name.", nameof(fileName));
        }
        ArgumentNullException.ThrowIfNull(content);
        var copiedContent = content.ToArray();
        return new DeploymentArtifact(fileName, copiedContent, DeploymentCanonicalJson.Hash(copiedContent));
    }

    private static bool IsPortableFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName is "." or ".." ||
            fileName.Contains('/') ||
            fileName.Contains('\\') ||
            fileName.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0 ||
            fileName.Any(char.IsControl) ||
            fileName.EndsWith('.') ||
            fileName.EndsWith(' ') ||
            Path.GetFileName(fileName) != fileName)
        {
            return false;
        }

        var deviceStem = fileName.Split('.', 2)[0];
        return !WindowsReservedFileNames.Contains(deviceStem);
    }
}

/// <summary>Writes a complete target-owned artifact directory through a staged same-parent directory swap.</summary>
/// <remarks>
/// The writer refuses non-empty directories that do not contain its ownership marker and refuses unexpected files in
/// an owned directory. It stages every artifact before replacing the directory, so validation, serialization, and
/// cancellation failures do not expose a partial new bundle. The marker is internal bookkeeping and is not a release
/// evidence artifact.
/// </remarks>
public static class DeploymentArtifactBundleWriter
{
    /// <summary>Gets the file used to identify an AppSurface-owned artifact directory.</summary>
    public const string OwnershipMarkerFileName = ".appsurface-deployment-output.v1";

    /// <summary>Writes a complete deterministic artifact bundle.</summary>
    /// <param name="outputDirectory">Dedicated output directory owned by one deployment target.</param>
    /// <param name="target">Stable target identifier stored in the ownership marker.</param>
    /// <param name="artifacts">Complete artifact set; file names must be unique.</param>
    /// <param name="cancellationToken">Cancels before the staged directory replaces the current bundle.</param>
    /// <exception cref="DeploymentValidationException">Thrown for unsafe paths, non-owned files, or duplicate names.</exception>
    public static async Task WriteAsync(
        string outputDirectory,
        string target,
        IEnumerable<DeploymentArtifact> artifacts,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory)) throw new ArgumentException("Output directory is required.", nameof(outputDirectory));
        if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException("Target is required.", nameof(target));

        var artifactArray = (artifacts ?? throw new ArgumentNullException(nameof(artifacts))).OrderBy(item => item.FileName, StringComparer.Ordinal).ToArray();
        var duplicate = artifactArray.GroupBy(item => item.FileName, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw Validation("ASDEPLOY120", "Duplicate artifact file name.", $"More than one artifact is named '{duplicate.Key}'.", "Emit each target-owned artifact exactly once.");
        if (artifactArray.Any(item => string.Equals(item.FileName, OwnershipMarkerFileName, StringComparison.OrdinalIgnoreCase))) throw Validation("ASDEPLOY120", "Artifact name is reserved.", $"'{OwnershipMarkerFileName}' is the output ownership marker.", "Choose a different artifact file name.");

        var output = Path.GetFullPath(outputDirectory);
        var parent = Directory.GetParent(output)?.FullName ?? throw Validation("ASDEPLOY121", "Unsafe artifact output path.", "The output path has no parent directory.", "Choose a dedicated directory beneath the AppHost output path.");
        var name = Path.GetFileName(output.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(name)) throw Validation("ASDEPLOY121", "Unsafe artifact output path.", "The filesystem root cannot be target-owned.", "Choose a dedicated directory beneath the AppHost output path.");

        ValidateExistingDirectory(output, target, artifactArray);
        Directory.CreateDirectory(parent);

        var suffix = Guid.NewGuid().ToString("N");
        var staging = Path.Join(parent, $".{name}.appsurface-stage-{suffix}");
        var backup = Path.Join(parent, $".{name}.appsurface-backup-{suffix}");
        try
        {
            Directory.CreateDirectory(staging);
            foreach (var artifact in artifactArray)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await File.WriteAllBytesAsync(Path.Join(staging, artifact.FileName), artifact.Content, cancellationToken).ConfigureAwait(false);
            }

            await File.WriteAllTextAsync(Path.Join(staging, OwnershipMarkerFileName), target + "\n", new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var hadExisting = Directory.Exists(output);
            if (hadExisting) Directory.Move(output, backup);
            try
            {
                Directory.Move(staging, output);
            }
            catch
            {
                if (hadExisting && Directory.Exists(backup) && !Directory.Exists(output)) Directory.Move(backup, output);
                throw;
            }

            if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            if (Directory.Exists(backup) && !Directory.Exists(output)) Directory.Move(backup, output);
        }
    }

    private static void ValidateExistingDirectory(string output, string target, IReadOnlyCollection<DeploymentArtifact> artifacts)
    {
        if (!Directory.Exists(output)) return;
        var directoryInfo = new DirectoryInfo(output);
        if (directoryInfo.LinkTarget is not null) throw Validation("ASDEPLOY122", "Symbolic-link output rejected.", "The target output directory is a symbolic link.", "Use a real dedicated directory beneath the Aspire output path.");

        var entries = directoryInfo.EnumerateFileSystemInfos().ToArray();
        if (entries.Length == 0) return;
        var marker = Path.Join(output, OwnershipMarkerFileName);
        if (!File.Exists(marker)) throw Validation("ASDEPLOY123", "Output directory is not AppSurface-owned.", "The non-empty directory has no AppSurface ownership marker.", "Choose an empty dedicated directory or remove it through its current owner.");
        if (!string.Equals(File.ReadAllText(marker), target + "\n", StringComparison.Ordinal)) throw Validation("ASDEPLOY124", "Output directory belongs to another target.", "The ownership marker does not match this deployment target.", "Give each deployment target a distinct output directory.");

        var allowed = new HashSet<string>(artifacts.Select(item => item.FileName), StringComparer.Ordinal) { OwnershipMarkerFileName };
        var unexpected = entries.FirstOrDefault(entry => !allowed.Contains(entry.Name));
        if (unexpected is not null) throw Validation("ASDEPLOY125", "Owned output contains an unexpected file.", $"'{unexpected.Name}' is not part of the complete target artifact set.", "Move unrelated files out of the dedicated target directory before publishing.");
    }

    private static DeploymentValidationException Validation(string code, string problem, string cause, string fix) =>
        new(DeploymentDiagnostic.Create(code, problem, cause, fix));
}

/// <summary>Inputs passed to an Aspire-independent deployment target renderer.</summary>
/// <param name="Intent">Validated provider-neutral deployment intent.</param>
/// <param name="BindingProfilePath">Resolved provider binding-profile path.</param>
/// <param name="OutputDirectory">Dedicated target output directory.</param>
/// <param name="GeneratorVersion">AppSurface generator package version recorded in evidence.</param>
/// <param name="BindingProfileRoot">Optional trusted authoring-host root used to reject symlink escapes before reading.</param>
public sealed record DeploymentRenderRequest(DeploymentIntent Intent, string BindingProfilePath, string OutputDirectory, string GeneratorVersion, string? BindingProfileRoot = null);

/// <summary>Deterministic artifacts produced by a deployment target.</summary>
/// <param name="Target">Stable target identifier.</param>
/// <param name="Artifacts">Complete deterministic artifact set.</param>
public sealed record DeploymentRenderResult(string Target, IReadOnlyList<DeploymentArtifact> Artifacts);

/// <summary>Inputs passed to a read-only deployment verifier.</summary>
/// <param name="RenderResult">Validated render result to verify.</param>
/// <param name="ParityMode">Shadow or owned comparison policy.</param>
public sealed record DeploymentVerifyRequest(DeploymentRenderResult RenderResult, DeploymentParityMode ParityMode);

/// <summary>Result of read-only deployment verification.</summary>
/// <param name="IsMatch">Whether all required fields and public-principal checks matched.</param>
/// <param name="ComparedFields">Number of normalized field checks performed.</param>
/// <param name="Diagnostics">Safe drift diagnostics.</param>
/// <param name="AuthorizationStatus">Explicit limitation or authorization evidence status.</param>
public sealed record DeploymentVerifyResult(bool IsMatch, int ComparedFields, IReadOnlyList<DeploymentDiagnostic> Diagnostics, string AuthorizationStatus);

/// <summary>Compiles portable deployment intent and optionally verifies deployed state without mutation.</summary>
public interface IDeploymentTarget
{
    /// <summary>Gets the stable target identifier.</summary>
    string Name { get; }

    /// <summary>Gets target capabilities used for fail-fast intent validation.</summary>
    IReadOnlySet<DeploymentCapability> Capabilities { get; }

    /// <summary>Renders deterministic artifacts without cloud calls or infrastructure mutation.</summary>
    Task<DeploymentRenderResult> RenderAsync(DeploymentRenderRequest request, CancellationToken cancellationToken = default);

    /// <summary>Performs read-only deployed-state verification; it must not accept or execute a job.</summary>
    Task<DeploymentVerifyResult> VerifyAsync(DeploymentVerifyRequest request, CancellationToken cancellationToken = default);
}
