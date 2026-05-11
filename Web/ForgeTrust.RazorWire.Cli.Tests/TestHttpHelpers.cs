using System.Net;
using System.Text;

namespace ForgeTrust.RazorWire.Cli.Tests;

internal static class TestHttpHelpers
{
    internal sealed class Factory : IHttpClientFactory
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public Factory(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public HttpClient CreateClient(string name)
        {
            return new HttpClient(new StaticHandler(_responseFactory));
        }
    }

    internal sealed class StaticHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public StaticHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_responseFactory(request));
        }
    }

    internal static Func<HttpRequestMessage, HttpResponseMessage> FixedStatus(HttpStatusCode statusCode)
    {
        return _ =>
            new HttpResponseMessage(statusCode)
            {
                Content = new StringContent("x", Encoding.UTF8, "text/plain")
            };
    }

    internal static Func<HttpRequestMessage, HttpResponseMessage> UrlAwareHtmlRoot(string baseUrl)
    {
        var normalized = baseUrl.TrimEnd('/');
        return request =>
        {
            if (request.RequestUri?.ToString() == $"{normalized}/")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html><body><h1>ok</h1></body></html>", Encoding.UTF8, "text/html")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
    }
}
