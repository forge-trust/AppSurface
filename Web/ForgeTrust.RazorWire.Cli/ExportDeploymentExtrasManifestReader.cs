using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace ForgeTrust.RazorWire.Cli;

internal static class ExportDeploymentExtrasManifestReader
{
    internal static IReadOnlyList<ExportDeploymentExtra> Read(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        var fullManifestPath = Path.GetFullPath(manifestPath);
        if (!File.Exists(fullManifestPath))
        {
            throw ExportDeploymentExtras.CreateException(
                "schema",
                $"Publish-root extras manifest '{fullManifestPath}' does not exist. Fix: pass the path to a YAML manifest such as 'deploy/export-extras.yml'.",
                ExportDeploymentExtras.RouteFallback);
        }

        YamlStream stream;
        try
        {
            stream = new YamlStream();
            using var reader = File.OpenText(fullManifestPath);
            stream.Load(reader);
        }
        catch (YamlException ex)
        {
            throw ExportDeploymentExtras.CreateException(
                "schema",
                $"Manifest '{fullManifestPath}' is not valid YAML: {ex.Message}. Fix: use the documented version/extras mapping.",
                ExportDeploymentExtras.RouteFallback);
        }

        if (stream.Documents.Count != 1)
        {
            throw Schema(fullManifestPath, null, "Manifest must contain exactly one YAML document.");
        }

        var root = stream.Documents[0].RootNode;
        RejectAliasNodes(fullManifestPath, root);
        if (root is not YamlMappingNode rootMap)
        {
            throw Schema(fullManifestPath, null, "Manifest root must be a mapping with 'version' and 'extras'.");
        }

        var rootFields = ReadMapping(fullManifestPath, null, rootMap, new HashSet<string>(StringComparer.Ordinal) { "version", "extras" });
        if (!rootFields.TryGetValue("version", out var versionNode)
            || !IsScalar(versionNode, out var version)
            || !string.Equals(version, "1", StringComparison.Ordinal))
        {
            throw Schema(fullManifestPath, null, "Manifest field 'version' must be integer 1.");
        }

        if (!rootFields.TryGetValue("extras", out var extrasNode)
            || extrasNode is not YamlSequenceNode extrasSequence)
        {
            throw Schema(fullManifestPath, null, "Manifest field 'extras' must be a non-empty sequence.");
        }

        if (extrasSequence.Children.Count == 0)
        {
            throw Schema(fullManifestPath, null, "Manifest field 'extras' must contain at least one item.");
        }

        var manifestDirectory = Path.GetDirectoryName(fullManifestPath)
            ?? throw Schema(fullManifestPath, null, "Manifest path must have a parent directory.");
        var extras = new List<ExportDeploymentExtra>();
        var publishPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < extrasSequence.Children.Count; i++)
        {
            var entryNumber = i + 1;
            if (extrasSequence.Children[i] is not YamlMappingNode itemMap)
            {
                throw Schema(fullManifestPath, entryNumber, "Each extras item must be a mapping with 'source' and 'publishPath'.");
            }

            var itemFields = ReadMapping(fullManifestPath, entryNumber, itemMap, new HashSet<string>(StringComparer.Ordinal) { "source", "publishPath" });
            if (!itemFields.TryGetValue("source", out var sourceNode) || !IsRequiredScalar(sourceNode, out var source))
            {
                throw Schema(fullManifestPath, entryNumber, "Field 'source' must be a non-empty string.");
            }

            if (!itemFields.TryGetValue("publishPath", out var publishPathNode) || !IsRequiredScalar(publishPathNode, out var publishPath))
            {
                throw Schema(fullManifestPath, entryNumber, "Field 'publishPath' must be a non-empty string such as '/CNAME'.");
            }

            var extra = ExportDeploymentExtras.CreateManifestExtra(
                fullManifestPath,
                entryNumber,
                manifestDirectory,
                source,
                publishPath);
            if (!publishPaths.Add(extra.PublishPath))
            {
                throw ExportDeploymentExtras.CreateException(
                    "target-duplicate",
                    ExportDeploymentExtras.FormatMessage(
                        fullManifestPath,
                        entryNumber,
                        $"Publish path '{extra.PublishPath}' is declared more than once. Fix: remove duplicate deployment extras; publish paths are matched case-insensitively."),
                    extra.PublishPath);
            }

            extras.Add(extra);
        }

        return extras;
    }

    private static Dictionary<string, YamlNode> ReadMapping(
        string manifestPath,
        int? entryIndex,
        YamlMappingNode mapping,
        IReadOnlySet<string> allowedKeys)
    {
        var result = new Dictionary<string, YamlNode>(StringComparer.Ordinal);
        foreach (var pair in mapping.Children)
        {
            if (!IsScalar(pair.Key, out var key) || string.IsNullOrWhiteSpace(key))
            {
                throw Schema(manifestPath, entryIndex, "Manifest keys must be plain scalar strings.");
            }

            if (!allowedKeys.Contains(key))
            {
                throw Schema(manifestPath, entryIndex, $"Unknown field '{key}'. Fix: use only {string.Join(", ", allowedKeys.Select(field => $"'{field}'"))}.");
            }

            if (!result.TryAdd(key, pair.Value))
            {
                throw Schema(manifestPath, entryIndex, $"Duplicate field '{key}' is not allowed.");
            }
        }

        foreach (var required in allowedKeys.Where(required => !result.ContainsKey(required)))
        {
            throw Schema(manifestPath, entryIndex, $"Missing required field '{required}'.");
        }

        return result;
    }

    private static bool IsRequiredScalar(YamlNode node, out string value)
    {
        return IsScalar(node, out value) && !string.IsNullOrWhiteSpace(value);
    }

    private static bool IsScalar(YamlNode node, out string value)
    {
        if (node is YamlScalarNode scalar && scalar.Value is not null)
        {
            value = scalar.Value;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static void RejectAliasNodes(string manifestPath, YamlNode node)
    {
        if (!node.Anchor.IsEmpty)
        {
            throw Schema(manifestPath, null, "YAML aliases and anchors are not supported in publish-root extras manifests.");
        }

        switch (node)
        {
            case { } when string.Equals(node.GetType().Name, "YamlAliasNode", StringComparison.Ordinal):
                throw Schema(manifestPath, null, "YAML aliases and anchors are not supported in publish-root extras manifests.");
            case YamlMappingNode mapping:
                foreach (var pair in mapping.Children)
                {
                    RejectAliasNodes(manifestPath, pair.Key);
                    RejectAliasNodes(manifestPath, pair.Value);
                }

                break;
            case YamlSequenceNode sequence:
                foreach (var child in sequence.Children)
                {
                    RejectAliasNodes(manifestPath, child);
                }

                break;
        }
    }

    private static ExportValidationException Schema(string manifestPath, int? entryIndex, string message)
    {
        return ExportDeploymentExtras.CreateException(
            "schema",
            ExportDeploymentExtras.FormatMessage(manifestPath, entryIndex, $"{message} Fix: use the documented schema with version: 1 and extras items containing source and publishPath."),
            ExportDeploymentExtras.RouteFallback);
    }
}
