using System.Reflection;
using System.Runtime.Loader;
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.Provider;

var legacyConsumerPath = Path.Combine(AppContext.BaseDirectory, "ForgeTrust.AppSurface.Durable.LegacyCoreProviderAbiConsumer.dll");
if (!File.Exists(legacyConsumerPath))
{
    throw new FileNotFoundException("The separately compiled historical Core/Provider ABI consumer is missing.", legacyConsumerPath);
}

var currentCore = typeof(DurableWorkExecutionContext).Assembly;
var currentProvider = typeof(DurableClaimedWork).Assembly;
var legacyConsumer = AssemblyLoadContext.Default.LoadFromAssemblyPath(legacyConsumerPath);
var probeType = legacyConsumer.GetType("ForgeTrust.AppSurface.Durable.LegacyAbi.LegacyApiProbe", throwOnError: true)!;
var probe = probeType.GetMethod("RunAsync", BindingFlags.Public | BindingFlags.Static)
    ?? throw new MissingMethodException(probeType.FullName, "RunAsync");
var result = probe.Invoke(null, null) as Task<string>
    ?? throw new InvalidOperationException("The historical ABI probe returned an unexpected task.");
var observation = await result.ConfigureAwait(false);
var currentMarker = string.Join(
    '|',
    currentCore.ManifestModule.ModuleVersionId.ToString("N"),
    currentProvider.ManifestModule.ModuleVersionId.ToString("N"));
if (!observation.StartsWith(currentMarker + "|", StringComparison.Ordinal))
{
    throw new InvalidOperationException("The historical caller did not bind to the freshly packed Core and Provider assemblies.");
}

Console.WriteLine($"historical Core/Provider ABI caller bound to fresh package assemblies: {observation}");
