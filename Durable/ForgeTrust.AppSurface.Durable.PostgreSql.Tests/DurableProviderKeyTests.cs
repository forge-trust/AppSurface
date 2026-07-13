namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class DurableProviderKeyTests
{
    [Fact]
    public void Create_IsStableActivityDerivedAndScopeSeparated()
    {
        var first = DurableProviderKey.Create(new DurableScopeId("scope-a"), "activity-a");
        var repeated = DurableProviderKey.Create(new DurableScopeId("scope-a"), "activity-a");
        var otherActivity = DurableProviderKey.Create(new DurableScopeId("scope-a"), "activity-b");
        var otherScope = DurableProviderKey.Create(new DurableScopeId("scope-b"), "activity-a");

        Assert.Equal(first, repeated);
        Assert.StartsWith("asdur-v1-", first, StringComparison.Ordinal);
        Assert.Equal("asdur-v1-".Length + 64, first.Length);
        Assert.NotEqual(first, otherActivity);
        Assert.NotEqual(first, otherScope);
    }
}
