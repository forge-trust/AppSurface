using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;

namespace LocalSecretsExample;

[Command("show-secret-posture", Description = "Verifies that the Stripe API key resolved without printing its value.")]
public sealed partial class ShowSecretCommand(StripeApiKeyConfig stripeApiKey) : ICommand
{
    public ValueTask ExecuteAsync(IConsole console)
    {
        console.Output.WriteLine(stripeApiKey.HasValue
            ? "Stripe:ApiKey resolved from configuration. Value: [redacted]"
            : "Stripe:ApiKey did not resolve.");

        return default;
    }
}
