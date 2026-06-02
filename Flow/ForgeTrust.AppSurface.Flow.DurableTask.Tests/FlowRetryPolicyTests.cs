namespace ForgeTrust.AppSurface.Flow.DurableTask.Tests;

public sealed class FlowRetryPolicyTests
{
    [Fact]
    public void Constructor_CapturesRetrySettings()
    {
        var policy = new FlowRetryPolicy(3, TimeSpan.FromSeconds(5), backoffCoefficient: 2);

        Assert.Equal(3, policy.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(5), policy.FirstRetryInterval);
        Assert.Equal(2, policy.BackoffCoefficient);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_WithInvalidMaxAttempts_ThrowsArgumentOutOfRangeException(int maxAttempts)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FlowRetryPolicy(maxAttempts, TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_WithInvalidFirstRetryInterval_ThrowsArgumentOutOfRangeException(int seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FlowRetryPolicy(1, TimeSpan.FromSeconds(seconds)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.5)]
    public void Constructor_WithInvalidBackoffCoefficient_ThrowsArgumentOutOfRangeException(double backoffCoefficient)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FlowRetryPolicy(1, TimeSpan.FromSeconds(1), backoffCoefficient));
    }
}
