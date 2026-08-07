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

    /// <summary>Creates a scope that assigns an environment variable for the duration of a test.</summary>
    /// <param name="name">The name of the process environment variable to assign.</param>
    /// <param name="value">The value to assign, or <see langword="null" /> to remove the variable while the scope is active.</param>
    /// <remarks>Disposing the scope restores the variable value that existed before construction.</remarks>
    internal EnvironmentVariableScope(string name, string? value)
    {
        _name = name;
        _previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    /// <inheritdoc />
    public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
}
