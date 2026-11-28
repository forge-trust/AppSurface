using System.Globalization;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Bounded transport rules for compiler assemblies and their adjacent portable PDBs.</summary>
internal static class CoverageFanInCompilerSymbols
{
    internal const int MaximumAssemblies = 8;
    internal const string ReportProfileId = "coverlet-collector-cobertura-async-v1";
    private const string FilePrefix = "compiler-";

    internal static bool IsEnabled(CoverageFanInExpected expected) =>
        expected.ContractVersion == 3
        && expected.Policy.TryGetProperty("reportProfileId", out var profile)
        && StringComparer.Ordinal.Equals(profile.GetString(), ReportProfileId);

    internal static string PdbPath(string assemblyPath) => Path.ChangeExtension(assemblyPath, ".pdb");

    internal static void ValidateRequest(string operation, CoverageFanInEngineRequest request)
    {
        var assemblies = request.CompilerSymbolAssemblies;
        if (assemblies is { Count: 0 })
            throw new ArgumentException("Compiler symbol inventory cannot be empty when supplied.");
        var hasAssemblies = assemblies is { Count: > 0 };
        var enabled = IsEnabled(request.Expected);
        if (assemblies is { Count: > MaximumAssemblies })
            throw new ArgumentException("Compiler symbol inventory exceeds its bound.");
        if (hasAssemblies && (!enabled || operation is not ("capture" or "collect")))
            throw new ArgumentException("Compiler symbols require the async report profile on capture or collection.");
        if (hasAssemblies && operation == "capture" && request.ImportDirectory is not null)
            throw new ArgumentException("Imported compiler symbols must come from the verified payload.");

        if (hasAssemblies)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var assembly in assemblies!)
            {
                if (string.IsNullOrWhiteSpace(assembly) || !Path.IsPathFullyQualified(assembly)
                    || !StringComparer.OrdinalIgnoreCase.Equals(Path.GetExtension(assembly), ".dll"))
                    throw new ArgumentException("Compiler symbol assemblies must be absolute DLL paths.");
                var full = CoverageRunOutputLease.NormalizePlatformPath(Path.GetFullPath(assembly));
                if (!seen.Add(full)) throw new ArgumentException("Compiler symbol assembly paths must be unique.");
            }
        }

        if (enabled && (operation is "capture" or "collect") && request.ProducerId == "primary"
            && request.ImportDirectory is null && !hasAssemblies)
            throw new ArgumentException("Async-profile primary capture requires compiler symbols.");
    }

    internal static Dictionary<string, byte[]> ReadPayload(IReadOnlyList<string> assemblies, CoverageFanInIO io,
        string sourceRoot)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        for (var index = 0; index < assemblies.Count; index++)
        {
            var path = assemblies[index];
            files.Add(AssemblyFileName(index), io.Read(path));
            files.Add(PdbFileName(index), io.Read(PdbPath(path)));
        }
        _ = ReadMethods(FileNames(assemblies.Count), files, sourceRoot);
        return files;
    }

    internal static string[] RetainedFileNames(JsonElement files)
    {
        if (files.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Capture file inventory is invalid.");
        var candidates = files.EnumerateObject().Select(item => item.Name)
            .Where(name => name.StartsWith(FilePrefix, StringComparison.Ordinal)).ToArray();
        if (candidates.Length == 0) return [];
        if (candidates.Length > MaximumAssemblies * 2 || candidates.Length % 2 != 0)
            throw new InvalidDataException("Compiler symbol file inventory is incomplete or exceeds its bound.");

        var expected = FileNames(candidates.Length / 2);
        if (!candidates.ToHashSet(StringComparer.Ordinal).SetEquals(expected))
            throw new InvalidDataException("Compiler symbol files must form exact contiguous DLL/PDB pairs.");
        return expected;
    }

    internal static void ValidateRetainedInventory(CoverageFanInExpected expected, string producerId,
        IReadOnlyList<string> names)
    {
        if (names.Count > 0 && !IsEnabled(expected))
            throw new InvalidDataException("Compiler symbols are unsupported by this report profile.");
        if (producerId == "primary" && IsEnabled(expected) && names.Count == 0)
            throw new InvalidDataException("Async-profile primary capture is missing compiler symbols.");
    }

    internal static IReadOnlyList<CoverageAsyncMethod> ReadMethods(IReadOnlyList<string> names,
        IReadOnlyDictionary<string, byte[]> files, string sourceRoot)
    {
        if (names.Count == 0) return Array.Empty<CoverageAsyncMethod>();
        var methods = new List<CoverageAsyncMethod>();
        for (var index = 0; index < names.Count / 2; index++)
        {
            methods.AddRange(CoverageAsyncMethodMetadata.Read(files[AssemblyFileName(index)],
                files[PdbFileName(index)], sourceRoot));
        }
        return Array.AsReadOnly(methods.ToArray());
    }

    private static string[] FileNames(int assemblyCount) =>
        Enumerable.Range(0, assemblyCount).SelectMany(index => new[] { AssemblyFileName(index), PdbFileName(index) }).ToArray();

    private static string AssemblyFileName(int index) => FilePrefix + index.ToString("D4", CultureInfo.InvariantCulture) + ".dll";
    private static string PdbFileName(int index) => FilePrefix + index.ToString("D4", CultureInfo.InvariantCulture) + ".pdb";
}
