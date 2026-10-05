using System.Text.Json;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Loads and validates the one authored doctor v1 diagnosis matrix used by CLI proof levels.</summary>
/// <remarks>
/// Fixture rows have a unique <c>id</c>, <c>workerPair</c> selection, deterministic <c>scenario</c>, five slash-delimited
/// <c>expectedChecks</c> states in the root's declared credential/schema/epoch/retention/worker order, ordered
/// <c>expectedCodes</c>, <c>exitCode</c>, and a <c>nextAction</c> token (<c>verify</c>, <c>retry</c>, <c>status</c>, or
/// <c>help</c>). Terminal rows may add <c>terminalCategories</c> to state the fixed failure provenance. The internal
/// loader is intentionally reusable by later pure, PostgreSQL, and installed-tool tests in this test assembly; the
/// checked-in JSON remains the shared contract artifact rather than a production API.
/// </remarks>
internal static class DurableDoctorV1Matrix
{
    private const string FixturePath = "Cli/ForgeTrust.AppSurface.Cli.Tests/Fixtures/durable-doctor-v1.json";
    private static readonly string[] CheckOrder = ["credential", "schema", "epoch", "retention", "worker"];
    private static readonly string[] CheckStates = ["P", "F", "NC", "NR"];
    private static readonly string[] NextActions = ["verify", "retry", "status", "help"];
    private static readonly string[] TerminalCategories = [
        "session-affinity", "dependency", "deadline", "cleanup", "caller-canceled", "catalog-contract", "input",
    ];

    /// <summary>Reads the matrix from the repository root and rejects schema drift before any proof consumes it.</summary>
    internal static IReadOnlyList<DurableDoctorMatrixRow> Load()
    {
        var rootPath = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var fixturePath = TestPathUtils.PathUnder(rootPath, FixturePath.Split('/'));
        using var document = JsonDocument.Parse(File.ReadAllBytes(fixturePath));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || RequiredInt(root, "schemaVersion") != 1
            || !RequiredStrings(root, "checkOrder").SequenceEqual(CheckOrder, StringComparer.Ordinal)
            || !CheckStateMapIsCurrent(root.GetProperty("checkStates"))
            || root.EnumerateObject().Select(static property => property.Name)
                .Except(["schemaVersion", "checkOrder", "checkStates", "rows"], StringComparer.Ordinal).Any())
        {
            throw new InvalidDataException("The Durable doctor v1 matrix header is malformed.");
        }

        var rowsElement = root.GetProperty("rows");
        if (rowsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The Durable doctor v1 matrix rows must be an array.");
        }

        var rows = new List<DurableDoctorMatrixRow>(28);
        foreach (var element in rowsElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("A Durable doctor v1 matrix row must be an object.");
            }

            var allowedProperties = new HashSet<string>([
                "id", "workerPair", "scenario", "expectedChecks", "expectedCodes", "exitCode", "nextAction", "terminalCategories",
            ], StringComparer.Ordinal);
            var seenProperties = new HashSet<string>(StringComparer.Ordinal);
            if (element.EnumerateObject().Any(property => !allowedProperties.Contains(property.Name)
                || !seenProperties.Add(property.Name))
                || !new[] { "id", "workerPair", "scenario", "expectedChecks", "expectedCodes", "exitCode", "nextAction" }
                    .All(seenProperties.Contains)
                || seenProperties.Any(property => property != "terminalCategories"
                    && !new[] { "id", "workerPair", "scenario", "expectedChecks", "expectedCodes", "exitCode", "nextAction" }
                        .Contains(property, StringComparer.Ordinal)))
            {
                throw new InvalidDataException("A Durable doctor v1 matrix row has missing, duplicate, or unknown properties.");
            }

            var id = RequiredString(element, "id");
            var workerPair = RequiredBoolean(element, "workerPair");
            var scenario = RequiredString(element, "scenario");
            var checkStates = RequiredString(element, "expectedChecks").Split('/');
            var codes = RequiredStrings(element, "expectedCodes");
            var exitCode = RequiredInt(element, "exitCode");
            var nextAction = RequiredString(element, "nextAction");
            var terminalCategories = element.TryGetProperty("terminalCategories", out var categoryElement)
                ? ReadStringArray(categoryElement)
                : Array.Empty<string>();

            if (id.Length is < 1 or > 8
                || scenario.Length is < 1 or > 80
                || checkStates.Length != CheckOrder.Length
                || checkStates.Any(state => !CheckStates.Contains(state, StringComparer.Ordinal))
                || codes.Length > 12
                || codes.Any(static code => code.Length != 8 || !code.StartsWith("ASDUR", StringComparison.Ordinal))
                || exitCode is not (0 or 1 or 2 or 3 or 4)
                || !NextActions.Contains(nextAction, StringComparer.Ordinal)
                || terminalCategories.Length > 4
                || terminalCategories.Any(category => !TerminalCategories.Contains(category, StringComparer.Ordinal))
                || terminalCategories.Distinct(StringComparer.Ordinal).Count() != terminalCategories.Length)
            {
                throw new InvalidDataException($"Durable doctor v1 matrix row '{id}' is outside its fixed schema.");
            }

            rows.Add(new DurableDoctorMatrixRow(
                id,
                workerPair,
                scenario,
                Array.AsReadOnly(checkStates),
                Array.AsReadOnly(codes),
                exitCode,
                nextAction,
                Array.AsReadOnly(terminalCategories)));
        }

        if (rows.Count != 28
            || rows.Select(static row => row.Id).Distinct(StringComparer.Ordinal).Count() != rows.Count
            || rows.Select(static row => row.Scenario).Distinct(StringComparer.Ordinal).Count() != rows.Count)
        {
            throw new InvalidDataException("The Durable doctor v1 matrix must contain 28 uniquely identified scenarios.");
        }
        return Array.AsReadOnly(rows.ToArray());
    }

    private static bool CheckStateMapIsCurrent(JsonElement map)
    {
        if (map.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["P"] = "passed",
            ["F"] = "finding",
            ["NC"] = "not-checked",
            ["NR"] = "not-requested",
        };
        foreach (var property in map.EnumerateObject())
        {
            if (!expected.Remove(property.Name, out var value)
                || property.Value.ValueKind != JsonValueKind.String
                || property.Value.GetString() != value)
            {
                return false;
            }
        }
        return expected.Count == 0;
    }

    private static string RequiredString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new InvalidDataException($"Durable doctor v1 matrix property '{name}' must be a string.");

    private static bool RequiredBoolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw new InvalidDataException($"Durable doctor v1 matrix property '{name}' must be a boolean.");

    private static int RequiredInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? number
            : throw new InvalidDataException($"Durable doctor v1 matrix property '{name}' must be an integer.");

    private static string[] RequiredStrings(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? ReadStringArray(value)
            : throw new InvalidDataException($"Durable doctor v1 matrix property '{name}' must be present.");

    private static string[] ReadStringArray(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("A Durable doctor v1 matrix string list must be an array.");
        }
        return value.EnumerateArray().Select(static item => item.ValueKind == JsonValueKind.String
            ? item.GetString()!
            : throw new InvalidDataException("A Durable doctor v1 matrix list entry must be a string.")).ToArray();
    }
}

/// <summary>One deterministic diagnosis assertion row from the shared version-one matrix.</summary>
internal sealed record DurableDoctorMatrixRow(
    string Id,
    bool WorkerPair,
    string Scenario,
    IReadOnlyList<string> ExpectedChecks,
    IReadOnlyList<string> ExpectedCodes,
    int ExitCode,
    string NextAction,
    IReadOnlyList<string> TerminalCategories);
