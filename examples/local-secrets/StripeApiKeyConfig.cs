using ForgeTrust.AppSurface.Config;

namespace LocalSecretsExample;

[ConfigKey("Stripe:ApiKey")]
[ConfigKeyRequired]
public sealed class StripeApiKeyConfig : Config<string>
{
}
