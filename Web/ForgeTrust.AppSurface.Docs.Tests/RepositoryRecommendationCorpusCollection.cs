namespace ForgeTrust.AppSurface.Docs.Tests;

// Real repository harvests and process-wide GC measurements must not compete with other tests.
// Keep the production harvester timeout unchanged and measure complete, independent generations.
[CollectionDefinition(nameof(RepositoryRecommendationCorpusCollection), DisableParallelization = true)]
public sealed class RepositoryRecommendationCorpusCollection;
