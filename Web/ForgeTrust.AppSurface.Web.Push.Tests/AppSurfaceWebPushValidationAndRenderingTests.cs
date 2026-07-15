using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Web.Push.Tests;

public sealed class AppSurfaceWebPushValidationAndRenderingTests
{
    [Theory]
    [InlineData("a", true)]
    [InlineData("Primary.v1-key", true)]
    [InlineData("", false)]
    [InlineData("-leading", false)]
    [InlineData("contains space", false)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", true)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false)]
    public void SafeKeyId_EnforcesAlphabetAndBounds(string value, bool expected) =>
        Assert.Equal(expected, AppSurfaceWebPushValidation.IsSafeKeyId(value));

    [Fact]
    public void CanonicalBase64Url_RequiresExactLengthAlphabetAndEncoding()
    {
        var bytes = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
        var canonical = AppSurfaceWebPushValidation.Base64UrlEncode(bytes);

        Assert.True(AppSurfaceWebPushValidation.TryDecodeCanonicalBase64Url(canonical, 16, out var decoded));
        Assert.Equal(bytes, decoded);
        Assert.False(AppSurfaceWebPushValidation.TryDecodeCanonicalBase64Url(canonical + "=", 16, out _));
        Assert.False(AppSurfaceWebPushValidation.TryDecodeCanonicalBase64Url(canonical + "+", 16, out _));
        Assert.False(AppSurfaceWebPushValidation.TryDecodeCanonicalBase64Url(canonical, 15, out _));
        Assert.False(AppSurfaceWebPushValidation.TryDecodeCanonicalBase64Url(string.Empty, 0, out _));
    }

    [Fact]
    public void P256Validation_RequiresUncompressedCurvePointAndMatchingPrivateKey()
    {
        var first = CreateKeyPair();
        var second = CreateKeyPair();
        var invalidPoint = new byte[65];
        invalidPoint[0] = 4;

        Assert.True(AppSurfaceWebPushValidation.IsValidP256PublicKey(first.PublicKey));
        Assert.True(AppSurfaceWebPushValidation.IsMatchingVapidPair(first.PublicKey, first.PrivateKey));
        Assert.False(AppSurfaceWebPushValidation.IsMatchingVapidPair(first.PublicKey, second.PrivateKey));
        Assert.False(AppSurfaceWebPushValidation.IsValidP256PublicKey(
            AppSurfaceWebPushValidation.Base64UrlEncode(invalidPoint)));
        Assert.False(AppSurfaceWebPushValidation.IsValidP256PublicKey(
            AppSurfaceWebPushValidation.Base64UrlEncode(new byte[33])));
    }

    [Theory]
    [InlineData("mailto:push@example.test", true)]
    [InlineData("mailto:push+alerts@example.test", true)]
    [InlineData("https://push.example.test/contact", true)]
    [InlineData("mailto:", false)]
    [InlineData("mailto:local-only", false)]
    [InlineData("mailto:push @example.test", false)]
    [InlineData("mailto:push@example.test\r\n", false)]
    [InlineData("mailto:push%20alerts@example.test", false)]
    [InlineData("mailto:push@example.test?subject=hello", false)]
    [InlineData("mailto:push@example.test#team", false)]
    [InlineData(" https://push.example.test", false)]
    [InlineData("https://push.example.test/con tact", false)]
    [InlineData("http://push.example.test/contact", false)]
    [InlineData("https://user@push.example.test/contact", false)]
    [InlineData("https://push.example.test/contact#team", false)]
    public void SubjectValidation_EnforcesContactUriContract(string value, bool expected) =>
        Assert.Equal(expected, AppSurfaceWebPushValidation.IsValidSubject(value));

    [Theory]
    [InlineData("https://push.example.test", true)]
    [InlineData("https://push.example.test/", false)]
    [InlineData("https://PUSH.example.test", false)]
    [InlineData("https://push.example.test/path", false)]
    [InlineData("https://push.example.test?x=1", false)]
    [InlineData("https://push.example.test:443", false)]
    [InlineData("https://push.example.test:8443", false)]
    [InlineData("http://push.example.test", false)]
    [InlineData("https://user@push.example.test", false)]
    [InlineData("https://push.example.test/%2f", false)]
    public void AllowedOriginValidation_RequiresExactNormalizedHttpsOrigin(string value, bool expected)
    {
        var valid = AppSurfaceWebPushValidation.TryNormalizeAllowedOrigin(value, out var normalized);

        Assert.Equal(expected, valid);
        if (expected)
        {
            Assert.Equal(value, normalized);
        }
    }

    [Theory]
    [InlineData("https://push.example.test/send", true)]
    [InlineData("https://push.example.test/send?token=value", true)]
    [InlineData("https://other.example.test/send", false)]
    [InlineData("http://push.example.test/send", false)]
    [InlineData("https://push.example.test:8443/send", false)]
    [InlineData("https://user@push.example.test/send", false)]
    [InlineData("https://push.example.test/send#fragment", false)]
    public void EndpointValidation_RequiresAllowedExactOrigin(string value, bool expected)
    {
        var origins = new HashSet<string>(StringComparer.Ordinal) { "https://push.example.test" };

        var valid = AppSurfaceWebPushValidation.TryValidateEndpoint(value, origins, out var origin);

        Assert.Equal(expected, valid);
        if (expected)
        {
            Assert.Equal("https://push.example.test", origin);
        }
    }

    [Fact]
    public void EndpointValidation_RejectsOversizeValue()
    {
        var origins = new HashSet<string>(StringComparer.Ordinal) { "https://push.example.test" };
        var endpoint = "https://push.example.test/" + new string('a', 4096);

        Assert.False(AppSurfaceWebPushValidation.TryValidateEndpoint(endpoint, origins, out _));
    }

    [Theory]
    [InlineData("/icons/push.svg", true, true)]
    [InlineData("/account/push?tab=alerts", false, true)]
    [InlineData("/account/push?first=1?second=2", false, false)]
    [InlineData("//evil.example/path", false, false)]
    [InlineData("/account/../admin", false, false)]
    [InlineData("/account/%252e%252e/admin", false, false)]
    [InlineData("/%2F%2Fevil", false, false)]
    [InlineData("/account/%GG", false, false)]
    [InlineData("/account\\admin", false, false)]
    [InlineData("/account/{id}", false, false)]
    [InlineData("/account/push#fragment", false, false)]
    [InlineData("relative/path", false, false)]
    public void AppRelativePaths_RejectEscapesTraversalAndAuthorityForms(
        string value,
        bool validAsset,
        bool validDestination)
    {
        Assert.Equal(validAsset, AppSurfaceWebPushValidation.IsValidAssetPath(value));
        Assert.Equal(validDestination, AppSurfaceWebPushValidation.IsValidDestinationPath(value));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("topic_01-safe", true)]
    [InlineData("", false)]
    [InlineData("contains space", false)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", true)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false)]
    public void TopicValidation_EnforcesUrlSafeBounds(string? value, bool expected) =>
        Assert.Equal(expected, AppSurfaceWebPushValidation.IsValidTopic(value));

    [Fact]
    public void SensitiveModels_RejectNullRequiredInputsAndRedactValues()
    {
        Assert.Throws<ArgumentNullException>(() => new AppSurfaceWebPushSubscription(null!, "p256dh", "auth", "primary"));
        Assert.Throws<ArgumentNullException>(() => new AppSurfaceWebPushSubscription("endpoint", null!, "auth", "primary"));
        Assert.Throws<ArgumentNullException>(() => new AppSurfaceWebPushSubscription("endpoint", "p256dh", null!, "primary"));
        Assert.Throws<ArgumentNullException>(() => new AppSurfaceWebPushSubscription("endpoint", "p256dh", "auth", null!));
        Assert.Throws<ArgumentNullException>(() => new AppSurfaceWebPushSubscriptionReference(null!));
        Assert.Throws<ArgumentNullException>(() => new AppSurfaceWebPushNotification(null!));

        var subscription = new AppSurfaceWebPushSubscription("private-endpoint", "private-p256dh", "private-auth", "primary");
        var reference = new AppSurfaceWebPushSubscriptionReference("private-endpoint");
        var key = new AppSurfaceWebPushVapidKeyOptions
        {
            PublicKey = "private-public-key",
            PrivateKey = "private-private-key",
        };

        Assert.Equal("AppSurfaceWebPushSubscription { Redacted = true }", subscription.ToString());
        Assert.Equal("AppSurfaceWebPushSubscriptionReference { Redacted = true }", reference.ToString());
        Assert.Equal("AppSurfaceWebPushVapidKeyOptions { Redacted = true }", key.ToString());
    }

    [Fact]
    public void SendRequest_RejectsNullComponents()
    {
        var subscription = new AppSurfaceWebPushSubscription("endpoint", "p256dh", "auth", "primary");
        var notification = new AppSurfaceWebPushNotification("title");
        var options = new AppSurfaceWebPushSendOptions(60);

        Assert.Throws<ArgumentNullException>(() => new AppSurfaceWebPushSendRequest(null!, notification, options));
        Assert.Throws<ArgumentNullException>(() => new AppSurfaceWebPushSendRequest(subscription, null!, options));
        Assert.Throws<ArgumentNullException>(() => new AppSurfaceWebPushSendRequest(subscription, notification, null!));
    }

    [Fact]
    public void ClientTagHelper_RendersVersionedScriptUnderRequestPathBase()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.PathBase = "/tenant";
        var helper = new AppSurfaceWebPushClientTagHelper
        {
            ViewContext = new ViewContext { HttpContext = httpContext },
        };
        var context = CreateTagHelperContext();
        var output = CreateTagHelperOutput();

        helper.Process(context, output);

        Assert.Equal("script", output.TagName);
        Assert.Equal(TagMode.StartTagAndEndTag, output.TagMode);
        Assert.Equal(
            $"/tenant{AppSurfaceWebPushClientAsset.Path}?v={AppSurfaceWebPushClientAsset.Version}",
            output.Attributes["src"].Value);
    }

    [Fact]
    public void ClientTagHelper_RejectsNullTagHelperArguments()
    {
        var helper = new AppSurfaceWebPushClientTagHelper();

        Assert.Throws<ArgumentNullException>(() => helper.Process(null!, CreateTagHelperOutput()));
        Assert.Throws<ArgumentNullException>(() => helper.Process(CreateTagHelperContext(), null!));
    }

    [Fact]
    public async Task DevelopmentProofTransport_IsDevelopmentOnlyAndRunsGuardedClassification()
    {
        var production = new ProofEnvironment(Environments.Production);
        Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddAppSurfaceWebPushDevelopmentProofTransport(production));

        var services = new ServiceCollection();
        var returned = services.AddAppSurfaceWebPushDevelopmentProofTransport(
            new ProofEnvironment(Environments.Development));
        using var provider = services.BuildServiceProvider();
        var vapid = CreateKeyPair();
        using var subscriptionKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var subscriptionParameters = subscriptionKey.ExportParameters(false);
        var subscriptionPublicKey = new byte[65];
        subscriptionPublicKey[0] = 4;
        subscriptionParameters.Q.X!.CopyTo(subscriptionPublicKey, 1);
        subscriptionParameters.Q.Y!.CopyTo(subscriptionPublicKey, 33);

        var response = await provider.GetRequiredService<GuardedWebPushAdapter>().SendAsync(
            new GuardedWebPushRequest(
                "https://push.example.test/send",
                AppSurfaceWebPushValidation.Base64UrlEncode(subscriptionPublicKey),
                AppSurfaceWebPushValidation.Base64UrlEncode(RandomNumberGenerator.GetBytes(16)),
                "{\"version\":1,\"title\":\"Proof\"}",
                60,
                GuardedWebPushUrgency.Normal,
                null,
                "mailto:push@example.test",
                vapid.PublicKey,
                vapid.PrivateKey),
            CancellationToken.None);

        Assert.Same(services, returned);
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
    }

    private static TagHelperContext CreateTagHelperContext() =>
        new(new TagHelperAttributeList(), new Dictionary<object, object>(), "web-push-test");

    private static TagHelperOutput CreateTagHelperOutput() =>
        new(
            "appsurface:web-push-client",
            new TagHelperAttributeList(),
            static (_, _) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));

    private static (string PublicKey, string PrivateKey) CreateKeyPair()
    {
        using var algorithm = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = algorithm.ExportParameters(true);
        var publicKey = new byte[65];
        publicKey[0] = 4;
        parameters.Q.X!.CopyTo(publicKey, 1);
        parameters.Q.Y!.CopyTo(publicKey, 33);
        return (
            AppSurfaceWebPushValidation.Base64UrlEncode(publicKey),
            AppSurfaceWebPushValidation.Base64UrlEncode(parameters.D!));
    }

    private sealed class ProofEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "AppSurface.Web.Push.Tests";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
