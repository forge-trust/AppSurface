namespace Issue164.Api;

/// <summary>
/// A representative C# API fixture for semantic extraction and Razor rendering tests.
/// </summary>
/// <typeparam name="TItem">The item accepted by the service.</typeparam>
public sealed class FixtureService<TItem>
{
    /// <summary>
    /// Processes a <paramref name="item"/> and returns a safe <see cref="T:System.String"/> value.
    /// </summary>
    /// <param name="item">The item to process.</param>
    /// <param name="attempt">The optional attempt number.</param>
    /// <returns>A rendered result.</returns>
    /// <exception cref="T:System.InvalidOperationException">Thrown when processing cannot continue.</exception>
    /// <remarks>
    /// <para>Hostile XML-like text: &lt;script&gt;must remain text&lt;/script&gt;.</para>
    /// <para>
    /// References <see cref="T:System.String"/>,
    /// <see href="https://example.com/issue164">external documentation</see>,
    /// <see langword="null"/>, and <typeparamref name="TItem"/>.
    /// Unknown markup <unknown>must fall back to text</unknown>.
    /// </para>
    /// <list type="number">
    /// <item><description>First ordered item.</description></item>
    /// <item><description>Second ordered item.</description></item>
    /// </list>
    /// <list type="bullet">
    /// <item><description>Bullet item.</description></item>
    /// </list>
    /// </remarks>
    /// <example><code>var value = service.Process(item);</code></example>
    public string Process(TItem item, int attempt = 1) => item?.ToString() ?? string.Empty;

    /// <summary>Processes a string value.</summary>
    public string Process(string item) => item;

    /// <summary>Gets the stable fixture name.</summary>
    public string Name => "fixture";
}

/// <summary>Fixture states.</summary>
public enum FixtureState
{
    /// <summary>Ready for processing.</summary>
    Ready
}
