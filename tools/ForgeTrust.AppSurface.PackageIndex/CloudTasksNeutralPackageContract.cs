using System.IO.Compression;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace ForgeTrust.AppSurface.PackageIndex;

/// <summary>Reads the packed nuspec contract for the neutral Durable packages being compared to the baseline.</summary>
internal static class CloudTasksNeutralPackageContract
{
    private const long MaximumNuspecBytes = 1024 * 1024;
    private static readonly HashSet<string> CoordinatedPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "ForgeTrust.AppSurface.Core",
        "ForgeTrust.AppSurface.Flow",
        "ForgeTrust.AppSurface.Workers",
        "ForgeTrust.AppSurface.Durable",
    };

    internal static string Read(string packagePath, string coordinatedVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(coordinatedVersion);

        using var archive = ZipFile.OpenRead(packagePath);
        var nuspecs = archive.Entries
            .Where(static entry => entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (nuspecs.Length != 1 || nuspecs[0].FullName.Contains('/') || nuspecs[0].FullName.Contains('\\'))
            throw new InvalidDataException($"Package '{Path.GetFileName(packagePath)}' must contain exactly one root nuspec file.");

        var nuspec = nuspecs[0];
        if (nuspec.Length == 0 || nuspec.Length > MaximumNuspecBytes)
            throw new InvalidDataException($"Package '{Path.GetFileName(packagePath)}' nuspec is empty or exceeds the {MaximumNuspecBytes}-byte inspection limit.");

        XDocument document;
        try
        {
            using var stream = nuspec.Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException($"Package '{Path.GetFileName(packagePath)}' nuspec is malformed XML.", exception);
        }

        var root = document.Root
            ?? throw new InvalidDataException($"Package '{Path.GetFileName(packagePath)}' nuspec has no root element.");
        if (root.Name.LocalName != "package")
            throw new InvalidDataException($"Package '{Path.GetFileName(packagePath)}' nuspec root must be 'package'.");
        var metadata = SingleChild(root, "metadata", "nuspec package")
            ?? throw new InvalidDataException($"Package '{Path.GetFileName(packagePath)}' nuspec has no metadata.");
        var dependenciesElement = SingleChild(metadata, "dependencies", "nuspec metadata")
            ?? throw new InvalidDataException($"Package '{Path.GetFileName(packagePath)}' nuspec has no dependencies contract.");
        if (SingleChild(metadata, "frameworkReferences", "nuspec metadata") is not null)
            throw new InvalidDataException("Nuspec framework references are not supported by the neutral package contract reader.");
        var dependencies = ReadDependencyGroups(dependenciesElement, coordinatedVersion);
        var frameworkAssemblies = ReadFrameworkAssemblies(metadata);
        var references = ReadReferences(metadata);

        var contract = new PackageContract(dependencies, frameworkAssemblies, references);
        return JsonSerializer.Serialize(contract);
    }

    private static DependencySection ReadDependencyGroups(XElement dependencies, string coordinatedVersion)
    {
        var groups = new List<DependencyGroup>();
        var ungrouped = dependencies.Elements().Where(static element => element.Name.LocalName == "dependency").ToArray();
        if (ungrouped.Length != 0)
            groups.Add(new DependencyGroup(string.Empty, [], ReadDependencies(ungrouped, coordinatedVersion)));

        var targetFrameworks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in dependencies.Elements())
        {
            if (child.Name.LocalName == "dependency") continue;
            if (child.Name.LocalName != "group")
                throw new InvalidDataException($"Nuspec dependencies contain unsupported element '{child.Name.LocalName}'.");

            var targetFramework = (string?)child.Attribute("targetFramework");
            if (string.IsNullOrWhiteSpace(targetFramework))
                throw new InvalidDataException("Nuspec dependency group is missing targetFramework.");
            if (!targetFrameworks.Add(targetFramework))
                throw new InvalidDataException($"Nuspec dependency groups repeat targetFramework '{targetFramework}'.");
            if (child.Elements().Any(static element => element.Name.LocalName != "dependency"))
                throw new InvalidDataException($"Nuspec dependency group '{targetFramework}' contains an unsupported element.");

            groups.Add(new DependencyGroup(
                targetFramework,
                Attributes(child, "targetFramework"),
                ReadDependencies(child.Elements().ToArray(), coordinatedVersion)));
        }

        return new DependencySection(Attributes(dependencies), groups.OrderBy(static group => group.TargetFramework, StringComparer.Ordinal).ToArray());
    }

    private static IReadOnlyList<DependencyContract> ReadDependencies(IEnumerable<XElement> elements, string coordinatedVersion)
    {
        var dependencies = new List<DependencyContract>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in elements)
        {
            var id = (string?)element.Attribute("id");
            var version = (string?)element.Attribute("version");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(version))
                throw new InvalidDataException("Nuspec dependency requires nonblank id and version attributes.");
            if (!ids.Add(id))
                throw new InvalidDataException($"Nuspec dependency group repeats package id '{id}'.");
            if (element.HasElements)
                throw new InvalidDataException($"Nuspec dependency '{id}' contains unexpected child elements.");

            var additionalAttributes = Attributes(element, "id", "version", "include", "exclude");
            dependencies.Add(new DependencyContract(
                id,
                NormalizeVersion(id, version, coordinatedVersion),
                (string?)element.Attribute("include"),
                (string?)element.Attribute("exclude"),
                additionalAttributes));
        }
        return dependencies.OrderBy(static dependency => dependency.Id, StringComparer.Ordinal).ToArray();
    }

    private static ElementSection ReadFrameworkAssemblies(XElement metadata)
    {
        var container = SingleChild(metadata, "frameworkAssemblies", "nuspec metadata");
        if (container is null) return new ElementSection([], []);
        if (container.Elements().Any(static element => element.Name.LocalName != "frameworkAssembly"))
            throw new InvalidDataException("Nuspec framework assemblies contain an unsupported element.");
        return new ElementSection(Attributes(container), ReadNamedElements(container, "frameworkAssembly", "assemblyName", "framework assemblies"));
    }

    private static ReferenceSection ReadReferences(XElement metadata)
    {
        var container = SingleChild(metadata, "references", "nuspec metadata");
        if (container is null) return new ReferenceSection([], []);
        var result = new List<ReferenceGroup>();
        var direct = container.Elements().Where(static element => element.Name.LocalName == "reference").ToArray();
        if (direct.Length != 0)
            result.Add(new ReferenceGroup(string.Empty, [], ReadNamedElements(container, "reference", "file", "nuspec references")));

        var frameworks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in container.Elements())
        {
            if (child.Name.LocalName == "reference") continue;
            if (child.Name.LocalName != "group")
                throw new InvalidDataException($"Nuspec references contain unsupported element '{child.Name.LocalName}'.");
            var targetFramework = (string?)child.Attribute("targetFramework");
            if (string.IsNullOrWhiteSpace(targetFramework))
                throw new InvalidDataException("Nuspec reference group is missing targetFramework.");
            if (!frameworks.Add(targetFramework))
                throw new InvalidDataException($"Nuspec reference groups repeat targetFramework '{targetFramework}'.");
            if (child.Elements().Any(static element => element.Name.LocalName != "reference"))
                throw new InvalidDataException($"Nuspec reference group '{targetFramework}' contains an unsupported element.");
            result.Add(new ReferenceGroup(
                targetFramework,
                Attributes(child, "targetFramework"),
                ReadNamedElements(child, "reference", "file", "nuspec reference group")));
        }
        return new ReferenceSection(Attributes(container), result.OrderBy(static group => group.TargetFramework, StringComparer.Ordinal).ToArray());
    }

    private static IReadOnlyList<ElementContract> ReadNamedElements(XElement parent, string elementName, string requiredAttribute, string context)
    {
        var values = new List<ElementContract>();
        var contracts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in parent.Elements().Where(element => element.Name.LocalName == elementName))
        {
            var requiredValue = (string?)element.Attribute(requiredAttribute);
            if (string.IsNullOrWhiteSpace(requiredValue))
                throw new InvalidDataException($"{context} element '{elementName}' requires '{requiredAttribute}'.");
            if (element.HasElements)
                throw new InvalidDataException($"{context} element '{elementName}' contains unexpected child elements.");
            var contract = new ElementContract(requiredValue, Attributes(element));
            var key = JsonSerializer.Serialize(contract);
            if (!contracts.Add(key))
                throw new InvalidDataException($"{context} repeats '{requiredValue}'.");
            values.Add(contract);
        }
        return values.OrderBy(static item => item.Name, StringComparer.Ordinal)
            .ThenBy(static item => JsonSerializer.Serialize(item.Attributes), StringComparer.Ordinal)
            .ToArray();
    }

    private static XElement? SingleChild(XElement? parent, string localName, string context)
    {
        var elements = parent?.Elements().Where(element => element.Name.LocalName == localName).ToArray() ?? [];
        if (elements.Length > 1)
            throw new InvalidDataException($"{context} repeats '{localName}'.");
        return elements.SingleOrDefault();
    }

    private static IReadOnlyList<AttributeContract> Attributes(XElement element, params string[] excludedNames)
        => element.Attributes()
            .Where(attribute => !attribute.IsNamespaceDeclaration && !excludedNames.Contains(attribute.Name.LocalName, StringComparer.Ordinal))
            .Select(static attribute => new AttributeContract(attribute.Name.ToString(), attribute.Value))
            .OrderBy(static attribute => attribute.Name, StringComparer.Ordinal)
            .ToArray();

    private static string NormalizeVersion(string packageId, string version, string coordinatedVersion)
    {
        if (!CoordinatedPackages.Contains(packageId)) return version;
        if (string.Equals(version, coordinatedVersion, StringComparison.Ordinal)) return "<coordinated>";
        if (string.Equals(version, $"[{coordinatedVersion}]", StringComparison.Ordinal)) return "[<coordinated>]";
        if (version.Length > 3 && version[0] == '[' && version[^1] == ')')
        {
            var comma = version.IndexOf(',');
            if (comma > 0
                && string.Equals(version[1..comma].Trim(), coordinatedVersion, StringComparison.Ordinal)
                && string.IsNullOrWhiteSpace(version[(comma + 1)..^1]))
                return "[<coordinated>,)";
        }
        return version;
    }

    private sealed record PackageContract(
        DependencySection Dependencies,
        ElementSection FrameworkAssemblies,
        ReferenceSection References);

    private sealed record DependencySection(IReadOnlyList<AttributeContract> AdditionalAttributes, IReadOnlyList<DependencyGroup> Groups);

    private sealed record DependencyGroup(string TargetFramework, IReadOnlyList<AttributeContract> AdditionalAttributes, IReadOnlyList<DependencyContract> Dependencies);

    private sealed record ElementSection(IReadOnlyList<AttributeContract> AdditionalAttributes, IReadOnlyList<ElementContract> Items);

    private sealed record ReferenceSection(IReadOnlyList<AttributeContract> AdditionalAttributes, IReadOnlyList<ReferenceGroup> Groups);

    private sealed record DependencyContract(
        string Id,
        string Version,
        string? Include,
        string? Exclude,
        IReadOnlyList<AttributeContract> AdditionalAttributes);

    private sealed record ReferenceGroup(string TargetFramework, IReadOnlyList<AttributeContract> AdditionalAttributes, IReadOnlyList<ElementContract> References);

    private sealed record ElementContract(string Name, IReadOnlyList<AttributeContract> Attributes);

    private sealed record AttributeContract(string Name, string Value);
}
