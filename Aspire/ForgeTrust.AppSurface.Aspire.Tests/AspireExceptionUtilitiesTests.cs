using ForgeTrust.AppSurface.Aspire;

public sealed class AspireExceptionUtilitiesTests
{
    [Theory]
    [InlineData(typeof(OutOfMemoryException), true)]
    [InlineData(typeof(StackOverflowException), true)]
    [InlineData(typeof(AccessViolationException), true)]
    [InlineData(typeof(InvalidOperationException), false)]
    public void IsProcessFatal_ClassifiesCleanupBoundaryExceptions(Type exceptionType, bool expected)
    {
        var exception = Assert.IsAssignableFrom<Exception>(Activator.CreateInstance(exceptionType));

        Assert.Equal(expected, AspireExceptionUtilities.IsProcessFatal(exception));
    }
}
