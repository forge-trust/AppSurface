using System.Text.Json;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>One bounded transport selector; only capture/import may validate its evidence or establish custody.</summary>
internal sealed record CoverageFanInNativeInput(string Directory, string SourceRoot);

/// <summary>Discovers downloaded dogfood manifests under native no-follow handles before invoking import.</summary>
/// <remarks>Limits are 10,000 total entries, depth 64, and 1 MiB per manifest. Exactly one manifest for
/// each expected producer is required. Discovery grants no ownership, authenticity, or execution claim.</remarks>
internal static class CoverageFanInNativeInputs
{
    internal static IReadOnlyDictionary<string, CoverageFanInNativeInput> Discover(string input,
        IEnumerable<string> producerIds, CancellationToken token = default)
    {
        var expected = producerIds.ToHashSet(StringComparer.Ordinal);
        var found = new Dictionary<string, CoverageFanInNativeInput>(StringComparer.Ordinal);
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((Path.GetFullPath(input), 0));
        var entries = 0;
        while (pending.TryPop(out var current))
        {
            token.ThrowIfCancellationRequested();
            if (current.Depth > 64) throw new InvalidDataException("Native input discovery exceeds depth budget.");
            using var directory = CoverageRunOutputLease.AcquireArtifactDirectory(current.Path, false, false)
                ?? throw new IOException("Native input directory unavailable.");
            foreach (var entry in directory.EnumerateArtifactEntries(10000))
            {
                token.ThrowIfCancellationRequested();
                if (++entries > 10000) throw new InvalidDataException("Native input discovery exceeds entry budget.");
                if (entry.IsDirectory) pending.Push((Path.Join(current.Path, entry.Name), current.Depth + 1));
                else if (entry.Name == "manifest.json")
                {
                    using var file = directory.OpenArtifactFile(entry.Name, false, false);
                    const int maximumBytes = 1024 * 1024;
                    if (file.Length > maximumBytes) throw new InvalidDataException("Native manifest exceeds byte budget.");
                    using var bytes = new MemoryStream();
                    var buffer = new byte[81920];
                    int count;
                    while ((count = file.Read(buffer)) != 0)
                    {
                        token.ThrowIfCancellationRequested();
                        if (bytes.Length + count > maximumBytes) throw new InvalidDataException("Native manifest exceeds byte budget.");
                        bytes.Write(buffer, 0, count);
                    }
                    using var manifest = JsonDocument.Parse(bytes.ToArray(), new() { MaxDepth = 64 });
                    var id = manifest.RootElement.GetProperty("producerId").GetString();
                    var source = manifest.RootElement.GetProperty("sourceRoot").GetString();
                    if (id is null || !expected.Contains(id) || string.IsNullOrWhiteSpace(source)
                        || !found.TryAdd(id, new(current.Path, source)))
                        throw new InvalidDataException("Native manifest inventory differs.");
                }
            }
            directory.VerifyArtifactBinding();
        }
        if (found.Count != expected.Count) throw new InvalidDataException("Native manifest inventory is incomplete.");
        return found;
    }
}
