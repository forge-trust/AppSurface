using System.IO.Compression;
using System.Net;
using Google.Api.Gax;
using Google.Api.Gax.Grpc;
using Google.Cloud.Tasks.V2;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using Grpc.Net.Client.Configuration;
using Grpc.Net.Compression;
using ProviderTask = Google.Cloud.Tasks.V2.Task;

namespace ForgeTrust.AppSurface.Durable.CloudTasks;

/// <summary>Owns the reusable SDK channel and HTTP handler; no ambient credentials or retry policy.</summary>
internal sealed class CloudTasksSdkTransport : ICloudTasksRpcTransport, IDisposable
{
    /// <summary>Maximum encoded and decoded protobuf bytes accepted by this transport.</summary>
    internal const int MessageLimit = 64 * 1024;
    private readonly GrpcChannel _channel;
    private readonly CloudTasksClient _client;
    private bool _disposed;

    /// <summary>Lifecycle observation seam for container ownership tests.</summary>
    internal bool IsDisposed => _disposed;

    /// <summary>Constructs the fixed production transport without opening a connection.</summary>
    internal CloudTasksSdkTransport() : this(new Uri("https://cloudtasks.googleapis.com"), CreateHandler()) { }

    /// <summary>Creates a real SDK channel against a local HTTP/2 fixture, owning its handler.</summary>
    /// <param name="endpoint">The fixture HTTPS endpoint.</param>
    /// <param name="handler">The owned HTTP handler with the production transport bounds.</param>
    /// <param name="observeSdkCall">Optional internal fixture callback observing SDK RPC calls before HTTP transport.</param>
    internal CloudTasksSdkTransport(Uri endpoint, HttpMessageHandler handler, Action? observeSdkCall = null)
    {
        _channel = GrpcChannel.ForAddress(endpoint, new Grpc.Net.Client.GrpcChannelOptions
        {
            HttpHandler = new ResponseGuardHandler(handler),
            DisposeHttpClient = true,
            MaxReceiveMessageSize = MessageLimit,
            MaxRetryAttempts = 1,
            ServiceConfig = new ServiceConfig(),
            DisableResolverServiceConfig = true,
            HttpVersion = HttpVersion.Version20,
            HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            CompressionProviders = [new BoundedGzipProvider()]
        });
        _client = new CloudTasksClientBuilder
        {
            CallInvoker = observeSdkCall is null ? _channel.CreateCallInvoker() : _channel.CreateCallInvoker().Intercept(new RpcInvocationObserver(observeSdkCall)),
            Settings = new CloudTasksSettings
            {
                CreateTaskSettings = CallSettings.FromRetry(RetrySettings.FromConstantBackoff(1, TimeSpan.Zero, static _ => false))
            }
        }.Build();
    }

    /// <summary>Observes generated RPC calls for internal fixtures before transport-level replay.</summary>
    private sealed class RpcInvocationObserver(Action observeCall) : Interceptor
    {
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            TRequest request, ClientInterceptorContext<TRequest, TResponse> context,
            AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
        {
            observeCall();
            return continuation(request, context);
        }
    }

    /// <summary>Constructs only the supported fixed-policy sockets handler.</summary>
    internal static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        UseProxy = false,
        MaxResponseHeadersLength = 16, // KiB; .NET HTTP/2 maintains one decoded header+trailer budget per stream.
        MaxResponseDrainSize = 0,
        ConnectTimeout = TimeSpan.FromSeconds(2)
    };

    /// <inheritdoc />
    public async Task<string?> CreateAsync(CloudTasksActivationRequest request, string token, TimeSpan remaining, CancellationToken cancellationToken)
    {
        var target = request.Target;
        var providerRequest = new CreateTaskRequest
        {
            Parent = target.QueueName,
            ResponseView = ProviderTask.Types.View.Basic,
            Task = new ProviderTask
            {
                Name = request.TaskName,
                HttpRequest = new Google.Cloud.Tasks.V2.HttpRequest
                {
                    HttpMethod = Google.Cloud.Tasks.V2.HttpMethod.Post,
                    Url = target.WorkerEndpoint.AbsoluteUri,
                    OidcToken = new OidcToken { ServiceAccountEmail = target.OidcServiceAccountEmail, Audience = target.Audience }
                },
                DispatchDeadline = Duration.FromTimeSpan(target.DispatchDeadline)
            }
        };
        try
        {
            var task = await _client.CreateTaskAsync(providerRequest,
                CallSettings.FromHeader("authorization", "Bearer " + token)
                    .WithCancellationToken(cancellationToken)
                    .WithExpiration(Expiration.FromTimeout(remaining))).ConfigureAwait(false);
            return task?.Name;
        }
        catch (RpcException exception)
        {
            var localFailure = FindLocalFailure(exception.Status.DebugException);
            if (localFailure is not null && CloudTasksDiagnostics.IsFatal(localFailure))
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(localFailure).Throw();
            if (localFailure is not null) throw new CloudTasksResponseLimitException();
            throw;
        }
    }

    private static Exception? FindLocalFailure(Exception? exception)
    {
        for (var depth = 0; exception is not null && depth < 16; depth++, exception = exception is RpcException nested ? nested.Status.DebugException : exception.InnerException)
        {
            if (CloudTasksDiagnostics.IsFatal(exception) || exception is CloudTasksResponseLimitException
                || exception is HttpRequestException { HttpRequestError: HttpRequestError.ConfigurationLimitExceeded }) return exception;
        }
        return null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _channel.Dispose();
    }

    /// <summary>Rejects unsupported response encodings before SDK-local statuses can resemble provider rejections.</summary>
    private sealed class ResponseGuardHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        /// <inheritdoc />
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            var validMediaType = string.Equals(mediaType, "application/grpc", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mediaType, "application/grpc+proto", StringComparison.OrdinalIgnoreCase);
            // A trailers-only status is handled before message decoding by the pinned SDK.
            var hasInitialStatus = response.Headers.Contains("grpc-status");
            var validEncoding = hasInitialStatus || !response.Headers.TryGetValues("grpc-encoding", out var values)
                || values.Count() == 1 && values.All(static value => value is "identity" or "gzip");
            if (response.StatusCode != HttpStatusCode.OK || !validMediaType || !validEncoding)
            {
                response.Dispose(); // Never consume a fallback HTML/error body or decompress an unapproved encoding.
                throw new InvalidDataException("Cloud Tasks response protocol was invalid.");
            }
            return response;
        }
    }

    /// <summary>Uses the supported channel compression seam to enforce the decoded cap before protobuf parsing.</summary>
    internal sealed class BoundedGzipProvider : ICompressionProvider
    {
        private readonly GzipCompressionProvider _gzip = new(CompressionLevel.Fastest);
        /// <inheritdoc />
        public string EncodingName => _gzip.EncodingName;
        /// <inheritdoc />
        public Stream CreateCompressionStream(Stream stream, CompressionLevel? compressionLevel) => _gzip.CreateCompressionStream(stream, compressionLevel);
        /// <inheritdoc />
        public Stream CreateDecompressionStream(Stream stream) => new LimitedReadStream(_gzip.CreateDecompressionStream(stream));
    }

    /// <summary>Reads at most limit-plus-one decoded bytes and never hands the excess byte to a protobuf decoder.</summary>
    internal sealed class LimitedReadStream(Stream inner) : Stream
    {
        private int _read;
        /// <inheritdoc />
        public override bool CanRead => inner.CanRead;
        /// <inheritdoc />
        public override bool CanSeek => false;
        /// <inheritdoc />
        public override bool CanWrite => false;
        /// <inheritdoc />
        public override long Length => throw new NotSupportedException();
        /// <inheritdoc />
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        /// <inheritdoc />
        public override int Read(Span<byte> buffer) => Account(inner.Read(buffer[..Math.Min(buffer.Length, MessageLimit - _read + 1)]));
        /// <inheritdoc />
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => Account(await inner.ReadAsync(buffer[..Math.Min(buffer.Length, MessageLimit - _read + 1)], cancellationToken).ConfigureAwait(false));
        /// <inheritdoc />
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        private int Account(int count)
        {
            _read += count;
            if (_read > MessageLimit) throw new CloudTasksResponseLimitException();
            return count;
        }
        /// <inheritdoc />
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        /// <inheritdoc />
        public override void Flush() => throw new NotSupportedException();
        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();
        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
