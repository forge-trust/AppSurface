using System.Security.Cryptography;
using System.Text.Json;
using ForgeTrust.AppSurface.Core;

namespace ForgeTrust.AppSurface.Evidence.Validation;

/// <summary>The source and SDK identity shared by collection and final coverage acceptance.</summary>
/// <param name="Commit">Exact 40-digit Git commit checked before and after collection.</param>
/// <param name="Tree">Exact 40-digit Git tree for that commit.</param>
/// <param name="Sdk">Selected SDK version, required to match final gate execution.</param>
internal sealed record SolutionCoverageIdentity(string Commit, string Tree, string Sdk)
{
    /// <summary>Rejects missing or malformed identities before publishing a collection receipt.</summary>
    internal void Validate()
    {
        if (Commit.Length != 40 || Tree.Length != 40 || !Commit.All(Uri.IsHexDigit) || !Tree.All(Uri.IsHexDigit)
            || string.IsNullOrWhiteSpace(Sdk) || Sdk.Length > 64)
            throw new InvalidDataException("Coverage collection identity is invalid.");
    }
}

/// <summary>
/// Transfers successful collection between trusted jobs in one workflow attempt. This is consistency checking,
/// not authentication: the workflow must download its own attempt's artifact, never a cache or another run's report.
/// Timing history is separate advisory input and cannot supply this handoff.
/// </summary>
internal static class SolutionCoverageHandoff
{
    private const string ReceiptName = "collection-receipt.json";
    private const string CompilerSymbolsDirectoryName = "compiler-symbols";
    private const string CompilerSymbolsInventoryName = "inventory.json";
    private static readonly string[] RunKeys = ["GITHUB_REPOSITORY", "GITHUB_RUN_ID", "GITHUB_RUN_ATTEMPT"];
    private static readonly JsonSerializerOptions CompilerSymbolsJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] CompilerSymbolFileNames = CreateCompilerSymbolFileNames();

    /// <summary>Requires repository, workflow run and attempt identity for both sides of the handoff.</summary>
    internal static void RequireRun(IReadOnlyDictionary<string, string?> run)
    {
        if (RunKeys.Any(key => !run.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value)))
            throw new InvalidDataException("Coverage collection requires complete workflow provenance.");
    }

    /// <summary>Publishes a receipt only after successful complete collection and an unchanged-source check.</summary>
    /// <exception cref="InvalidDataException">Required collection inputs are missing, empty or linked.</exception>
    internal static async Task WriteAsync(string coverage, SolutionCoverageIdentity identity,
        IReadOnlyDictionary<string, string?> run, CancellationToken cancellationToken,
        bool includeKeycloakNativeCoverage = false)
    {
        identity.Validate();
        RequireRun(run);
        var compilerSymbolsSha256 = includeKeycloakNativeCoverage
            ? await ValidateCompilerSymbolsAsync(PathUtils.PathUnder(coverage, CompilerSymbolsDirectoryName), null, cancellationToken).ConfigureAwait(false)
            : null;
        var receipt = new Receipt(1, identity, "Release", run,
            await DigestAsync(Path.Join(coverage, "coverage.cobertura.xml"), cancellationToken).ConfigureAwait(false),
            await DigestAsync(Path.Join(coverage, "timings.json"), cancellationToken).ConfigureAwait(false), compilerSymbolsSha256);
        await using var output = new FileStream(Path.Join(coverage, ReceiptName), FileMode.CreateNew, FileAccess.Write);
        await JsonSerializer.SerializeAsync(output, receipt, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Copies only the three fixed PE/PDB pairs from the completed Keycloak adapter test run.</summary>
    internal static async Task StageCompilerSymbolsAsync(string root, string coverage, string configuration,
        CancellationToken cancellationToken)
    {
        var sourceDirectory = KeycloakCoverageCompilerInputs.TestOutput(root, configuration);
        var destinationDirectory = PathUtils.PathUnder(coverage, CompilerSymbolsDirectoryName);
        RejectLinks(sourceDirectory);
        if (Directory.Exists(destinationDirectory) || File.Exists(destinationDirectory))
            throw new InvalidDataException("Primary compiler-symbol output must be fresh.");

        Directory.CreateDirectory(destinationDirectory);
        var files = new List<CompilerSymbolFile>(CompilerSymbolFileNames.Length);
        foreach (var assembly in KeycloakCoverageCompilerInputs.AssemblyPaths(sourceDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pair = new[] { assembly, Path.ChangeExtension(assembly, ".pdb") };
            foreach (var source in pair)
            {
                RejectLinks(source);
                if (Directory.Exists(source))
                    throw new InvalidDataException("Primary compiler-symbol inputs must be files.");
                var fileName = Path.GetFileName(source);
                var sourceSha256 = await DigestAsync(source, cancellationToken).ConfigureAwait(false);
                var destination = PathUtils.PathUnder(destinationDirectory, fileName);
                File.Copy(source, destination, overwrite: false);
                if (await DigestAsync(destination, cancellationToken).ConfigureAwait(false) != sourceSha256)
                    throw new InvalidDataException("A staged primary compiler-symbol file changed while copying.");
                files.Add(new CompilerSymbolFile(fileName, sourceSha256));
            }
        }

        var inventory = new CompilerSymbolInventory(1, files);
        var inventoryPath = PathUtils.PathUnder(destinationDirectory, CompilerSymbolsInventoryName);
        await File.WriteAllBytesAsync(inventoryPath,
            JsonSerializer.SerializeToUtf8Bytes(inventory, CompilerSymbolsJsonOptions), cancellationToken).ConfigureAwait(false);
        _ = await ValidateCompilerSymbolsAsync(destinationDirectory, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Copies an unlinked collection into fresh producer output, matching source, SDK, configuration, run, attempt
    /// and the report/timings digests. The producer must independently verify inventory and all coverage gates afterward.
    /// </summary>
    /// <exception cref="InvalidDataException">Collection inputs are missing, malformed, linked or inconsistent.</exception>
    internal static async Task ImportAsync(string collection, string coverage, SolutionCoverageIdentity identity,
        IReadOnlyDictionary<string, string?> run, CancellationToken cancellationToken,
        bool requireKeycloakNativeCoverage = false)
    {
        identity.Validate();
        RequireRun(run);
        var input = Path.Join(Path.GetFullPath(collection), "coverage");
        RejectLinks(input);
        var receiptPath = Path.Join(input, ReceiptName);
        RejectLinks(receiptPath);
        if (Directory.Exists(receiptPath))
            throw new InvalidDataException("Coverage collection receipt must be a file.");
        if (new FileInfo(receiptPath).Length is <= 0 or > 16384)
            throw new InvalidDataException("Coverage collection receipt is missing or oversized.");
        Receipt receipt;
        try
        {
            receipt = JsonSerializer.Deserialize<Receipt>(await File.ReadAllBytesAsync(receiptPath, cancellationToken).ConfigureAwait(false))
                ?? throw new InvalidDataException("Coverage collection receipt is invalid.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Coverage collection receipt is invalid.", exception);
        }
        if (receipt.SchemaVersion != 1 || receipt.Identity != identity || receipt.Configuration != "Release"
            || receipt.Run is null || receipt.Run.Count != RunKeys.Length
            || RunKeys.Any(key => !receipt.Run.TryGetValue(key, out var value) || value != run[key]))
            throw new InvalidDataException("Coverage collection does not match this source, SDK or workflow attempt.");
        if (requireKeycloakNativeCoverage && receipt.CompilerSymbolsInventorySha256 is null)
            throw new InvalidDataException("Keycloak coverage collection is missing its primary compiler-symbol inventory.");
        var compilerSymbols = PathUtils.PathUnder(input, CompilerSymbolsDirectoryName);
        if (receipt.CompilerSymbolsInventorySha256 is { } compilerSymbolsSha256)
            await ValidateCompilerSymbolsAsync(compilerSymbols, compilerSymbolsSha256, cancellationToken).ConfigureAwait(false);
        else if (Directory.Exists(compilerSymbols) || File.Exists(compilerSymbols))
            throw new InvalidDataException("Coverage collection has unbound primary compiler-symbol inputs.");
        var destination = Path.GetFullPath(coverage);
        // Reject case aliases conservatively even on case-sensitive hosts; artifact output never needs them.
        if (destination.Equals(input, StringComparison.OrdinalIgnoreCase)
            || destination.StartsWith(input + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || input.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any()))
            throw new InvalidDataException("Coverage collection requires disjoint fresh output.");
        CopyDirectory(input, destination, cancellationToken);
        if (await DigestAsync(Path.Join(destination, "coverage.cobertura.xml"), cancellationToken).ConfigureAwait(false) != receipt.ReportSha256
            || await DigestAsync(Path.Join(destination, "timings.json"), cancellationToken).ConfigureAwait(false) != receipt.TimingsSha256)
            throw new InvalidDataException("Coverage collection report or timings were altered.");
        if (receipt.CompilerSymbolsInventorySha256 is { } importedSymbolsSha256)
            await ValidateCompilerSymbolsAsync(PathUtils.PathUnder(destination, CompilerSymbolsDirectoryName), importedSymbolsSha256,
                cancellationToken).ConfigureAwait(false);
    }

    private static void CopyDirectory(string source, string destination, CancellationToken cancellationToken)
    {
        RejectLinks(source);
        Directory.CreateDirectory(destination);
        RejectLinks(destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectLinks(entry);
            var target = Path.Join(destination, Path.GetFileName(entry));
            if (Directory.Exists(entry)) CopyDirectory(entry, target, cancellationToken);
            else File.Copy(entry, target, overwrite: false);
        }
    }

    private static void RejectLinks(string path)
    {
        try
        {
            for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException("Coverage collection paths cannot contain links.");
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new InvalidDataException("Coverage collection input is missing.", exception);
        }
    }

    private static async Task<string> DigestAsync(string path, CancellationToken cancellationToken)
    {
        RejectLinks(path);
        if (Directory.Exists(path))
            throw new InvalidDataException("Coverage collection input must be a file.");
        await using var input = File.OpenRead(path);
        if (input.Length == 0) throw new InvalidDataException("Coverage collection input is empty.");
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<string> ValidateCompilerSymbolsAsync(string directory, string? expectedInventorySha256,
        CancellationToken cancellationToken)
    {
        RejectLinks(directory);
        if (!Directory.Exists(directory))
            throw new InvalidDataException("Primary compiler-symbol directory is missing.");
        var expectedEntries = CompilerSymbolFileNames.Append(CompilerSymbolsInventoryName).Order(StringComparer.Ordinal).ToArray();
        var actualEntries = Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        if (!actualEntries.SequenceEqual(expectedEntries, StringComparer.Ordinal))
            throw new InvalidDataException("Primary compiler-symbol directory does not contain the exact required file set.");

        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            RejectLinks(entry);
            if (Directory.Exists(entry))
                throw new InvalidDataException("Primary compiler-symbol entries must be files.");
        }

        var inventoryPath = PathUtils.PathUnder(directory, CompilerSymbolsInventoryName);
        var inventoryBytes = await ReadBoundedFileAsync(inventoryPath, 16384, cancellationToken).ConfigureAwait(false);
        var inventorySha256 = Convert.ToHexStringLower(SHA256.HashData(inventoryBytes));
        if (expectedInventorySha256 is not null && inventorySha256 != expectedInventorySha256)
            throw new InvalidDataException("Primary compiler-symbol inventory differs from the collection receipt.");

        using var document = JsonDocument.Parse(inventoryBytes);
        var inventory = document.RootElement;
        if (!HasExactProperties(inventory, "schemaVersion", "files")
            || inventory.GetProperty("schemaVersion").ValueKind != JsonValueKind.Number
            || inventory.GetProperty("schemaVersion").GetInt32() != 1
            || inventory.GetProperty("files").ValueKind != JsonValueKind.Array
            || inventory.GetProperty("files").GetArrayLength() != CompilerSymbolFileNames.Length)
            throw new InvalidDataException("Primary compiler-symbol inventory schema is invalid.");

        var fileEntries = inventory.GetProperty("files").EnumerateArray().ToArray();
        for (var index = 0; index < CompilerSymbolFileNames.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileEntry = fileEntries[index];
            var fileName = CompilerSymbolFileNames[index];
            if (!HasExactProperties(fileEntry, "path", "sha256")
                || fileEntry.GetProperty("path").ValueKind != JsonValueKind.String
                || fileEntry.GetProperty("path").GetString() != fileName
                || fileEntry.GetProperty("sha256").ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Primary compiler-symbol inventory file entry is invalid.");

            var expectedFileSha256 = fileEntry.GetProperty("sha256").GetString();
            if (expectedFileSha256 is null || !IsSha256(expectedFileSha256)
                || await DigestAsync(PathUtils.PathUnder(directory, fileName), cancellationToken).ConfigureAwait(false) != expectedFileSha256)
                throw new InvalidDataException("Primary compiler-symbol file hash does not match its inventory.");
        }
        return inventorySha256;
    }

    private static async Task<byte[]> ReadBoundedFileAsync(string path, int maximumBytes, CancellationToken cancellationToken)
    {
        RejectLinks(path);
        if (Directory.Exists(path))
            throw new InvalidDataException("Primary compiler-symbol inventory must be a file.");
        var info = new FileInfo(path);
        if (info.Length is <= 0 || info.Length > maximumBytes)
            throw new InvalidDataException("Primary compiler-symbol inventory is missing, empty or oversized.");
        return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private static bool HasExactProperties(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        var actual = element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
        return actual.SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal);
    }

    private static bool IsSha256(string value) => value.Length == 64
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string[] CreateCompilerSymbolFileNames() => KeycloakCoverageCompilerInputs.AssemblyPaths(Path.GetTempPath())
        .SelectMany(assembly => new[] { Path.GetFileName(assembly), Path.GetFileName(Path.ChangeExtension(assembly, ".pdb")) })
        .ToArray();

    private sealed record Receipt(int SchemaVersion, SolutionCoverageIdentity Identity, string Configuration,
        IReadOnlyDictionary<string, string?> Run, string ReportSha256, string TimingsSha256,
        string? CompilerSymbolsInventorySha256 = null);
    private sealed record CompilerSymbolInventory(int SchemaVersion, IReadOnlyList<CompilerSymbolFile> Files);
    private sealed record CompilerSymbolFile(string Path, string Sha256);
}
