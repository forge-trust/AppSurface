namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class RepositoryDurableDocsContractTests
{
    private const string PostgreSqlRoleRecipeUrl =
        "https://github.com/forge-trust/AppSurface/blob/main/Durable/configure-postgresql-roles.sql";

    [Fact]
    public void ScheduleDocs_ShouldLinkToThePostgreSqlRoleRecipeAsAnExternalResource()
    {
        var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);

        var content = File.ReadAllText(TestPathUtils.PathUnder(repoRoot, "Durable/slice5-reference-workload.md"));

        Assert.Contains(
            $"[`configure-postgresql-roles.sql`]({PostgreSqlRoleRecipeUrl})",
            content,
            StringComparison.Ordinal);
    }
}
