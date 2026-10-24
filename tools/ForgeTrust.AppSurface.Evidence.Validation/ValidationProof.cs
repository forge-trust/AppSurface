using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace ForgeTrust.AppSurface.Evidence.Validation;

/// <summary>Independent input, JSON and byte assertions used by native acceptance producers.</summary>
internal static class ValidationProof
{
    /// <summary>Rejects a failed contract without copying untrusted output into a diagnostic.</summary>
    internal static void Require(bool condition, string contract)
    {
        if (!condition) throw new InvalidOperationException("Native validation contract failed: " + contract);
    }

    /// <summary>Computes a file digest without buffering the complete file.</summary>
    internal static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    /// <summary>Validates the package ID, exact version, and trusted source commit.</summary>
    internal static void VerifyPackage(ZipArchive archive, ValidationSettings settings)
    {
        var specification = archive.Entries.Single(entry => entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
        Require(specification.Length <= 1024 * 1024, "bounded package specification");
        using var contents = specification.Open();
        using var reader = XmlReader.Create(contents, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var metadata = XDocument.Load(reader).Root!.Elements().Single(e => e.Name.LocalName == "metadata");
        string Value(string name) => metadata.Elements().Single(e => e.Name.LocalName == name).Value;
        Require(Value("id") == "ForgeTrust.AppSurface.Cli", "candidate package ID");
        Require(Value("version") == settings.PackageVersion, "candidate package version");
        var repository = metadata.Elements().Single(e => e.Name.LocalName == "repository");
        Require((string?)repository.Attribute("commit") == settings.ExpectedSourceCommit, "candidate source commit");
    }

    /// <summary>Reads the CLI JSON data envelope while rejecting a non-object response.</summary>
    internal static JsonElement Data(string output)
    {
        using var json = JsonDocument.Parse(output);
        return json.RootElement.GetProperty("data").Clone();
    }

    /// <summary>Inventories original bytes, excluding transient lock files only.</summary>
    internal static async Task<SortedDictionary<string, string>> InventoryAsync(string root, CancellationToken cancellationToken)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (file.EndsWith(".lock", StringComparison.Ordinal)) continue;
            Require(!File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint), "non-linked fixture input");
            result.Add(Path.GetRelativePath(root, file).Replace('\\', '/'), await HashAsync(file, cancellationToken));
        }
        return result;
    }

    /// <summary>Compares complete path and hash inventories.</summary>
    internal static bool Equal(IReadOnlyDictionary<string, string> first, IReadOnlyDictionary<string, string> second) =>
        first.Count == second.Count && first.All(pair => second.TryGetValue(pair.Key, out var digest) && digest == pair.Value);
}
