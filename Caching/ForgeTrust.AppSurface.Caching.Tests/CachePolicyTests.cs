namespace ForgeTrust.AppSurface.Caching.Tests;

public class CachePolicyTests
{
    [Fact]
    public void Absolute_ZeroDuration_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CachePolicy.Absolute(TimeSpan.Zero));
    }

    [Fact]
    public void Absolute_NegativeDuration_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CachePolicy.Absolute(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void Absolute_ValidDuration_SetsProperty()
    {
        var policy = CachePolicy.Absolute(TimeSpan.FromMinutes(5));
        Assert.Equal(TimeSpan.FromMinutes(5), policy.AbsoluteExpiration);
        Assert.Null(policy.SlidingExpiration);
    }

    [Fact]
    public void Sliding_ZeroWindow_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CachePolicy.Sliding(TimeSpan.Zero));
    }

    [Fact]
    public void Sliding_NegativeWindow_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CachePolicy.Sliding(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void Sliding_ValidWindow_SetsProperty()
    {
        var policy = CachePolicy.Sliding(TimeSpan.FromMinutes(5));
        Assert.Equal(TimeSpan.FromMinutes(5), policy.SlidingExpiration);
        Assert.Null(policy.AbsoluteExpiration);
    }

    [Fact]
    public void SlidingWithAbsolute_ZeroWindow_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CachePolicy.SlidingWithAbsolute(TimeSpan.Zero, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void SlidingWithAbsolute_NegativeWindow_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CachePolicy.SlidingWithAbsolute(TimeSpan.FromSeconds(-1), TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void SlidingWithAbsolute_ZeroMax_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CachePolicy.SlidingWithAbsolute(TimeSpan.FromMinutes(1), TimeSpan.Zero));
    }

    [Fact]
    public void SlidingWithAbsolute_NegativeMax_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CachePolicy.SlidingWithAbsolute(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void SlidingWithAbsolute_WindowEqualsMax_Throws()
    {
        var duration = TimeSpan.FromMinutes(5);
        Assert.Throws<ArgumentException>(() =>
            CachePolicy.SlidingWithAbsolute(duration, duration));
    }

    [Fact]
    public void SlidingWithAbsolute_WindowGreaterThanMax_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            CachePolicy.SlidingWithAbsolute(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void SlidingWithAbsolute_ValidArgs_SetsBothProperties()
    {
        var policy = CachePolicy.SlidingWithAbsolute(
            TimeSpan.FromMinutes(5), TimeSpan.FromHours(1));
        Assert.Equal(TimeSpan.FromMinutes(5), policy.SlidingExpiration);
        Assert.Equal(TimeSpan.FromHours(1), policy.AbsoluteExpiration);
    }
}
