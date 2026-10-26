using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Evidence.Planner;

var definitions = EvidencePrivateC3Registration.Definitions;
// Catalogue snapshots and hashes do not establish that the actual shared adapter
// accepts a declaration. Exercise its metadata-only factory before publishing digests.
try
{
    foreach (var definition in definitions)
        foreach (var producer in definition.Producers)
            _ = EvidenceRestrictedCoverageProducerFactory.Create(producer.Declaration);
}
catch (EvidenceAdmissionException error) when (error.Code == "ASEVD404")
{
    Console.Error.WriteLine("ASEVD404: A compiled C3 producer declaration was rejected before digest publication.");
    return 1;
}
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
return 0;
