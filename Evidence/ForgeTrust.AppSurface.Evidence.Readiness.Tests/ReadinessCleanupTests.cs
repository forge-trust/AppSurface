namespace ForgeTrust.AppSurface.Evidence.Readiness.Tests;

public sealed class ReadinessCleanupTests
{
    // Value: protects=fatal identity and depth-first precedence through deeply nested cleanup aggregates;
    // fails_when=recursive traversal exhausts the call stack or traversal selects the later fatal sibling;
    // why_new=existing adapter theories cover shallow aggregates but do not exercise deep nesting;
    // seam=none; no_claim=unit classification only; adapter lifecycle and real resource readiness have separate tests.
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
