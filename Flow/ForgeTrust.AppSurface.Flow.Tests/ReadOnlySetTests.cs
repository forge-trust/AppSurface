using System.Collections;

namespace ForgeTrust.AppSurface.Flow.Tests;

public sealed class ReadOnlySetTests
{
    [Fact]
    public void Constructor_CopiesDistinctValues()
    {
        var source = new[] { "review", "approve", "review" };

        var set = new ReadOnlySet<string>(source);

        Assert.Equal(2, set.Count);
        Assert.True(set.Contains("review"));
        Assert.False(set.Contains("missing"));
    }

    [Fact]
    public void Enumerators_ReturnStoredValues()
    {
        var set = new ReadOnlySet<string>(["review", "approve"]);

        Assert.Equal(["approve", "review"], set.OrderBy(value => value, StringComparer.Ordinal));
        Assert.Equal(
            ["approve", "review"],
            ((IEnumerable)set)
                .Cast<string>()
                .OrderBy(value => value, StringComparer.Ordinal));
    }

    [Fact]
    public void SetOperations_DelegateToInnerSet()
    {
        var set = new ReadOnlySet<string>(["review", "approve"]);

        Assert.True(set.IsSubsetOf(["review", "approve", "archive"]));
        Assert.True(set.IsProperSubsetOf(["review", "approve", "archive"]));
        Assert.True(set.IsSupersetOf(["review"]));
        Assert.True(set.IsProperSupersetOf(["review"]));
        Assert.True(set.Overlaps(["archive", "approve"]));
        Assert.True(set.SetEquals(["approve", "review"]));

        Assert.False(set.IsSubsetOf(["review"]));
        Assert.False(set.IsProperSubsetOf(["review", "approve"]));
        Assert.False(set.IsSupersetOf(["review", "approve", "archive"]));
        Assert.False(set.IsProperSupersetOf(["review", "approve"]));
        Assert.False(set.Overlaps(["archive"]));
        Assert.False(set.SetEquals(["review"]));
    }
}
