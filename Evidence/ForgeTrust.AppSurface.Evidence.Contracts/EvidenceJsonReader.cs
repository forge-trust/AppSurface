using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace ForgeTrust.AppSurface.Evidence.Contracts;

/// <summary>
/// Enforces the canonical Evidence JSON input limits and validates the structure of typed contracts.
/// </summary>
internal static class EvidenceJsonReader
{
    /// <summary>Validates the input length and structure before returning the requested contract.</summary>
    /// <typeparam name="TValue">The requested value type.</typeparam>
    /// <param name="utf8Json">The JSON bytes held in memory.</param>
    /// <param name="maximumBytes">The limit for this document, within the canonical JSON ceiling.</param>
    /// <returns>The deserialized value.</returns>
    internal static TValue Deserialize<TValue>(ReadOnlySpan<byte> utf8Json, int maximumBytes)
    {
        ValidateMaximumBytes(maximumBytes);
        if (utf8Json.Length > maximumBytes)
        {
            throw new InvalidDataException("Evidence JSON exceeds the maximum input size.");
        }

        TValue? value;
        try
        {
            ValidateUniquePropertyNames(utf8Json);
            value = JsonSerializer.Deserialize<TValue>(utf8Json, DeserializerOptions);
        }
        catch (JsonException)
        {
            throw new JsonException("Evidence JSON is malformed or does not match the supported contract.");
        }

        if (value is null)
        {
            throw new JsonException("Evidence JSON is empty or does not contain the requested contract.");
        }

        return value;
    }

    /// <summary>Counts incoming bytes before parsing the JSON and leaves the supplied stream open.</summary>
    /// <typeparam name="TValue">The requested value type.</typeparam>
    /// <param name="utf8Json">The caller-owned source stream.</param>
    /// <param name="maximumBytes">The limit for this document, within the canonical JSON ceiling.</param>
    /// <param name="cancellationToken">The token that cancels the read.</param>
    /// <returns>The deserialized value.</returns>
    internal static async ValueTask<TValue> DeserializeAsync<TValue>(
        Stream utf8Json,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(utf8Json);
        ValidateMaximumBytes(maximumBytes);

        var bytes = await ReadBoundedInputAsync(utf8Json, maximumBytes, cancellationToken).ConfigureAwait(false);
        return Deserialize<TValue>(bytes, maximumBytes);
    }

    private static readonly JsonSerializerOptions DeserializerOptions = CreateDeserializerOptions();

    private static JsonSerializerOptions CreateDeserializerOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(static typeInfo =>
        {
            if (typeInfo.Kind == JsonTypeInfoKind.Object
                && typeInfo.Type.Assembly == typeof(EvidencePolicy).Assembly)
            {
                // Validate each typed contract when it is materialized, including contracts inside
                // caller-owned arrays, dictionaries or wrappers. Root-only checks miss those values.
                typeInfo.OnDeserialized = static value =>
                {
                    ValidateCollectionItems(value);
                    ValidateContractVersion(value);
                };
            }
        });
        return new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            MaxDepth = EvidenceCanonicalJson.MaximumDepth,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            TypeInfoResolver = resolver,
            Converters = { new StrictStringEnumConverterFactory() },
        };
    }

    private static async ValueTask<byte[]> ReadBoundedInputAsync(
        Stream source,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        const int BufferSize = 81920;
        var buffer = new byte[BufferSize];
        using var output = new MemoryStream(Math.Min(maximumBytes, BufferSize));

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytesRemaining = maximumBytes - (int)output.Length;
            var requestedBytes = Math.Min(buffer.Length, bytesRemaining + 1);
            int bytesRead;
            try
            {
                bytesRead = await source.ReadAsync(buffer.AsMemory(0, requestedBytes), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or NotSupportedException or ObjectDisposedException)
            {
                throw new InvalidDataException("Evidence JSON input could not be read.");
            }

            if (bytesRead == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return output.ToArray();
            }

            if (bytesRead > bytesRemaining)
            {
                throw new InvalidDataException("Evidence JSON exceeds the maximum input size.");
            }

            output.Write(buffer, 0, bytesRead);
        }
    }

    private static void ValidateMaximumBytes(int maximumBytes)
    {
        if (maximumBytes < 0 || maximumBytes > EvidenceCanonicalJson.MaximumInputBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumBytes),
                $"The input limit must be between zero and {EvidenceCanonicalJson.MaximumInputBytes} bytes.");
        }
    }

    private static void ValidateUniquePropertyNames(ReadOnlySpan<byte> utf8Json)
    {
        var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions { MaxDepth = EvidenceCanonicalJson.MaximumDepth });
        var objectScopes = new Stack<HashSet<string>?>();
        var reusableSets = new Stack<HashSet<string>>();

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    objectScopes.Push(null);
                    break;
                case JsonTokenType.PropertyName:
                    {
                        var properties = objectScopes.Pop();
                        properties ??= reusableSets.Count > 0
                            ? reusableSets.Pop()
                            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        string propertyName;
                        try
                        {
                            propertyName = reader.GetString()!;
                        }
                        catch (InvalidOperationException)
                        {
                            // GetString uses this exception for invalid UTF-8 and unpaired surrogate escapes.
                            throw new JsonException("Evidence JSON contains an invalid property name.");
                        }

                        if (!properties.Add(propertyName))
                        {
                            throw new JsonException("Evidence JSON contains duplicate or case-colliding object properties.");
                        }

                        objectScopes.Push(properties);
                        break;
                    }
                case JsonTokenType.EndObject:
                    {
                        var properties = objectScopes.Pop();
                        if (properties is not null)
                        {
                            properties.Clear();
                            reusableSets.Push(properties);
                        }

                        break;
                    }
            }
        }
    }

    private static void ValidateContractVersion<TValue>(TValue value)
    {
        if (value is EvidencePlan plan && !string.Equals(plan.ContractVersion, EvidencePlan.CurrentContractVersion, StringComparison.Ordinal))
        {
            throw new JsonException("Evidence plan contract version is missing or unsupported.");
        }

        if (value is EvidenceManifest manifest && !string.Equals(manifest.ContractVersion, EvidencePlan.CurrentContractVersion, StringComparison.Ordinal))
        {
            throw new JsonException("Evidence manifest contract version is missing or unsupported.");
        }
    }

    // System.Text.Json validates required members, but nullable annotations do not cover collection elements.
    private static void ValidateCollectionItems(object value)
    {
        switch (value)
        {
            case EvidencePolicy policy:
                ValidateItems(policy.Profiles);
                ValidateItems(policy.Rules);
                break;
            case EvidenceProfile profile:
                ValidateItems(profile.Resources);
                ValidateItems(profile.Producers);
                ValidateItems(profile.Obligations);
                break;
            case EvidenceResourceDeclaration resource:
                ValidateItems(resource.Requires);
                break;
            case EvidenceProducerDeclaration producer:
                ValidateItems(producer.RequiredResources);
                ValidateItems(producer.AssertionIds);
                ValidateItems(producer.ArtifactSlots);
                break;
            case EvidenceTestDeclaration test:
                ValidateItems(test.Projects);
                break;
            case EvidenceObligation obligation:
                ValidateItems(obligation.RequiredProducerIds);
                break;
            case EvidencePlan plan:
                ValidateItems(plan.ChangedPaths);
                ValidateItems(plan.MatchedRuleIds);
                break;
            case EvidenceManifest manifest:
                ValidateItems(manifest.ResourceResults);
                ValidateItems(manifest.SelectedObligationIds);
                ValidateItems(manifest.ClosedObligationIds);
                ValidateItems(manifest.UnmediatedObligationIds);
                ValidateItems(manifest.ProducerResults);
                break;
            case EvidenceProducerResult result:
                ValidateItems(result.SatisfiedAssertionIds);
                if (result.Artifacts is not null)
                {
                    ValidateItems(result.Artifacts);
                }

                break;
        }
    }

    private static void ValidateItems<TItem>(IEnumerable<TItem> items)
        where TItem : notnull
    {
        foreach (var item in items)
        {
            if (item is null)
            {
                throw new JsonException("Evidence JSON contains a null item in a required contract collection.");
            }
        }
    }

    private sealed class StrictStringEnumConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        {
            var converterType = typeof(StrictStringEnumConverter<>).MakeGenericType(typeToConvert);
            return (JsonConverter)Activator.CreateInstance(converterType)!;
        }
    }

    private sealed class StrictStringEnumConverter<TEnum> : JsonConverter<TEnum>
        where TEnum : struct, Enum
    {
        public StrictStringEnumConverter()
        {
        }

        public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException();
            }

            var name = reader.GetString();
            if (name is null
                || !Enum.GetNames<TEnum>().Contains(name, StringComparer.OrdinalIgnoreCase)
                || !Enum.TryParse<TEnum>(name, ignoreCase: true, out var value))
            {
                throw new JsonException();
            }

            return value;
        }

        public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
        {
            throw new NotSupportedException("The strict enum converter is for Evidence JSON input only.");
        }
    }
}
