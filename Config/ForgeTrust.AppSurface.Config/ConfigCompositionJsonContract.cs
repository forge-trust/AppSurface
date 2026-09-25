using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace ForgeTrust.AppSurface.Config;

/// <summary>Caches value-free default JSON metadata and creates separate invocation-owned binding options.</summary>
internal sealed class ConfigCompositionJsonContract
{
    private readonly JsonSerializerOptions _structural;
    private readonly AppSurfaceConfigOptions _limits;
    private readonly ConcurrentDictionary<Type, bool> _containsSecrets = new();
    private readonly ConcurrentDictionary<Type, ConfigCompositionShape> _shapes = new();

    /// <summary>Captures immutable host structural settings and bounded discovery options.</summary>
    internal ConfigCompositionJsonContract(AppSurfaceConfigOptions limits)
    {
        _limits = limits;
        _structural = new JsonSerializerOptions(JsonSerializerOptions.Default)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };
        _structural.MakeReadOnly();
    }

    /// <summary>Detects opt-in even when a custom converter hides the graph from JSON metadata.</summary>
    internal bool ContainsSecrets(Type type) => _containsSecrets.GetOrAdd(type, t => ContainsSecrets(t, []));

    private static bool ContainsSecrets(Type type, HashSet<Type> visited)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (IsSecret(type)) return true;
        if (ConfigScalarTypes.IsScalar(type) || type == typeof(object) || !visited.Add(type)) return false;
        // The opt-in probe is finite independently of compilation. An over-large graph is conservatively compiled
        // so its explicit limits can fail safely instead of silently selecting the legacy path.
        if (visited.Count > 4096) return true;
        if (type.IsArray) return ContainsSecrets(type.GetElementType()!, visited);
        if (typeof(IEnumerable).IsAssignableFrom(type) && type.IsGenericType
            && type.GetGenericArguments().Any(t => ContainsSecrets(t, visited))) return true;
        return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                   .Where(p => p.GetIndexParameters().Length == 0 && !Ignored(p))
                   .Any(p => ContainsSecrets(p.PropertyType, visited))
               || type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                   .Where(f => f.IsDefined(typeof(JsonIncludeAttribute)) && !Ignored(f))
                   .Any(f => ContainsSecrets(f.FieldType, visited));
    }

    private static bool Ignored(MemberInfo member) => member.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition == JsonIgnoreCondition.Always;

    /// <summary>Tests the exact wrapper definition without inspecting its payload.</summary>
    internal static bool IsSecret(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Secret<>);

    /// <summary>Gets cached structural destinations. The shape contains neither source declarations nor payloads.</summary>
    internal ConfigCompositionShape GetShape(Type type) => _shapes.GetOrAdd(type, Discover);

    private ConfigCompositionShape Discover(Type rootType)
    {
        var secrets = new List<ConfigSecretDestination>();
        var members = new List<ConfigCompositionMember>();
        var failures = new List<ConfigShapeFailure>();
        var active = new HashSet<Type>();
        var nodes = 0;
        if (rootType.IsValueType || IsSecret(rootType))
            failures.Add(new([], "secret-destination-type-unsupported"));
        else Visit(rootType, [], 0);
        return new(rootType, secrets.AsReadOnly(), members.AsReadOnly(), failures.AsReadOnly());

        void Visit(Type type, string[] path, int depth)
        {
            if (++nodes > _limits.MaxCompositionGraphNodes || depth > _limits.MaxCompositionGraphDepth)
            {
                failures.Add(new(path, "secret-graph-limit-exceeded"));
                return;
            }
            if (IsSecret(type))
            {
                var inner = type.GetGenericArguments()[0];
                if (!ConfigScalarTypes.IsScalar(inner) || inner.IsDefined(typeof(JsonConverterAttribute), true))
                    failures.Add(new(path, "secret-destination-type-unsupported"));
                else if (secrets.Count >= _limits.MaxSecretDestinationsPerRoot)
                    failures.Add(new(path, "secret-graph-limit-exceeded"));
                else secrets.Add(new(Array.AsReadOnly(path), type, inner));
                return;
            }
            if (ConfigScalarTypes.IsScalar(type) || type == typeof(object)) return;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ConfigStruct<>))
            {
                failures.Add(new(path, "secret-destination-type-unsupported"));
                return;
            }
            if (!active.Add(type))
            {
                if (ContainsSecrets(type)) failures.Add(new(path, "secret-destination-type-unsupported"));
                return;
            }
            try
            {
                if (typeof(IEnumerable).IsAssignableFrom(type))
                {
                    if (ContainsSecrets(type)) failures.Add(new(path, "secret-destination-type-unsupported"));
                    return;
                }
                var info = _structural.GetTypeInfo(type);
                if (info.Kind != JsonTypeInfoKind.Object || info.PolymorphismOptions is not null)
                {
                    if (ContainsSecrets(type)) failures.Add(new(path, "secret-destination-type-unsupported"));
                    return;
                }
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in info.Properties)
                {
                    // Ignored members have neither accessor. Constructor-bound and init members remain metadata members.
                    if (property.Get is null && property.Set is null) continue;
                    string[] child = [.. path, property.Name];
                    if (string.IsNullOrWhiteSpace(property.Name) || property.Name.Contains('.') || property.Name.Contains(':') || !names.Add(property.Name))
                    {
                        failures.Add(new(path, "secret-path-collision"));
                        continue;
                    }
                    if (property.IsExtensionData || (property.CustomConverter is not null && ContainsSecrets(property.PropertyType)))
                    {
                        failures.Add(new(child, "secret-destination-type-unsupported"));
                        continue;
                    }
                    if (ContainsSecrets(property.PropertyType) && property.Set is null && property.AssociatedParameter is null)
                    {
                        failures.Add(new(child, "secret-destination-type-unsupported"));
                        continue;
                    }
                    if (!IsSecret(property.PropertyType)) members.Add(new(Array.AsReadOnly(child), property.PropertyType));
                    Visit(property.PropertyType, child, depth + 1);
                    if (nodes > _limits.MaxCompositionGraphNodes) break;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException)
            {
                failures.Add(new(path, "secret-destination-type-unsupported"));
            }
            finally { active.Remove(type); }
        }
    }

    /// <summary>Binds opaque slot ids once with a context that is cleared on both success and failure.</summary>
    internal object? Bind(Type type, JsonObject buffer, IReadOnlyList<IConfigSecretValue> slots)
    {
        var context = slots.ToList();
        try
        {
            var options = new JsonSerializerOptions(_structural);
            options.Converters.Insert(0, new SlotConverterFactory(context));
            options.MakeReadOnly();
            return buffer.Deserialize(type, options);
        }
        finally { context.Clear(); }
    }

    /// <summary>Constructs a typed wrapper without exposing a public value-bearing factory.</summary>
    internal static IConfigSecretValue CreateSecret(Type innerType, bool enabled, object? value, string? provider) =>
        (IConfigSecretValue)typeof(ConfigCompositionJsonContract).GetMethod(nameof(CreateTypedSecret), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(innerType).Invoke(null, [enabled, value, provider])!;

    private static IConfigSecretValue CreateTypedSecret<T>(bool enabled, object? value, string? provider) where T : notnull =>
        new Secret<T>(enabled, value is not null, value is null ? default : (T)value, provider);

    /// <summary>Creates only execution-local converters; never enters structural caches.</summary>
    private sealed class SlotConverterFactory(List<IConfigSecretValue> slots) : JsonConverterFactory
    {
        /// <inheritdoc />
        public override bool CanConvert(Type typeToConvert) => IsSecret(typeToConvert);
        /// <inheritdoc />
        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(SlotConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]), slots)!;
    }

    /// <summary>Reads an already-classified wrapper by opaque id and never accepts a descriptor or payload.</summary>
    private sealed class SlotConverter<T>(List<IConfigSecretValue> slots) : JsonConverter<Secret<T>> where T : notnull
    {
        /// <inheritdoc />
        public override Secret<T>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out var index)
                || index < 0 || index >= slots.Count || slots[index] is not Secret<T> secret)
                throw new JsonException("Invalid opaque secret slot.");
            return secret;
        }
        /// <inheritdoc />
        public override void Write(Utf8JsonWriter writer, Secret<T> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteBoolean(nameof(value.Enabled), value.Enabled);
            writer.WriteBoolean(nameof(value.HasValue), value.HasValue);
            writer.WriteString(nameof(value.ResolvedProvider), value.ResolvedProvider);
            writer.WriteEndObject();
        }
    }
}

/// <summary>A cached value-free root shape.</summary>
internal sealed record ConfigCompositionShape(Type ValueType, IReadOnlyList<ConfigSecretDestination> Secrets,
    IReadOnlyList<ConfigCompositionMember> Members, IReadOnlyList<ConfigShapeFailure> Failures);
/// <summary>A scalar secret's relative serialized path and types.</summary>
internal sealed record ConfigSecretDestination(IReadOnlyList<string> Members, Type WrapperType, Type InnerType);
/// <summary>An ordinary bindable member used for final environment composition.</summary>
internal sealed record ConfigCompositionMember(IReadOnlyList<string> Members, Type ValueType);
/// <summary>A structural no-I/O failure, relative to its requested root.</summary>
internal sealed record ConfigShapeFailure(IReadOnlyList<string> Members, string Code);
