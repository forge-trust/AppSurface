using System.Security.Cryptography;
using System.Text.Json;

if (args is ["--self-test"])
{
    VerifyReceiptParser();
    Console.WriteLine("receipt-parser=self-test-passed");
    return;
}

var options = ParseArguments(args);
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("APP_SURFACE_PREFLIGHT_TOOL")))
{
    throw new InvalidOperationException("The installed exact-bundle CLI tool path is unavailable.");
}
var verified = await File.ReadAllBytesAsync(options.VerifiedArtifactsPath);
using var artifactDocument = JsonDocument.Parse(verified);
var artifactRoot = artifactDocument.RootElement;
var packageVersion = artifactRoot.GetProperty("packageVersion").GetString()
    ?? throw new InvalidOperationException("Verified package version is missing.");
var bundleManifestSha256 = artifactRoot.GetProperty("manifestSha256").GetString()
    ?? throw new InvalidOperationException("Verified package manifest hash is missing.");
var cliPackage = artifactRoot.GetProperty("packages").GetProperty("forgetrust.appsurface.cli");
var providerPackage = artifactRoot.GetProperty("packages").GetProperty("forgetrust.appsurface.durable.postgresql");
var cliSha256 = cliPackage.GetProperty("sha256").GetString()!;
var providerSha256 = providerPackage.GetProperty("sha256").GetString()!;
await using var recipeStream = File.OpenRead(options.RecipePath);
var recipeSha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(recipeStream));
await using var schema10FixtureStream = File.OpenRead(options.Schema10FixturePath);
var schema10FixtureSha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(schema10FixtureStream));

var manifestBytes = await File.ReadAllBytesAsync(options.RolePairsPath);
var manifestSha256 = Convert.ToHexStringLower(SHA256.HashData(manifestBytes));
if (manifestBytes.Length is 0 or > 65_536)
{
    throw new InvalidOperationException("The role-pair manifest must be between 1 byte and 64 KiB.");
}

var pairs = ReadPairs(manifestBytes);
var ownerName = ValidateRoleName(options.MigrationOwnerRole);
if (pairs.Any(pair => string.Equals(pair.Dispatcher, ownerName, StringComparison.Ordinal)
    || string.Equals(pair.Runtime, ownerName, StringComparison.Ordinal)))
{
    throw new InvalidOperationException("The migration owner overlaps a manifest role.");
}
if (pairs.Count != 2
    || pairs[0].Profile != "full"
    || pairs[1].Profile != "work_only")
{
    throw new InvalidOperationException("The package consumer requires a full pair followed by a work_only pair; one-pair proof uses the first pair from this manifest.");
}

var storeId = options.StoreId is null ? Guid.Empty : Guid.Parse(options.StoreId);
var activeEpoch = options.ActiveEpoch is null ? Guid.Empty : Guid.Parse(options.ActiveEpoch);
if (options.StoreId is not null && storeId == Guid.Empty || options.ActiveEpoch is not null && activeEpoch == Guid.Empty)
    throw new InvalidOperationException("Optional fixture StoreId/epoch overrides must be nonempty GUIDs.");

var cliPath = Environment.GetEnvironmentVariable("APP_SURFACE_PREFLIGHT_TOOL");
if (string.IsNullOrWhiteSpace(cliPath) || !File.Exists(cliPath))
{
    throw new InvalidOperationException("The installed exact-bundle CLI tool path is unavailable.");
}

await DisposableProofController.RunAsync(
    options,
    artifactRoot,
    packageVersion,
    bundleManifestSha256,
    cliSha256,
    providerSha256,
    recipeSha256,
    schema10FixtureSha256,
    manifestSha256,
    ownerName,
    storeId,
    activeEpoch,
    pairs,
    cliPath);

static IReadOnlyList<RolePair> ReadPairs(byte[] bytes)
{
    using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 8
    });
    var root = document.RootElement;
    if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2
        || !root.TryGetProperty("version", out var version) || version.GetInt32() != 1
        || !root.TryGetProperty("pairs", out var pairsElement) || pairsElement.ValueKind != JsonValueKind.Array)
    {
        throw new InvalidOperationException("The role-pair manifest must use the version-1 complete pairs shape.");
    }

    var result = new List<RolePair>();
    var names = new HashSet<string>(StringComparer.Ordinal);
    foreach (var pair in pairsElement.EnumerateArray())
    {
        if (pair.ValueKind != JsonValueKind.Object || pair.EnumerateObject().Count() != 3)
        {
            throw new InvalidOperationException("Each pair must contain exactly dispatcher, runtime, and dispatcher_profile.");
        }

        var dispatcher = ValidateRoleName(pair.GetProperty("dispatcher").GetString()!);
        var runtime = ValidateRoleName(pair.GetProperty("runtime").GetString()!);
        var profile = pair.GetProperty("dispatcher_profile").GetString();
        if (profile is not ("full" or "work_only") || !names.Add(dispatcher) || !names.Add(runtime))
        {
            throw new InvalidOperationException("The manifest contains an invalid profile or duplicate role name.");
        }

        result.Add(new RolePair(dispatcher, runtime, profile));
    }

    if (result.Count is < 1 or > 32)
    {
        throw new InvalidOperationException("The manifest must contain between 1 and 32 role pairs.");
    }

    return result;
}

static string ValidateRoleName(string value)
{
    if (string.IsNullOrEmpty(value) || System.Text.Encoding.UTF8.GetByteCount(value) > 63
        || value.Any(char.IsControl))
    {
        throw new InvalidOperationException("A PostgreSQL role name is empty, overlength, or contains control characters.");
    }

    return value;
}

static void VerifyReceiptParser()
{
    CliMachineReceiptParser.VerifySelfTest();
}

static ConsumerOptions ParseArguments(string[] arguments)
{
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 0; index < arguments.Length; index += 2)
    {
        if (index + 1 >= arguments.Length || !arguments[index].StartsWith("--", StringComparison.Ordinal)
            || !values.TryAdd(arguments[index], arguments[index + 1]))
        {
            throw new InvalidOperationException("Consumer arguments are malformed or duplicated.");
        }
    }

    string Required(string key) => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new InvalidOperationException($"Consumer argument {key} is required.");

    var defaults = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../examples/durable-postgresql/role-pairs-full-and-work-only.example.json"));
    var result = new ConsumerOptions(
        Required("--verified-artifacts"),
        Required("--recipe"),
        Required("--receipt"),
        Required("--source-commit"),
        Required("--run-id"),
        Required("--artifact-id"),
        values.TryGetValue("--role-pairs-file", out var rolePairs) ? rolePairs : Environment.GetEnvironmentVariable("PREFLIGHT_ROLE_PAIRS_FILE") ?? defaults,
        values.TryGetValue("--migration-owner-role", out var owner) ? owner : Environment.GetEnvironmentVariable("PREFLIGHT_MIGRATION_OWNER_ROLE") ?? "appsurface_durable_owner",
        values.TryGetValue("--store-id", out var store) ? store : Environment.GetEnvironmentVariable("PREFLIGHT_STORE_ID"),
        values.TryGetValue("--active-epoch", out var epoch) ? epoch : Environment.GetEnvironmentVariable("PREFLIGHT_ACTIVE_EPOCH"),
        values.TryGetValue("--schema10-fixture", out var fixture) ? fixture : Path.Combine(AppContext.BaseDirectory, "schema10-two-pair.sql"));
    if (values.Keys.Except(GetConsumerOptionNames(), StringComparer.Ordinal).Any())
    {
        throw new InvalidOperationException("Consumer arguments contain an unknown option.");
    }

    return result;
}

static HashSet<string> GetConsumerOptionNames() => new(StringComparer.Ordinal)
{
    "--verified-artifacts", "--recipe", "--receipt", "--source-commit", "--run-id", "--artifact-id",
    "--role-pairs-file", "--migration-owner-role", "--store-id", "--active-epoch", "--schema10-fixture"
};

internal sealed record RolePair(string Dispatcher, string Runtime, string Profile);

internal sealed record ConsumerOptions(
    string VerifiedArtifactsPath,
    string RecipePath,
    string ReceiptPath,
    string SourceCommit,
    string RunId,
    string ArtifactId,
    string RolePairsPath,
    string MigrationOwnerRole,
    string? StoreId,
    string? ActiveEpoch,
    string Schema10FixturePath);

internal sealed record ParsedReceipt(
    string Caller,
    int? Pair,
    string Role,
    string Owner,
    int RuntimeCount,
    string ManifestSha256,
    Guid StoreId,
    Guid? ActiveEpoch);

internal sealed record PreflightReceipt(
    string Scenario,
    string Caller,
    int? Pair,
    string Role,
    string Owner,
    int RuntimeCount,
    string ManifestSha256,
    Guid StoreId,
    Guid? ActiveEpoch,
    double ElapsedMilliseconds)
{
    internal PreflightReceipt WithScenario(string scenario) => this with { Scenario = scenario };
}
