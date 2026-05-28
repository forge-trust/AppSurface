using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Console;
using ForgeTrust.AppSurface.Core;
using Markdig;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ForgeTrust.AppSurface.Release;

internal static class PackageIndexEditor
{
    /// <summary>
    /// Updates release note paths for rows classified as public and publishable.
    /// </summary>
    /// <param name="content">Existing package index YAML.</param>
    /// <param name="releasePath">New repository-relative release note path.</param>
    /// <returns>Updated YAML content.</returns>
    /// <remarks>
    /// This method edits YAML line-by-line so existing comments and ordering survive a release PR. It expects the package index's
    /// current shape: package entries begin with two spaces and <c>- project:</c>, fields are indented with four spaces, and
    /// <c>classification</c>, <c>publish_decision</c>, <c>release_notes_path</c>, and <c>order</c> stay inside one package block.
    /// Different indentation or nested structures require updating this editor. Output is normalized to LF line endings with one
    /// trailing LF so release PR diffs are stable across platforms.
    /// </remarks>
    internal static string UpdatePublicPublishedReleaseNotes(string content, string releasePath)
    {
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var output = new List<string>(lines.Length);
        var block = new List<string>();

        foreach (var line in lines)
        {
            if (line.StartsWith("  - project:", StringComparison.Ordinal) && block.Count > 0)
            {
                output.AddRange(UpdateBlock(block, releasePath));
                block.Clear();
            }

            block.Add(line);
        }

        if (block.Count > 0)
        {
            output.AddRange(UpdateBlock(block, releasePath));
        }

        return string.Join('\n', output).TrimEnd() + "\n";
    }

    private static IEnumerable<string> UpdateBlock(List<string> block, string releasePath)
    {
        var isPublic = block.Any(line => line.Trim() == "classification: public");
        var isPublish = block.Any(line => line.Trim() == "publish_decision: publish");
        if (!isPublic || !isPublish)
        {
            return block;
        }

        var copy = block.ToArray();
        for (var index = 0; index < copy.Length; index++)
        {
            if (copy[index].TrimStart().StartsWith("release_notes_path:", StringComparison.Ordinal))
            {
                copy[index] = "    release_notes_path: " + releasePath;
                return copy;
            }
        }

        var insertAt = Array.FindIndex(copy, line => line.TrimStart().StartsWith("order:", StringComparison.Ordinal));
        if (insertAt < 0)
        {
            return copy.Append("    release_notes_path: " + releasePath);
        }

        return copy.Take(insertAt).Append("    release_notes_path: " + releasePath).Concat(copy.Skip(insertAt));
    }
}
