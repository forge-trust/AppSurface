using System.Text.Json;
using System.Text.Json.Nodes;

namespace ForgeTrust.AppSurface.PackageIndex;

/// <summary>
/// Materializes the reviewed <c>net10.0</c> native-consumer NuGet lock before restore. The checked-in template
/// fixes every third-party entry, including its version, hash, and dependencies. Only producer-bound first-party
/// versions and archive hashes may change for a release candidate.
/// </summary>
internal static class TailwindConsumerLock
{
    internal const string RelativeTemplatePath = "tools/ForgeTrust.AppSurface.PackageIndex/tailwind-native-consumer.lock.json";
    private const int MaxLockBytes = 16 * 1024 * 1024;

    /// <summary>Write a candidate lock from the reviewed template and exact producer package identities.</summary>
    /// <exception cref="PackageIndexException">The template, candidate closure, or output is unsafe or inconsistent.</exception>
    internal static byte[] Materialize(string templatePath, string outputPath, IReadOnlyList<TailwindSubjectPackage> packages)
    {
        var bytes = ReadRegularBounded(templatePath);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        RejectDuplicates(document.RootElement);
        var root = JsonNode.Parse(bytes) as JsonObject
            ?? throw new PackageIndexException("Native consumer lock template must be a JSON object.");
        RequireFields(root, ["version", "dependencies"], "root");
        if (root["version"]?.GetValue<int>() != 1)
            throw new PackageIndexException("Native consumer lock template must use NuGet lock version 1.");
        var frameworks = root["dependencies"] as JsonObject
            ?? throw new PackageIndexException("Native consumer lock template has no dependency graph.");
        RequireFields(frameworks, ["net10.0"], "frameworks");
        var graph = frameworks["net10.0"] as JsonObject
            ?? throw new PackageIndexException("Native consumer lock template has no net10.0 graph.");
        if (packages.Count == 0 || packages.Select(package => package.PackageId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != packages.Count)
            throw new PackageIndexException("Producer first-party closure is empty or contains duplicate identities.");
        var packageById = packages.ToDictionary(package => package.PackageId, StringComparer.OrdinalIgnoreCase);
        var templateFirstParty = graph.Select(item => item.Key).Where(id => id.StartsWith("ForgeTrust.", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (!templateFirstParty.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(packageById.Keys))
            throw new PackageIndexException("Native consumer lock template first-party closure differs from the producer subject; update the reviewed lock template.");

        foreach (var (id, value) in graph)
        {
            var entry = value as JsonObject
                ?? throw new PackageIndexException($"Native consumer lock entry '{id}' must be an object.");
            RequireFields(entry, entry.ContainsKey("requested") && entry.ContainsKey("dependencies")
                ? ["type", "requested", "resolved", "contentHash", "dependencies"]
                : entry.ContainsKey("requested") ? ["type", "requested", "resolved", "contentHash"]
                : entry.ContainsKey("dependencies") ? ["type", "resolved", "contentHash", "dependencies"]
                : ["type", "resolved", "contentHash"], id);
            var hash = entry["contentHash"]?.GetValue<string>();
            if (hash is null || !TryDecodeSha512(hash))
                throw new PackageIndexException($"Native consumer lock entry '{id}' has an invalid SHA-512 contentHash.");
            if (entry["resolved"]?.GetValue<string>() is not { Length: > 0 })
                throw new PackageIndexException($"Native consumer lock entry '{id}' has no resolved version.");
            var dependencies = entry["dependencies"] as JsonObject;
            if (entry.ContainsKey("dependencies") && dependencies is null)
                throw new PackageIndexException($"Native consumer lock entry '{id}' has invalid dependencies.");
            if (dependencies is not null && dependencies.Any(dependency => dependency.Value is not JsonValue))
                throw new PackageIndexException($"Native consumer lock entry '{id}' has an invalid dependency version.");

            if (packageById.TryGetValue(id, out var package))
            {
                var direct = id.Equals("ForgeTrust.AppSurface.Web.Tailwind", StringComparison.OrdinalIgnoreCase);
                if (entry["type"]?.GetValue<string>() != (direct ? "Direct" : "Transitive") || direct != entry.ContainsKey("requested"))
                    throw new PackageIndexException($"Native consumer lock first-party entry '{id}' has an unexpected dependency type.");
                entry["resolved"] = package.PackageVersion;
                entry["contentHash"] = Convert.ToBase64String(Convert.FromHexString(package.PackageSha512));
                if (direct) entry["requested"] = $"[{package.PackageVersion}, )";
            }
            else if (entry["type"]?.GetValue<string>() != "Transitive" || entry.ContainsKey("requested")
                || !(id.Equals("CliWrap", StringComparison.OrdinalIgnoreCase)
                    || id.Equals("System.Diagnostics.EventLog", StringComparison.OrdinalIgnoreCase)
                    || id.StartsWith("Microsoft.Extensions.", StringComparison.OrdinalIgnoreCase)))
                throw new PackageIndexException($"Native consumer lock contains an unreviewed third-party entry '{id}'.");

            if (dependencies is null) continue;
            foreach (var (dependencyId, dependencyValue) in dependencies.ToArray())
            {
                if (dependencyValue?.GetValue<string>() is not { Length: > 0 })
                    throw new PackageIndexException($"Native consumer lock entry '{id}' has an empty dependency version.");
                if (!dependencyId.StartsWith("ForgeTrust.", StringComparison.OrdinalIgnoreCase)) continue;
                if (!packageById.TryGetValue(dependencyId, out var dependency))
                    throw new PackageIndexException($"Native consumer lock entry '{id}' references an unbound first-party dependency.");
                dependencies[dependencyId] = dependency.PackageVersion;
            }
        }
        var result = System.Text.Encoding.UTF8.GetBytes(root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        File.WriteAllBytes(outputPath, result);
        return result;
    }

    /// <summary>Reject a lock rewritten, removed, or linked by either restore before it becomes host evidence.</summary>
    internal static void RequireUnchanged(string path, byte[] expected)
    {
        if (!ReadRegularBounded(path).AsSpan().SequenceEqual(expected))
            throw new PackageIndexException("Native consumer packages.lock.json changed during locked restore.");
    }

    private static byte[] ReadRegularBounded(string path)
    {
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new PackageIndexException($"Native consumer lock is missing or is not a regular file: '{path}'.");
        var info = new FileInfo(path);
        if (info.Length > MaxLockBytes) throw new PackageIndexException("Native consumer lock exceeds the 16 MiB JSON limit.");
        return File.ReadAllBytes(path);
    }

    private static bool TryDecodeSha512(string hash)
    {
        Span<byte> decoded = stackalloc byte[64];
        return Convert.TryFromBase64String(hash, decoded, out var written) && written == 64;
    }

    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new PackageIndexException($"Native consumer lock has duplicate JSON property '{property.Name}'.");
                RejectDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var child in value.EnumerateArray()) RejectDuplicates(child);
    }

    private static void RequireFields(JsonObject value, IReadOnlyCollection<string> expected, string context)
    {
        if (!value.Select(item => item.Key).ToHashSet(StringComparer.Ordinal).SetEquals(expected))
            throw new PackageIndexException($"Native consumer lock {context} has missing or unexpected fields.");
    }
}
