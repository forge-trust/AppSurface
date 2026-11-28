using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Validation;

/// <summary>Independent input, JSON and byte assertions used by native acceptance producers.</summary>
internal static class ValidationProof
{
    /// <summary>Identifies failures raised by the validator's own contract checks.</summary>
    internal const string ContractFailurePrefix = "Native validation contract failed: ";

    /// <summary>Rejects a failed contract without copying untrusted output into a diagnostic.</summary>
    internal static void Require(bool condition, string contract)
    {
        if (!condition) throw new InvalidOperationException(ContractFailurePrefix + contract);
    }

    /// <summary>Checks the verdict, tolerance, and configured and effective overall thresholds in a CLI gate report.</summary>
    /// <param name="gate">The already-parsed root object of the candidate gate report.</param>
    /// <param name="requirements">The caller's expected overall line, branch, and tolerance values.</param>
    /// <param name="contract">An optional failure label shared by all checks. The default labels identify individual failures.</param>
    /// <remarks>Callers retain responsibility for report custody and any patch coverage requirements.</remarks>
    internal static void RequireOverallCoverageGate(JsonElement gate, EvidenceCoverageGateRequirements requirements, string? contract = null)
    {
        Require(gate.GetProperty("passed").GetBoolean(), contract ?? "coverage gate passed");
        var tolerance = requirements.TolerancePercent;
        Require(gate.GetProperty("tolerancePercent").GetDecimal() == tolerance, contract ?? "coverage gate zero tolerance");
        Require(MatchesThresholds(gate.GetProperty("thresholds"), requirements.MinLinePercent, requirements.MinBranchPercent),
            contract ?? "coverage gate thresholds");
        Require(MatchesThresholds(gate.GetProperty("effectiveThresholds"),
                Math.Max(0m, requirements.MinLinePercent - tolerance), Math.Max(0m, requirements.MinBranchPercent - tolerance)),
            contract ?? "coverage gate effective thresholds");

        static bool MatchesThresholds(JsonElement thresholds, decimal line, decimal branch)
            => thresholds.GetProperty("line").GetDecimal() == line && thresholds.GetProperty("branch").GetDecimal() == branch;
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
        var parsedMetadata = PackedCandidateArchive.ReadBoundedMetadata(specification);
        Require(parsedMetadata is not null, "bounded package specification");
        var metadata = parsedMetadata!;
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

    /// <summary>Reads a candidate-produced JSON file with a cumulative byte limit.</summary>
    internal static JsonDocument ReadJsonFile(string path, long maximumBytes, CancellationToken cancellationToken)
    {
        if (maximumBytes <= 0 || maximumBytes > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(maximumBytes));

        var info = new FileInfo(path);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length <= 0 || info.Length > maximumBytes)
            throw new InvalidDataException("Candidate JSON is missing or exceeds its size limit.");

        var bytes = ReadBoundedFile(path, (int)maximumBytes, cancellationToken);
        try
        {
            return JsonDocument.Parse(bytes);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Candidate JSON is invalid.", exception);
        }
    }

    /// <summary>Captures a local file up to an inclusive byte limit and closes it before returning.</summary>
    /// <param name="path">The source file. Callers apply their own file admission rules.</param>
    /// <param name="maximumBytes">Maximum number of bytes that may be captured.</param>
    /// <param name="cancellationToken">Cancels the capture before opening or between reads.</param>
    /// <returns>The original file bytes.</returns>
    internal static ReadOnlyMemory<byte> ReadBoundedFile(string path, int maximumBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        return ReadBoundedBytes(input, maximumBytes, cancellationToken);
    }

    /// <summary>Captures the original bytes up to an inclusive limit while leaving the stream open.</summary>
    /// <param name="input">Readable input at the start of the data to capture.</param>
    /// <param name="maximumBytes">Maximum number of bytes that may be captured.</param>
    /// <param name="cancellationToken">Cancels the capture between reads.</param>
    /// <returns>The original bytes without parsing or normalizing them.</returns>
    /// <exception cref="InvalidDataException">The input exceeds the size limit.</exception>
    internal static ReadOnlyMemory<byte> ReadBoundedBytes(Stream input, int maximumBytes, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        cancellationToken.ThrowIfCancellationRequested();
        var length = input.CanSeek ? input.Length - input.Position : 0;
        if (length > maximumBytes)
            throw new InvalidDataException("Evidence input exceeds its size limit.");

        using var bytes = new MemoryStream((int)Math.Max(0, length));
        var buffer = new byte[Math.Min(64 * 1024, maximumBytes)];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = maximumBytes - bytes.Length;
            var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining + 1));
            if (read == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return bytes.GetBuffer().AsMemory(0, (int)bytes.Length);
            }

            if (read > remaining)
                throw new InvalidDataException("Evidence input exceeds its size limit.");
            bytes.Write(buffer, 0, read);
        }
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
