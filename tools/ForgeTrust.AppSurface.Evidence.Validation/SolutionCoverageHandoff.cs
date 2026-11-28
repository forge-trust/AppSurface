using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    private const int CompilerSymbolMaximumBytes = 1024 * 1024;
    private static readonly string[] RunKeys = ["GITHUB_REPOSITORY", "GITHUB_RUN_ID", "GITHUB_RUN_ATTEMPT"];
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
        var compilerSymbols = includeKeycloakNativeCoverage
            ? await ReadCompilerSymbolHashesAsync(coverage, cancellationToken).ConfigureAwait(false)
            : null;
        var receipt = new Receipt(includeKeycloakNativeCoverage ? 2 : 1, identity, "Release", run,
            await DigestAsync(Path.Join(coverage, "coverage.cobertura.xml"), cancellationToken).ConfigureAwait(false),
            await DigestAsync(Path.Join(coverage, "timings.json"), cancellationToken).ConfigureAwait(false), compilerSymbols);
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
        foreach (var name in CompilerSymbolFileNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = PathUtils.PathUnder(sourceDirectory, name);
            var sourceSha256 = await DigestCompilerSymbolAsync(source, cancellationToken).ConfigureAwait(false);
            var destination = PathUtils.PathUnder(destinationDirectory, name);
            File.Copy(source, destination, overwrite: false);
            if (await DigestCompilerSymbolAsync(destination, cancellationToken).ConfigureAwait(false) != sourceSha256)
                throw new InvalidDataException("A staged primary compiler-symbol file changed while copying.");
        }
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
        if (receipt.SchemaVersion != (requireKeycloakNativeCoverage ? 2 : 1)
            || receipt.Identity != identity || receipt.Configuration != "Release"
            || receipt.Run is null || receipt.Run.Count != RunKeys.Length
            || RunKeys.Any(key => !receipt.Run.TryGetValue(key, out var value) || value != run[key]))
            throw new InvalidDataException("Coverage collection does not match this source, SDK or workflow attempt.");
        if (requireKeycloakNativeCoverage != (receipt.CompilerSymbols is not null))
            throw new InvalidDataException("Coverage receipt compiler symbols do not match the selected profile.");
        if (receipt.CompilerSymbols is { } compilerSymbols)
            await VerifyCompilerSymbolsAsync(input, compilerSymbols, cancellationToken).ConfigureAwait(false);
        else if (Path.Exists(PathUtils.PathUnder(input, CompilerSymbolsDirectoryName)))
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
        if (receipt.CompilerSymbols is { } importedSymbols)
            await VerifyCompilerSymbolsAsync(destination, importedSymbols, cancellationToken).ConfigureAwait(false);
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

    private static async Task<Dictionary<string, string>> ReadCompilerSymbolHashesAsync(string coverage, CancellationToken token)
    {
        var directory = PathUtils.PathUnder(coverage, CompilerSymbolsDirectoryName);
        RejectLinks(directory);
        var entries = Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName).ToArray();
        if (entries.Length != CompilerSymbolFileNames.Length
            || !entries.ToHashSet(StringComparer.Ordinal).SetEquals(CompilerSymbolFileNames))
            throw new InvalidDataException("Primary compiler-symbol directory does not contain the exact required file set.");

        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in CompilerSymbolFileNames)
            hashes.Add(name, await DigestCompilerSymbolAsync(PathUtils.PathUnder(directory, name), token).ConfigureAwait(false));
        return hashes;
    }

    private static async Task VerifyCompilerSymbolsAsync(string coverage, Dictionary<string, string> expected, CancellationToken token)
    {
        var actual = await ReadCompilerSymbolHashesAsync(coverage, token).ConfigureAwait(false);
        if (expected.Count != actual.Count || actual.Any(entry => expected.GetValueOrDefault(entry.Key) != entry.Value))
            throw new InvalidDataException("Primary compiler-symbol hashes differ from the collection receipt.");
    }

    private static Task<string> DigestCompilerSymbolAsync(string path, CancellationToken token)
    {
        RejectLinks(path);
        if (new FileInfo(path).Length > CompilerSymbolMaximumBytes)
            throw new InvalidDataException("Primary compiler-symbol file exceeds the per-file byte budget.");
        return DigestAsync(path, token);
    }

    private static string[] CreateCompilerSymbolFileNames() => KeycloakCoverageCompilerInputs.AssemblyPaths(Path.GetTempPath())
        .SelectMany(assembly => new[] { Path.GetFileName(assembly), Path.GetFileName(Path.ChangeExtension(assembly, ".pdb")) })
        .ToArray();

    private sealed record Receipt(int SchemaVersion, SolutionCoverageIdentity Identity, string Configuration,
        IReadOnlyDictionary<string, string?> Run, string ReportSha256, string TimingsSha256,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Dictionary<string, string>? CompilerSymbols = null);
}
