using System.Text.Json;
using ForgeTrust.AppSurface.Web.Push;

namespace ForgeTrust.AppSurface.Web.Push.Tests;

public sealed class AppSurfaceWebPushPathParityTests
{
    [Fact]
    public void SenderValidation_FollowsSharedWorkerPathVectors()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "pwa-path-vectors.json")));

        AssertVectors(
            document.RootElement.GetProperty("assetPaths"),
            AppSurfaceWebPushValidation.IsValidAssetPath);
        AssertVectors(
            document.RootElement.GetProperty("destinationPaths"),
            AppSurfaceWebPushValidation.IsValidDestinationPath);
    }

    private static void AssertVectors(JsonElement vectors, Func<string?, bool> validate)
    {
        foreach (var vector in vectors.EnumerateArray())
        {
            var value = vector.GetProperty("value").GetString();
            var expected = vector.GetProperty("valid").GetBoolean();
            Assert.Equal(expected, validate(value));
        }
    }
}
