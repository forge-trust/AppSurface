using System.Text;
using System.Xml.Linq;

namespace ForgeTrust.AppSurface.PackageIndex;

/// <summary>Creates an owned candidate copy of the standalone starter before template packing.</summary>
/// <remarks>
/// Authored C# is copied unchanged. Only direct coordinated package-reference versions and the explicit install-command
/// version in the two first-use documents are stamped. The native template engine remains responsible for name expansion.
/// Staging is release preparation, never an install-time package selector or a source checkout mutation.
/// </remarks>
internal static class DurableTemplateStaging
{
    /// <summary>The single coordinated template package identity.</summary>
    internal const string PackageId = "ForgeTrust.AppSurface.Durable.Templates";

    /// <summary>The maintained standalone source, relative to the repository root.</summary>
    internal const string ContentPath = "Durable/ForgeTrust.AppSurface.Durable.Templates/content/durable-worker";

    /// <summary>Copies reviewed content into a previously absent owned directory and stamps its candidate package graph.</summary>
    /// <param name="source">Maintained content directory; all entries must be regular files/directories.</param>
    /// <param name="destination">Previously absent candidate directory; callers own its lifetime.</param>
    /// <param name="version">Exact release workflow version, without build metadata.</param>
    /// <returns>Absolute candidate directory suitable for the pack project's TemplateContentRoot property.</returns>
    /// <exception cref="PackageIndexException">Unsafe source, unexpected graph, or invalid candidate version.</exception>
    internal static string Stage(string source, string destination, string version)
    {
        PackageVersionValidator.Require(version, PackageVersionPolicy.StableOrPrereleaseNoBuildMetadata);
        RequireRegularPath(source);
        if (!Directory.Exists(source) || Directory.Exists(destination) || File.Exists(destination))
        {
            throw new PackageIndexException("Template staging requires an existing regular source and an absent owned destination.");
        }

        RequireRegularPath(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        var inputs = EnumerateContent(source).ToArray();
        var versions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in inputs.Where(path => path.EndsWith(".csproj", StringComparison.Ordinal) || Path.GetFileName(path) == "Directory.Packages.props"))
        {
            var document = XDocument.Load(project);
            foreach (var reference in document.Descendants().Where(element => element.Name.LocalName is "PackageReference" or "PackageVersion"))
            {
                if (((string?)reference.Attribute("Include"))?.StartsWith("ForgeTrust.AppSurface.", StringComparison.Ordinal) == true)
                {
                    var current = (string?)reference.Attribute("Version");
                    if (!string.IsNullOrWhiteSpace(current)) versions.Add(current);
                }
            }
        }
        DurableTemplateArtifactContract.ValidateProjectVersions(source, versions.Count == 1 ? versions.Single() : version);
        if (versions.Count != 1)
        {
            throw new PackageIndexException("Authored starter must contain exactly one coordinated package version.");
        }

        Directory.CreateDirectory(destination);
        foreach (var input in inputs)
        {
            var relative = Path.GetRelativePath(source, input);
            var output = Path.Join(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.Copy(input, output, overwrite: false);
            if (input.EndsWith(".csproj", StringComparison.Ordinal) || Path.GetFileName(input) == "Directory.Packages.props")
            {
                var document = XDocument.Load(input, LoadOptions.PreserveWhitespace);
                foreach (var reference in document.Descendants().Where(element => element.Name.LocalName is "PackageReference" or "PackageVersion"))
                {
                    if (((string?)reference.Attribute("Include"))?.StartsWith("ForgeTrust.AppSurface.", StringComparison.Ordinal) == true)
                    {
                        if (reference.Attribute("Version") is not null) reference.SetAttributeValue("Version", version);
                    }
                }
                using var writer = new StreamWriter(output, append: false, new UTF8Encoding(false));
                document.Save(writer, SaveOptions.DisableFormatting);
            }
            else if (relative.Replace('\\', '/') is "README.md" or "docs/replace-sample.md")
            {
                var text = File.ReadAllText(input);
                var oldCommand = $"{PackageId}@{versions.Single()}";
                File.WriteAllText(output, text.Replace(oldCommand, $"{PackageId}@{version}", StringComparison.Ordinal), new UTF8Encoding(false));
            }
        }
        return Path.GetFullPath(destination);
    }

    /// <summary>Enumerates maintained regular files, excluding only ordinary generated build and test output.</summary>
    /// <param name="root">Content root.</param>
    /// <returns>Files in deterministic relative-path order.</returns>
    internal static IEnumerable<string> EnumerateContent(string root)
    {
        RequireRegularPath(root);
        foreach (var path in Directory.EnumerateFileSystemEntries(root).Order(StringComparer.Ordinal))
        {
            RequireRegularPath(path);
            if (Directory.Exists(path))
            {
                if (Path.GetFileName(path) is "bin" or "obj" or "TestResults") continue;
                foreach (var file in EnumerateContent(path)) yield return file;
            }
            else yield return path;
        }
    }

    /// <summary>Rejects linked/reparse components before proof IO or cleanup.</summary>
    /// <param name="path">Absolute or relative file/directory path; existing ancestors are inspected.</param>
    /// <remarks>Absent final components are permitted; an existing ancestor must never redirect ownership.</remarks>
    internal static void RequireRegularPath(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                // macOS /tmp resolves to /private/tmp. Callers use canonical roots before this check.
                throw new PackageIndexException("Template proof paths must not contain symbolic links or reparse points.");
            }
            current = Path.GetDirectoryName(current);
        }
    }
}
