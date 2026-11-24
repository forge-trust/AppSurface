using System.Runtime.InteropServices;
using System.Text.Json;
using ForgeTrust.AppSurface.Caching;
using ForgeTrust.AppSurface.Docs;
using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Docs.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;

const string functionDocstring = "APPSURFACE_916_FUNCTION_DOCSTRING_SENTINEL";
const string classDocstring = "APPSURFACE_916_CLASS_DOCSTRING_SENTINEL";

if (args is not [("inactive" or "harvest" or "unavailable")])
{
    Console.Error.WriteLine("usage: <fixture.dll> <inactive|harvest|unavailable>");
    return 64;
}

var mode = args[0];
Console.WriteLine("APPSURFACE_916_STARTUP");
var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
{
    ContentRootPath = AppContext.BaseDirectory,
    Args = ["--hostBuilder:reloadConfigOnChange=false"],
});
builder.Services.AddAppSurfaceDocs();
await using var app = builder.Build();

var options = new AppSurfaceDocsOptions();
options.Source.RepositoryRoot = AppContext.BaseDirectory;
options.Contributor.Enabled = false;
options.Harvest.Python.Enabled = mode != "inactive";
options.Harvest.Python.IncludeGlobs = ["fixture.py"];
options.Harvest.Python.StrictHealth = mode == "unavailable";

using var cache = new MemoryCache(new MemoryCacheOptions());
var harvester = new PythonDocHarvester(options, NullLogger<PythonDocHarvester>.Instance);
var aggregator = new DocAggregator(
    [harvester],
    options,
    app.Environment,
    new Memo(cache),
    app.Services.GetRequiredService<IAppSurfaceDocsHtmlSanitizer>(),
    NullLogger<DocAggregator>.Instance);

var health = await aggregator.GetHarvestHealthAsync();
var docs = await aggregator.GetDocsAsync();
var diagnostic = health.Diagnostics.SingleOrDefault(item => item.Code == DocHarvestDiagnosticCodes.PythonParserUnavailable);
var hasFunctionDocstring = docs.Any(node => node.Content.Contains(functionDocstring, StringComparison.Ordinal));
var hasClassDocstring = docs.Any(node => node.Content.Contains(classDocstring, StringComparison.Ordinal));

Console.WriteLine("APPSURFACE_916_RESULT " + JsonSerializer.Serialize(new
{
    mode,
    rid = RuntimeInformation.RuntimeIdentifier,
    architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    health = health.Status.ToString(),
    parserDiagnostic = diagnostic?.Code,
    parserSeverity = diagnostic?.Severity.ToString(),
    hasFunctionDocstring,
    hasClassDocstring,
}));

return mode switch
{
    "inactive" when health.Status == DocHarvestHealthStatus.Empty && diagnostic is null && docs.Count == 0 => 0,
    "harvest" when health.Status == DocHarvestHealthStatus.Healthy && diagnostic is null && hasFunctionDocstring && hasClassDocstring => 0,
    "unavailable" when health.Status == DocHarvestHealthStatus.Failed && diagnostic?.Severity == DocHarvestDiagnosticSeverity.Error && docs.Count == 0 => 0,
    _ => 1,
};
