using Xunit;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AspireEnvironmentCollection
{
    public const string Name = "Aspire process environment";
}
