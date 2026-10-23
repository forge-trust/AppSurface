namespace ForgeTrust.AppSurface.Evidence.Readiness.Tests;

public sealed class ReadinessCleanupTests
{
    // Value: preserves fatal identity and depth-first precedence without stack exhaustion at a deep cleanup boundary.
    // Failure: recursive traversal exhausts the process stack; reversed ordering selects the later fatal sibling.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Deep_aggregate_cleanup_preserves_depth_first_fatal_identity(bool hasFatalLeaf)
    {
        var firstFatal = new OutOfMemoryException("first fatal");
        Exception nested = hasFatalLeaf ? firstFatal : new InvalidOperationException("ordinary leaf");
        for (var depth = 0; depth < 20_000; depth++)
        {
            nested = new AggregateException(new InvalidOperationException("ordinary sibling"), nested);
        }

        var laterFatal = new OutOfMemoryException("later fatal");
        var root = new AggregateException(nested, laterFatal);

        var actual = ReadinessCleanup.FindFatal(root, exception => exception is OutOfMemoryException);

        Assert.Same(hasFatalLeaf ? firstFatal : laterFatal, actual);
    }
}
