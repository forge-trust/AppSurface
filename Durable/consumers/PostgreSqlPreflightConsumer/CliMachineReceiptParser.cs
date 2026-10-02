using System.Text.Json;
using System.Text.RegularExpressions;

internal static class CliMachineReceiptParser
{
    private const int MaximumOutputCharacters = 256 * 1024;
    private static readonly string[] ExpectedKeys =
    ["caller", "pair", "role", "owner", "runtimes", "manifest_sha256", "store_id", "active_epoch"];

    internal static ParsedReceipt Parse(string stdout)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        if (stdout.Length > MaximumOutputCharacters)
            throw new InvalidOperationException("Preflight output exceeds the bounded parser limit.");

        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Count(line => line.StartsWith("Compatible: durable schema ", StringComparison.Ordinal)) != 1)
            throw new InvalidOperationException("Preflight output did not confirm schema compatibility.");
        var receiptLines = lines.Where(line => line.StartsWith("caller=", StringComparison.Ordinal)).ToArray();
        if (receiptLines.Length != 1)
            throw new InvalidOperationException("Preflight output omitted or duplicated its machine receipt line.");

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var segment in SplitFields(receiptLines[0]))
        {
            var separator = segment.IndexOf('=');
            if (separator <= 0 || !fields.TryAdd(segment[..separator], segment[(separator + 1)..]))
                throw new InvalidOperationException("Preflight output contains a duplicate or malformed receipt field.");
        }
        if (!fields.Keys.SequenceEqual(ExpectedKeys, StringComparer.Ordinal))
            throw new InvalidOperationException("Preflight output fields do not match the frozen machine receipt format.");

        var caller = fields["caller"];
        var pairText = fields["pair"];
        var role = ParseJsonString(fields["role"]);
        var owner = ParseJsonString(fields["owner"]);
        var runtimesText = fields["runtimes"];
        var manifestHash = fields["manifest_sha256"];
        var storeText = fields["store_id"];
        var epochText = fields["active_epoch"];
        if (caller is not ("runtime" or "owner-diagnostic")
            || !Regex.IsMatch(manifestHash, "\\A[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)
            || !Guid.TryParseExact(storeText, "D", out var storeId) || storeId == Guid.Empty
            || !int.TryParse(runtimesText, out var runtimeCount) || runtimeCount is < 1 or > 32)
            throw new InvalidOperationException("Preflight output did not contain a complete valid machine receipt.");

        int? pair = pairText == "none" ? null
            : int.TryParse(pairText, out var parsedPair) && parsedPair is >= 1 and <= 32 ? parsedPair
            : throw new InvalidOperationException("Preflight output has an invalid pair number.");
        // The CLI retains a terminal sentence period; remove exactly that punctuation.
        var normalizedEpoch = epochText.EndsWith(".", StringComparison.Ordinal) ? epochText[..^1] : epochText;
        Guid? epoch = normalizedEpoch == "null" ? null
            : Guid.TryParseExact(normalizedEpoch, "D", out var parsedEpoch) && parsedEpoch != Guid.Empty ? parsedEpoch
            : throw new InvalidOperationException("Preflight output has an invalid active epoch.");

        if ((caller == "runtime" && (pair is null || string.IsNullOrEmpty(role)))
            || (caller == "owner-diagnostic" && pair is not null))
            throw new InvalidOperationException("Preflight caller evidence kind and pair identity contradict one another.");
        return new ParsedReceipt(caller, pair, role, owner, runtimeCount, manifestHash, storeId, epoch);
    }

    internal static void VerifySelfTest()
    {
        const string prefix = "Compatible: durable schema 11; runtime requires 11.\n";
        const string hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var valid = prefix + "caller=runtime; pair=1; role=\"rt;\\\"one\"; owner=\"owner\"; runtimes=2; manifest_sha256=" + hash
            + "; store_id=aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa; active_epoch=bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb.\n";
        var parsed = Parse(valid);
        if (parsed.Role != "rt;\"one" || parsed.Pair != 1 || parsed.RuntimeCount != 2)
            throw new InvalidOperationException("Valid quoted-semicolon receipt self-test failed.");
        var ownerOnly = Parse(prefix + "caller=owner-diagnostic; pair=none; role=\"owner\"; owner=\"owner\"; runtimes=2; manifest_sha256=" + hash
            + "; store_id=aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa; active_epoch=null.\n");
        if (ownerOnly.Caller != "owner-diagnostic" || ownerOnly.ActiveEpoch is not null)
            throw new InvalidOperationException("Valid owner-diagnostic receipt self-test failed.");

        var invalid = new[]
        {
            valid.Replace("Compatible:", "Incompatible:", StringComparison.Ordinal),
            valid.Replace("pair=1", "pair=null", StringComparison.Ordinal),
            valid.Replace("manifest_sha256=" + hash, "compatible=true; manifest_sha256=" + hash, StringComparison.Ordinal),
            valid.Replace("active_epoch=bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb.", "active_epoch=not-a-guid.", StringComparison.Ordinal),
            valid + "caller=runtime; pair=1; role=\"duplicate\"; owner=\"owner\"; runtimes=2; manifest_sha256=" + hash
                + "; store_id=aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa; active_epoch=bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb.\n"
        };
        foreach (var sample in invalid)
        {
            try { _ = Parse(sample); }
            catch (InvalidOperationException) { continue; }
            throw new InvalidOperationException("Invalid machine receipt unexpectedly passed the parser self-test.");
        }
    }

    private static string ParseJsonString(string value)
    {
        try { return JsonSerializer.Deserialize<string>(value) ?? throw new JsonException(); }
        catch (JsonException) { throw new InvalidOperationException("Preflight output contains an invalid JSON-escaped identity."); }
    }

    private static IReadOnlyList<string> SplitFields(string line)
    {
        var fields = new List<string>();
        var start = 0;
        var inString = false;
        var escaped = false;
        for (var index = 0; index < line.Length; index++)
        {
            var current = line[index];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (current == '\\') escaped = true;
                else if (current == '"') inString = false;
            }
            else if (current == '"') inString = true;
            else if (current == ';')
            {
                fields.Add(line[start..index].Trim());
                start = index + 1;
                if (start < line.Length && line[start] == ' ') start++;
            }
        }
        if (inString || escaped) throw new InvalidOperationException("Preflight output contains an incomplete quoted identity.");
        fields.Add(line[start..].Trim());
        return fields;
    }
}
