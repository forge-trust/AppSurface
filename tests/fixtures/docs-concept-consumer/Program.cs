using ForgeTrust.AppSurface.Docs;
using ForgeTrust.AppSurface.ReleaseContracts;

var options = new AppSurfaceDocsOptions();
var contractsAssembly = typeof(PackageReleaseLink).Assembly;
var conceptCoreTypes = contractsAssembly.GetTypes()
    .Where(type => type.Name.StartsWith("DocConcept", StringComparison.Ordinal))
    .ToArray();

if (conceptCoreTypes.Length == 0)
{
    Console.Error.WriteLine("The packed ReleaseContracts assembly does not contain its DocConcept* core types.");
    return 1;
}

var publicConceptTypes = conceptCoreTypes
    .Where(type => type.IsVisible)
    .Select(type => type.Name)
    .Order(StringComparer.Ordinal)
    .ToArray();
if (publicConceptTypes.Length > 0)
{
    Console.Error.WriteLine("Concept core types leaked into the public ReleaseContracts API:");
    foreach (var typeName in publicConceptTypes)
    {
        Console.Error.WriteLine(typeName);
    }

    return 1;
}

if (!typeof(AppSurfaceDocsOptions).IsPublic || options.GetType().Assembly != typeof(AppSurfaceDocsOptions).Assembly)
{
    Console.Error.WriteLine("The packed Docs public API could not be loaded from its NuGet package.");
    return 1;
}

if (!PackageReleaseLinkResolver.TryResolve("coordinated", null, out var link, out var error)
    || link?.ReleaseNotesPath != PackageReleaseLink.CoordinatedReleaseNotesPath)
{
    Console.Error.WriteLine($"The transitive ReleaseContracts public API could not be used: {error}");
    return 1;
}

Console.WriteLine($"Loaded {typeof(AppSurfaceDocsOptions).Assembly.GetName().Name} and "
    + $"{contractsAssembly.GetName().Name}; its {conceptCoreTypes.Length} DocConcept* types are internal.");
return 0;
