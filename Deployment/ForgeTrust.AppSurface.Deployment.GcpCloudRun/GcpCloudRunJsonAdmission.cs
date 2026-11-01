using System.Text.Json;

namespace ForgeTrust.AppSurface.Deployment.GcpCloudRun;

/// <summary>Applies finite collection bounds to JSON admitted by the execution observer.</summary>
/// <remarks>The bound limits parser memory exposure at the observer trust boundary. It is not a Cloud Run provider semantic limit and is deliberately not used by legacy verification.</remarks>
internal static class GcpCloudRunJsonAdmission
{
    /// <summary>The inclusive maximum number of direct properties or array elements in one JSON collection.</summary>
    internal const int MaxCollectionEntries = 4096;

    /// <summary>Rejects an object or array whose direct collection exceeds the observer admission bound.</summary>
    /// <param name="root">The already parsed JSON root.</param>
    /// <exception cref="JsonException">Thrown when a collection exceeds the finite admission bound.</exception>
    internal static void ValidateFiniteCollections(JsonElement root)
    {
        Visit(root);
    }

    private static void Visit(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var count = 0;
            foreach (var property in value.EnumerateObject())
            {
                if (++count > MaxCollectionEntries)
                    throw new JsonException("JSON object exceeds the finite observer collection bound.");
                Visit(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var count = 0;
            foreach (var item in value.EnumerateArray())
            {
                if (++count > MaxCollectionEntries)
                    throw new JsonException("JSON array exceeds the finite observer collection bound.");
                Visit(item);
            }
        }
    }
}
