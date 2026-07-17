using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using ForgeTrust.AppSurface.Web.Push;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Web.Push.Tests;

public sealed class AppSurfaceWebPushOptionsAndSenderTests
{
    [Fact]
    public void OptionsValidator_ReportsMissingCollectionsAndUnsafeKeyIds()
    {
        var validator = new AppSurfaceWebPushOptionsValidator();
        var empty = validator.Validate(null, new AppSurfaceWebPushOptions());
        var unsafeKey = CreateOptions();
        var key = unsafeKey.VapidKeys["primary"];
        unsafeKey.VapidKeys.Clear();
        unsafeKey.VapidKeys.Add("unsafe key", key);
        unsafeKey.ActiveVapidKeyId = "unsafe key";

        var unsafeResult = validator.Validate(null, unsafeKey);
        Assert.NotNull(empty.Failures);
        Assert.NotNull(unsafeResult.Failures);

        Assert.Contains(empty.Failures!, failure => failure.StartsWith("ASPUSHCFG001", StringComparison.Ordinal));
        Assert.Contains(empty.Failures!, failure => failure.StartsWith("ASPUSHCFG002", StringComparison.Ordinal));
        Assert.Contains(empty.Failures!, failure => failure.StartsWith("ASPUSHCFG006", StringComparison.Ordinal));
        Assert.Contains(unsafeResult.Failures!, failure => failure.StartsWith("ASPUSHCFG003", StringComparison.Ordinal));
    }

    [Fact]
    public void OptionsValidator_AcceptsMatchingP256PairAndExactOrigin()
    {
        var options = CreateOptions();

        var result = new AppSurfaceWebPushOptionsValidator().Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("https://push.example.test/")]
    [InlineData("https://push.example.test/path")]
    [InlineData("https://*.example.test")]
    [InlineData("https://push.example.test:8443")]
    [InlineData("http://push.example.test")]
    [InlineData("https://user@push.example.test")]
    [InlineData("https://push.example.test?query=1")]
    public void OptionsValidator_RejectsNonExactOriginWithoutEchoingSecrets(string origin)
    {
        var options = CreateOptions();
        options.AllowedPushServiceOrigins.Clear();
        options.AllowedPushServiceOrigins.Add(origin);
        var privateKey = options.VapidKeys["primary"].PrivateKey!;

        var result = new AppSurfaceWebPushOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.DoesNotContain(privateKey, string.Join(' ', result.Failures));
    }

    [Fact]
    public void OptionsValidator_RejectsMismatchedKeysAndUsesOnlySafeId()
    {
        var options = CreateOptions();
        options.VapidKeys["primary"].PrivateKey = CreateKeyPair(ECDsa.Create).PrivateKey;

        var result = new AppSurfaceWebPushOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        var failure = Assert.Single(result.Failures, value => value.Contains("ASPUSHCFG005", StringComparison.Ordinal));
        Assert.Contains("primary", failure, StringComparison.Ordinal);
        Assert.DoesNotContain(options.VapidKeys["primary"].PublicKey!, failure, StringComparison.Ordinal);
        Assert.DoesNotContain(options.VapidKeys["primary"].PrivateKey!, failure, StringComparison.Ordinal);
    }

    [Fact]
    public void OptionsValidator_RejectsInvalidSubject()
    {
        var options = CreateOptions();
        options.VapidKeys["primary"].Subject = "mailto:";

        var result = new AppSurfaceWebPushOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.StartsWith("ASPUSHCFG004", StringComparison.Ordinal));
    }

    [Fact]
    public void SensitiveModels_RedactStringRepresentations()
    {
        var subscription = CreateSubscription();
        var notification = new AppSurfaceWebPushNotification("private title", body: "private body");
        var request = new AppSurfaceWebPushSendRequest(
            subscription,
            notification,
            new AppSurfaceWebPushSendOptions(60));

        Assert.DoesNotContain(subscription.Endpoint, subscription.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(subscription.Auth, subscription.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(subscription.VapidKeyId, subscription.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private title", notification.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private body", request.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sender_RejectsDisallowedStoredOriginWithoutNetwork()
    {
        var handler = new StatusHandler(HttpStatusCode.Created);
        var options = CreateOptions();
        var sender = CreateSender(handler, options);
        var request = CreateSendRequest(CreateSubscription("https://evil.example.test/send"));

        var result = await sender.SendAsync(request);

        Assert.Equal(AppSurfaceWebPushSendOutcome.PushServiceNotAllowed, result.Outcome);
        Assert.Equal(0, handler.Attempts);
    }

    [Fact]
    public async Task Sender_RejectsMissingRetainedKeyWithoutNetwork()
    {
        var handler = new StatusHandler(HttpStatusCode.Created);
        var options = CreateOptions();
        options.VapidKeys.Remove("primary");
        var sender = CreateSender(handler, options);

        var result = await sender.SendAsync(CreateSendRequest(CreateSubscription()));

        Assert.Equal(AppSurfaceWebPushSendOutcome.VapidKeyUnavailable, result.Outcome);
        Assert.Equal("ASPUSHSEND002", result.ReasonCode);
        Assert.Equal(0, handler.Attempts);
    }

    [Fact]
    public async Task Sender_ClassifiesNetworkFailureAsTransient()
    {
        var sender = CreateSender(new ThrowingHandler(), CreateOptions());

        var result = await sender.SendAsync(CreateSendRequest(CreateSubscription()));

        Assert.Equal(AppSurfaceWebPushSendOutcome.TransientFailure, result.Outcome);
        Assert.Equal("ASPUSHSEND004", result.ReasonCode);
        Assert.Null(result.StatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.Created, AppSurfaceWebPushSendOutcome.Accepted)]
    [InlineData(HttpStatusCode.OK, AppSurfaceWebPushSendOutcome.ProtocolFailure)]
    [InlineData(HttpStatusCode.Redirect, AppSurfaceWebPushSendOutcome.ProtocolFailure)]
    [InlineData(HttpStatusCode.BadRequest, AppSurfaceWebPushSendOutcome.Rejected)]
    [InlineData(HttpStatusCode.RequestTimeout, AppSurfaceWebPushSendOutcome.TransientFailure)]
    [InlineData(HttpStatusCode.TooManyRequests, AppSurfaceWebPushSendOutcome.TransientFailure)]
    [InlineData(HttpStatusCode.InternalServerError, AppSurfaceWebPushSendOutcome.TransientFailure)]
    public async Task Sender_ClassifiesOneAttempt(HttpStatusCode status, AppSurfaceWebPushSendOutcome expected)
    {
        var handler = new StatusHandler(status);
        var sender = CreateSender(handler, CreateOptions());

        var result = await sender.SendAsync(CreateSendRequest(CreateSubscription()));

        Assert.Equal(expected, result.Outcome);
        Assert.Equal((int)status, result.StatusCode);
        Assert.Null(result.RetryAfter);
        Assert.Equal("primary", result.VapidKeyId);
        Assert.Equal(1, handler.Attempts);
        Assert.Equal(AppSurfaceWebPushCleanupState.NotRequired, result.CleanupState);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, AppSurfaceWebPushTerminalReason.NotFound)]
    [InlineData(HttpStatusCode.Gone, AppSurfaceWebPushTerminalReason.Gone)]
    public async Task Sender_TerminalResponseCleansUpCompleteSnapshot(
        HttpStatusCode status,
        AppSurfaceWebPushTerminalReason reason)
    {
        var custody = new RecordingCustody();
        var subscription = CreateSubscription();
        var sender = CreateSender(new StatusHandler(status), CreateOptions(), custody);

        var result = await sender.SendAsync(CreateSendRequest(subscription));

        Assert.Equal(AppSurfaceWebPushSendOutcome.TerminalSubscription, result.Outcome);
        Assert.Equal(AppSurfaceWebPushCleanupState.Completed, result.CleanupState);
        Assert.Same(subscription, custody.TerminalSubscription);
        Assert.Equal(reason, custody.TerminalReason);
    }

    [Theory]
    [InlineData(AppSurfaceWebPushTerminalDisposition.AlreadyTerminal, AppSurfaceWebPushCleanupState.AlreadyTerminal)]
    [InlineData(AppSurfaceWebPushTerminalDisposition.Rejected, AppSurfaceWebPushCleanupState.Rejected)]
    public async Task Sender_PreservesSafeTerminalCleanupDisposition(
        AppSurfaceWebPushTerminalDisposition disposition,
        AppSurfaceWebPushCleanupState expected)
    {
        var custody = new RecordingCustody(disposition);
        var sender = CreateSender(new StatusHandler(HttpStatusCode.Gone), CreateOptions(), custody);

        var result = await sender.SendAsync(CreateSendRequest(CreateSubscription()));

        Assert.Equal(expected, result.CleanupState);
    }

    [Fact]
    public async Task Sender_PropagatesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var sender = CreateSender(new StatusHandler(HttpStatusCode.Created), CreateOptions());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await sender.SendAsync(CreateSendRequest(CreateSubscription()), cancellation.Token));
    }

    [Fact]
    public async Task Sender_RejectsOversizeUtf8PayloadBeforeNetwork()
    {
        var handler = new StatusHandler(HttpStatusCode.Created);
        var sender = CreateSender(handler, CreateOptions());
        var notification = new AppSurfaceWebPushNotification(
            new string('\u00e9', 256),
            body: new string('\u00e9', 2048));
        var request = new AppSurfaceWebPushSendRequest(
            CreateSubscription(),
            notification,
            new AppSurfaceWebPushSendOptions(60));

        await Assert.ThrowsAsync<ArgumentException>(async () => await sender.SendAsync(request));
        Assert.Equal(0, handler.Attempts);
    }

    [Theory]
    [InlineData("title-empty")]
    [InlineData("title-long")]
    [InlineData("body-empty")]
    [InlineData("body-long")]
    [InlineData("tag-empty")]
    [InlineData("tag-long")]
    [InlineData("icon-absolute")]
    [InlineData("badge-network-path")]
    [InlineData("destination-traversal")]
    [InlineData("ttl-zero")]
    [InlineData("ttl-too-large")]
    [InlineData("urgency-undefined")]
    [InlineData("topic-empty")]
    [InlineData("topic-long")]
    [InlineData("topic-invalid")]
    public async Task Sender_RejectsInvalidRequestFieldBeforeNetwork(string scenario)
    {
        var handler = new StatusHandler(HttpStatusCode.Created);
        var sender = CreateSender(handler, CreateOptions());
        var subscription = CreateSubscription();
        var notification = scenario switch
        {
            "title-empty" => new AppSurfaceWebPushNotification(string.Empty),
            "title-long" => new AppSurfaceWebPushNotification(new string('t', 257)),
            "body-empty" => new AppSurfaceWebPushNotification("title", body: string.Empty),
            "body-long" => new AppSurfaceWebPushNotification("title", body: new string('b', 2049)),
            "tag-empty" => new AppSurfaceWebPushNotification("title", tag: string.Empty),
            "tag-long" => new AppSurfaceWebPushNotification("title", tag: new string('t', 129)),
            "icon-absolute" => new AppSurfaceWebPushNotification("title", iconPath: "https://example.test/icon.png"),
            "badge-network-path" => new AppSurfaceWebPushNotification("title", badgePath: "//example.test/badge.png"),
            "destination-traversal" => new AppSurfaceWebPushNotification("title", destinationPath: "/account/../admin"),
            _ => new AppSurfaceWebPushNotification("title"),
        };
        var options = scenario switch
        {
            "ttl-zero" => new AppSurfaceWebPushSendOptions(0),
            "ttl-too-large" => new AppSurfaceWebPushSendOptions(2_419_201),
            "urgency-undefined" => new AppSurfaceWebPushSendOptions(60, (AppSurfaceWebPushUrgency)99),
            "topic-empty" => new AppSurfaceWebPushSendOptions(60, topic: string.Empty),
            "topic-long" => new AppSurfaceWebPushSendOptions(60, topic: new string('a', 33)),
            "topic-invalid" => new AppSurfaceWebPushSendOptions(60, topic: "not+safe"),
            _ => new AppSurfaceWebPushSendOptions(60),
        };

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await sender.SendAsync(new AppSurfaceWebPushSendRequest(subscription, notification, options)));
        Assert.Equal(0, handler.Attempts);
    }

    [Theory]
    [InlineData("key-id")]
    [InlineData("p256dh")]
    [InlineData("auth")]
    public async Task Sender_RejectsInvalidSubscriptionKeyMaterialBeforeNetwork(string field)
    {
        var handler = new StatusHandler(HttpStatusCode.Created);
        var sender = CreateSender(handler, CreateOptions());
        var valid = CreateSubscription();
        var subscription = new AppSurfaceWebPushSubscription(
            valid.Endpoint,
            field == "p256dh" ? "invalid" : valid.P256Dh,
            field == "auth" ? "invalid" : valid.Auth,
            field == "key-id" ? "unsafe key" : valid.VapidKeyId);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await sender.SendAsync(CreateSendRequest(subscription)));
        Assert.Equal(0, handler.Attempts);
    }

    [Fact]
    public async Task Sender_ClassifiesUnexpectedAdapterExceptionAsProtocolFailure()
    {
        var sender = CreateSender(new ProtocolFailureHandler(), CreateOptions());

        var result = await sender.SendAsync(CreateSendRequest(CreateSubscription()));

        Assert.Equal(AppSurfaceWebPushSendOutcome.ProtocolFailure, result.Outcome);
        Assert.Equal("ASPUSHSEND003", result.ReasonCode);
        Assert.Null(result.StatusCode);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("throws")]
    [InlineData("cancels")]
    [InlineData("unknown")]
    public async Task Sender_TerminalCleanupFailuresRemainSafe(string scenario)
    {
        IAppSurfaceWebPushSubscriptionCustody? custody = scenario switch
        {
            "throws" => new FailingTerminalCustody(cancel: false),
            "cancels" => new FailingTerminalCustody(cancel: true),
            "unknown" => new RecordingCustody((AppSurfaceWebPushTerminalDisposition)999),
            _ => null,
        };
        var sender = CreateSender(new StatusHandler(HttpStatusCode.Gone), CreateOptions(), custody);

        var result = await sender.SendAsync(CreateSendRequest(CreateSubscription()));

        Assert.Equal(AppSurfaceWebPushSendOutcome.TerminalSubscription, result.Outcome);
        Assert.Equal(AppSurfaceWebPushCleanupState.Failed, result.CleanupState);
    }

    [Fact]
    public async Task Sender_DoesNotInvokeCleanupCancellationCallbacksAfterSuccess()
    {
        var custody = new CancellationCallbackCustody();
        var sender = CreateSender(new StatusHandler(HttpStatusCode.Gone), CreateOptions(), custody);
        var send = sender.SendAsync(CreateSendRequest(CreateSubscription())).AsTask();

        var completed = await Task.WhenAny(send, Task.Delay(TimeSpan.FromSeconds(1)));
        try
        {
            Assert.Same(send, completed);
            Assert.Equal(AppSurfaceWebPushCleanupState.Completed, (await send).CleanupState);
            Assert.False(custody.CallbackInvoked);
        }
        finally
        {
            custody.ReleaseCallback();
        }
    }

    [Fact]
    public async Task Sender_TimedOutCleanupRetainsDedicatedScopeUntilBackgroundWorkEnds()
    {
        var custody = new SlowDisposableCustody();
        var services = new ServiceCollection();
        services.AddScoped<IAppSurfaceWebPushSubscriptionCustody>(_ => custody);
        await using var provider = services.BuildServiceProvider();
        var sender = new AppSurfaceWebPushSender(
            Options.Create(CreateOptions()),
            new GuardedWebPushAdapter(new StatusHandler(HttpStatusCode.Gone)),
            provider.GetRequiredService<IServiceScopeFactory>());

        var send = sender.SendAsync(CreateSendRequest(CreateSubscription())).AsTask();
        await custody.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var result = await send;

        Assert.Equal(AppSurfaceWebPushCleanupState.Failed, result.CleanupState);
        Assert.False(custody.Disposed.Task.IsCompleted);

        custody.Release.TrySetResult();
        await custody.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Sender_HardTimeoutDoesNotWaitForBlockingCancellationCallback()
    {
        var custody = new BlockingCancellationCustody();
        var sender = CreateSender(new StatusHandler(HttpStatusCode.Gone), CreateOptions(), custody);
        var send = sender.SendAsync(CreateSendRequest(CreateSubscription())).AsTask();
        await custody.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        try
        {
            var result = await send.WaitAsync(TimeSpan.FromSeconds(7));

            Assert.Equal(AppSurfaceWebPushCleanupState.Failed, result.CleanupState);
            await custody.CallbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            await custody.ReleaseCallbackAsync();
        }
    }

    private static AppSurfaceWebPushSender CreateSender(
        HttpMessageHandler handler,
        AppSurfaceWebPushOptions options,
        IAppSurfaceWebPushSubscriptionCustody? custody = null)
    {
        var services = new ServiceCollection();
        if (custody is not null)
        {
            services.AddSingleton(custody);
        }

        return new AppSurfaceWebPushSender(
            Options.Create(options),
            new GuardedWebPushAdapter(handler),
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
    }

    private static AppSurfaceWebPushOptions CreateOptions()
    {
        var keys = CreateKeyPair(ECDsa.Create);
        var options = new AppSurfaceWebPushOptions { ActiveVapidKeyId = "primary" };
        options.VapidKeys.Add("primary", new AppSurfaceWebPushVapidKeyOptions
        {
            Subject = "mailto:push@example.test",
            PublicKey = keys.PublicKey,
            PrivateKey = keys.PrivateKey,
        });
        options.AllowedPushServiceOrigins.Add("https://push.example.test");
        return options;
    }

    private static AppSurfaceWebPushSubscription CreateSubscription(
        string endpoint = "https://push.example.test/send")
    {
        var subscription = CreateKeyPair(ECDiffieHellman.Create);
        return new AppSurfaceWebPushSubscription(
            endpoint,
            subscription.PublicKey,
            Base64UrlEncode(RandomNumberGenerator.GetBytes(16)),
            "primary");
    }

    private static AppSurfaceWebPushSendRequest CreateSendRequest(AppSurfaceWebPushSubscription subscription) =>
        new(
            subscription,
            new AppSurfaceWebPushNotification("Hello", destinationPath: "/account/push"),
            new AppSurfaceWebPushSendOptions(60, topic: "account"));

    private static (string PublicKey, string PrivateKey) CreateKeyPair<T>(Func<T> factory)
        where T : ECAlgorithm
    {
        using var algorithm = factory();
        algorithm.GenerateKey(ECCurve.NamedCurves.nistP256);
        var parameters = algorithm.ExportParameters(true);
        var publicKey = new byte[65];
        publicKey[0] = 4;
        parameters.Q.X!.CopyTo(publicKey, 1);
        parameters.Q.Y!.CopyTo(publicKey, 33);
        return (Base64UrlEncode(publicKey), Base64UrlEncode(parameters.D!));
    }

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class StatusHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        private int attempts;

        public int Attempts => attempts;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref attempts);
            return Task.FromResult(new HttpResponseMessage(statusCode));
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("hostile network detail");
    }

    private sealed class ProtocolFailureHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("hostile protocol detail");
    }

    private sealed class RecordingCustody(
        AppSurfaceWebPushTerminalDisposition disposition = AppSurfaceWebPushTerminalDisposition.Completed)
        : IAppSurfaceWebPushSubscriptionCustody
    {
        public AppSurfaceWebPushSubscription? TerminalSubscription { get; private set; }

        public AppSurfaceWebPushTerminalReason? TerminalReason { get; private set; }

        public ValueTask<AppSurfaceWebPushRegistrationDisposition> RegisterAsync(
            AppSurfaceWebPushSubscriptionWriteContext context,
            AppSurfaceWebPushSubscription subscription,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(AppSurfaceWebPushRegistrationDisposition.Created);

        public ValueTask<AppSurfaceWebPushUnregistrationDisposition> UnregisterAsync(
            AppSurfaceWebPushSubscriptionWriteContext context,
            AppSurfaceWebPushSubscriptionReference subscription,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(AppSurfaceWebPushUnregistrationDisposition.Removed);

        public ValueTask<AppSurfaceWebPushTerminalDisposition> MarkTerminalAsync(
            AppSurfaceWebPushSubscription subscription,
            AppSurfaceWebPushTerminalReason reason,
            CancellationToken cancellationToken)
        {
            TerminalSubscription = subscription;
            TerminalReason = reason;
            return ValueTask.FromResult(disposition);
        }
    }

    private sealed class FailingTerminalCustody(bool cancel) : IAppSurfaceWebPushSubscriptionCustody
    {
        public ValueTask<AppSurfaceWebPushRegistrationDisposition> RegisterAsync(
            AppSurfaceWebPushSubscriptionWriteContext context,
            AppSurfaceWebPushSubscription subscription,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(AppSurfaceWebPushRegistrationDisposition.Created);

        public ValueTask<AppSurfaceWebPushUnregistrationDisposition> UnregisterAsync(
            AppSurfaceWebPushSubscriptionWriteContext context,
            AppSurfaceWebPushSubscriptionReference subscription,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(AppSurfaceWebPushUnregistrationDisposition.Removed);

        public ValueTask<AppSurfaceWebPushTerminalDisposition> MarkTerminalAsync(
            AppSurfaceWebPushSubscription subscription,
            AppSurfaceWebPushTerminalReason reason,
            CancellationToken cancellationToken) =>
            cancel
                ? ValueTask.FromCanceled<AppSurfaceWebPushTerminalDisposition>(new CancellationToken(canceled: true))
                : ValueTask.FromException<AppSurfaceWebPushTerminalDisposition>(new InvalidOperationException("hostile custody detail"));
    }

    private sealed class CancellationCallbackCustody : IAppSurfaceWebPushSubscriptionCustody
    {
        private readonly ManualResetEventSlim callbackRelease = new(initialState: false);
        private CancellationTokenRegistration registration;

        public bool CallbackInvoked { get; private set; }

        public ValueTask<AppSurfaceWebPushRegistrationDisposition> RegisterAsync(
            AppSurfaceWebPushSubscriptionWriteContext context,
            AppSurfaceWebPushSubscription subscription,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(AppSurfaceWebPushRegistrationDisposition.Created);

        public ValueTask<AppSurfaceWebPushUnregistrationDisposition> UnregisterAsync(
            AppSurfaceWebPushSubscriptionWriteContext context,
            AppSurfaceWebPushSubscriptionReference subscription,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(AppSurfaceWebPushUnregistrationDisposition.Removed);

        public ValueTask<AppSurfaceWebPushTerminalDisposition> MarkTerminalAsync(
            AppSurfaceWebPushSubscription subscription,
            AppSurfaceWebPushTerminalReason reason,
            CancellationToken cancellationToken)
        {
            registration = cancellationToken.Register(() =>
            {
                CallbackInvoked = true;
                callbackRelease.Wait();
            });
            return ValueTask.FromResult(AppSurfaceWebPushTerminalDisposition.Completed);
        }

        public void ReleaseCallback()
        {
            callbackRelease.Set();
            registration.Dispose();
            callbackRelease.Dispose();
        }
    }

    private sealed class SlowDisposableCustody : IAppSurfaceWebPushSubscriptionCustody, IAsyncDisposable
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<AppSurfaceWebPushRegistrationDisposition> RegisterAsync(
            AppSurfaceWebPushSubscriptionWriteContext context,
            AppSurfaceWebPushSubscription subscription,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(AppSurfaceWebPushRegistrationDisposition.Created);

        public ValueTask<AppSurfaceWebPushUnregistrationDisposition> UnregisterAsync(
            AppSurfaceWebPushSubscriptionWriteContext context,
            AppSurfaceWebPushSubscriptionReference subscription,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(AppSurfaceWebPushUnregistrationDisposition.Removed);

        public async ValueTask<AppSurfaceWebPushTerminalDisposition> MarkTerminalAsync(
            AppSurfaceWebPushSubscription subscription,
            AppSurfaceWebPushTerminalReason reason,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.ConfigureAwait(false);
            return AppSurfaceWebPushTerminalDisposition.Completed;
        }

        public ValueTask DisposeAsync()
        {
            Disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingCancellationCustody : IAppSurfaceWebPushSubscriptionCustody
    {
        private readonly ManualResetEventSlim callbackRelease = new(initialState: false);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CallbackStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CallbackCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<AppSurfaceWebPushRegistrationDisposition> RegisterAsync(
            AppSurfaceWebPushSubscriptionWriteContext context,
            AppSurfaceWebPushSubscription subscription,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(AppSurfaceWebPushRegistrationDisposition.Created);

        public ValueTask<AppSurfaceWebPushUnregistrationDisposition> UnregisterAsync(
            AppSurfaceWebPushSubscriptionWriteContext context,
            AppSurfaceWebPushSubscriptionReference subscription,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(AppSurfaceWebPushUnregistrationDisposition.Removed);

        public async ValueTask<AppSurfaceWebPushTerminalDisposition> MarkTerminalAsync(
            AppSurfaceWebPushSubscription subscription,
            AppSurfaceWebPushTerminalReason reason,
            CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    CallbackStarted.TrySetResult();
                    callbackRelease.Wait();
                }
                finally
                {
                    CallbackCompleted.TrySetResult();
                }
            });
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return AppSurfaceWebPushTerminalDisposition.Completed;
        }

        public async Task ReleaseCallbackAsync()
        {
            callbackRelease.Set();
            await CallbackCompleted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            callbackRelease.Dispose();
        }
    }
}
