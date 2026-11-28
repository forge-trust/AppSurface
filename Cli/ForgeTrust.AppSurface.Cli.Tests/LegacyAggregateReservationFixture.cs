using System.Text;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

internal static class LegacyAggregateReservationFixture
{
    public static async Task SeedAsync(string root, string repositoryNamespace, string key, long reservedBytes,
        bool emptyBindings = false)
    {
        if (reservedBytes < 0) throw new ArgumentOutOfRangeException(nameof(reservedBytes));

        var indexPath = TestPathUtils.PathUnder(root, "quota-index.json");
        var index = JsonNode.Parse(await File.ReadAllBytesAsync(indexPath))?.AsObject()
            ?? throw new InvalidDataException("The quota index fixture is missing.");
        var entry = index["Entries"]?.AsArray().SingleOrDefault(item =>
                StringComparer.Ordinal.Equals(item?["RepositoryNamespace"]?.GetValue<string>(), repositoryNamespace)
                && StringComparer.Ordinal.Equals(item?["Key"]?.GetValue<string>(), key))?.AsObject()
            ?? throw new InvalidDataException("The quota index fixture does not contain the requested key.");

        entry["ReservedPayloadBytes"] = reservedBytes;
        if (emptyBindings) entry["PayloadReservations"] = new JsonArray();
        else entry.Remove("PayloadReservations");
        await File.WriteAllBytesAsync(indexPath, Encoding.UTF8.GetBytes(index.ToJsonString()));
    }
}
