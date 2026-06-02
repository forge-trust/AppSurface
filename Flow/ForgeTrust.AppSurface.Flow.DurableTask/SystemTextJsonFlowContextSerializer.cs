using System.Text.Json;

namespace ForgeTrust.AppSurface.Flow.DurableTask;

/// <summary>
/// System.Text.Json implementation of <see cref="IFlowContextSerializer"/>.
/// </summary>
public sealed class SystemTextJsonFlowContextSerializer : IFlowContextSerializer
{
    private readonly JsonSerializerOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="SystemTextJsonFlowContextSerializer"/> class.
    /// </summary>
    public SystemTextJsonFlowContextSerializer()
        : this(new JsonSerializerOptions(JsonSerializerDefaults.Web))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SystemTextJsonFlowContextSerializer"/> class.
    /// </summary>
    /// <param name="options">JSON serializer options.</param>
    public SystemTextJsonFlowContextSerializer(JsonSerializerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public string Serialize<TContext>(TContext context) => JsonSerializer.Serialize(context, _options);

    /// <inheritdoc />
    public TContext Deserialize<TContext>(string payload)
    {
        var context = JsonSerializer.Deserialize<TContext>(payload, _options);
        if (context is null)
        {
            throw new JsonException($"Payload did not contain a '{typeof(TContext).FullName}' value.");
        }

        return context;
    }
}
