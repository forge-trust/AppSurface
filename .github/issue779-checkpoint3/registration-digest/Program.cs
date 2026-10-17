using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Planner;

var definitions = EvidencePrivateC3Registration.Definitions;
var entries = definitions.OrderBy(static item => item.Id, StringComparer.Ordinal)
    .Select(static item => new
    {
        application_id = item.Id,
        application_entry_digest = EvidenceClosedApplicationCatalogue.ComputeEntryDigest(item),
    })
    .ToArray();
var result = new
{
    schema = "issue779-c3-registration-digests-v1",
    catalogue_digest = EvidenceClosedApplicationCatalogue.ComputeCatalogueDigest(definitions),
    entries,
};
Console.WriteLine(JsonSerializer.Serialize(result));
