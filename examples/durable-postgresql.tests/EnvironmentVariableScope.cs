/// <summary>Serializes tests that temporarily configure process-wide local-proof environment variables.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DurablePostgreSqlLocalExampleCollection
{
    /// <summary>Gets the shared xUnit collection name for tests that change local-proof process state.</summary>
    public const string Name = "Durable PostgreSQL local example";
}

/// <summary>Restores one process environment variable after a test completes.</summary>
internal sealed class EnvironmentVariableScope : IDisposable
{
    private readonly string _name;
    private readonly string? _previous;

    internal EnvironmentVariableScope(string name, string? value)
    {
        _name = name;
        _previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    /// <inheritdoc />
    public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
}
