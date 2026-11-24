namespace ForgeTrust.AppSurface.PostgreSql.Testing.Tests;

public sealed class TemplateCleanupResultTests
{
    [Fact]
    public void ThrowIfFailed_AllowsEmptyAndFullyRemovedResults()
    {
        var empty = new TemplateCleanupResult([], TimeSpan.Zero);
        var removed = new TemplateCleanupResult(
            [TemplateCleanupOutcome.Removed("template_one")],
            TimeSpan.FromMilliseconds(3));

        empty.ThrowIfFailed();
        removed.ThrowIfFailed();

        Assert.Equal(0, empty.RegisteredTemplateCount);
        Assert.Equal(0, empty.RemovedTemplateCount);
        Assert.Equal(1, removed.RegisteredTemplateCount);
        Assert.Equal(1, removed.RemovedTemplateCount);
        Assert.Empty(removed.Failures);
        Assert.Empty(removed.IncompleteOutcomes);
    }

    [Fact]
    public void ThrowIfFailed_ExposesTheCompleteResultWhenCleanupIsIncomplete()
    {
        var result = new TemplateCleanupResult(
        [
            TemplateCleanupOutcome.Removed("template_removed"),
            TemplateCleanupOutcome.Failed("template_failed", new InvalidOperationException("drop failed")),
            TemplateCleanupOutcome.AlreadyInProgress("template_in_progress"),
        ],
        TimeSpan.FromMilliseconds(8));

        var exception = Assert.Throws<TemplateCleanupException>(result.ThrowIfFailed);

        Assert.Same(result, exception.Result);
        Assert.Equal(3, result.RegisteredTemplateCount);
        Assert.Equal(1, result.RemovedTemplateCount);
        Assert.Single(result.Failures);
        Assert.Equal("template_failed", result.Failures[0].DatabaseName);
        Assert.Equal("InvalidOperationException: drop failed", result.Failures[0].Failure);
        Assert.Equal(2, result.IncompleteOutcomes.Count);
        Assert.Contains("template_in_progress", exception.Message, StringComparison.Ordinal);
    }
}
