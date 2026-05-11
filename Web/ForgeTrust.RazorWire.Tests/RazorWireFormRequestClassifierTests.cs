using System.Text;
using ForgeTrust.RazorWire.Forms;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeTrust.RazorWire.Tests;

public class RazorWireFormRequestClassifierTests
{
    [Fact]
    public async Task IsRazorWireFormRequestAsync_WithHeader_ReturnsTrue()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[RazorWireFormHeaders.FormRequest] = "true";
        var classifier = CreateClassifier();

        var result = await classifier.IsRazorWireFormRequestAsync(context.Request);

        Assert.True(result);
    }

    [Fact]
    public async Task IsRazorWireFormRequestAsync_WithFalsyHeader_ReturnsFalse()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[RazorWireFormHeaders.FormRequest] = "false";
        var classifier = CreateClassifier();

        var result = await classifier.IsRazorWireFormRequestAsync(context.Request);

        Assert.False(result);
    }

    [Fact]
    public async Task IsRazorWireFormRequestAsync_WithUrlEncodedHiddenMarker_ReturnsTrue()
    {
        var context = new DefaultHttpContext();
        var body = "__RazorWireForm=1&name=value";
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.ContentLength = Encoding.UTF8.GetByteCount(body);
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        var classifier = CreateClassifier();

        var result = await classifier.IsRazorWireFormRequestAsync(context.Request);

        Assert.True(result);
    }

    [Fact]
    public async Task IsRazorWireFormRequestAsync_WithUnknownContentLength_ReturnsFalse()
    {
        var context = new DefaultHttpContext();
        var body = "__RazorWireForm=1&name=value";
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        var classifier = CreateClassifier();

        var result = await classifier.IsRazorWireFormRequestAsync(context.Request);

        Assert.False(result);
    }

    [Fact]
    public async Task IsRazorWireFormRequestAsync_WhenFormFeatureReportsUnsupportedContentType_ReturnsFalse()
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "text/plain";
        context.Request.ContentLength = 1;
        context.Features.Set<IFormFeature>(new TestFormFeature());
        var classifier = CreateClassifier();

        var result = await classifier.IsRazorWireFormRequestAsync(context.Request);

        Assert.False(result);
    }

    [Theory]
    [MemberData(nameof(FormReadExceptions))]
    public async Task IsRazorWireFormRequestAsync_WhenFallbackFormReadFails_ReturnsFalse(Exception exception)
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.ContentLength = 1;
        context.Features.Set<IFormFeature>(new TestFormFeature(exception));
        var classifier = CreateClassifier();

        var result = await classifier.IsRazorWireFormRequestAsync(context.Request);

        Assert.False(result);
    }

    [Fact]
    public async Task IsRazorWireFormRequestAsync_WithAlreadyParsedForm_ReturnsTrue()
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Features.Set<IFormFeature>(new FormFeature(new FormCollection(
            new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
            {
                ["__RazorWireForm"] = "true"
            })));
        var classifier = CreateClassifier();

        var result = await classifier.IsRazorWireFormRequestAsync(context.Request);

        Assert.True(result);
    }

    [Fact]
    public async Task IsRazorWireFormRequestAsync_WithMultipartWithoutHeader_ReturnsFalse()
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "multipart/form-data; boundary=test";
        var classifier = CreateClassifier();

        var result = await classifier.IsRazorWireFormRequestAsync(context.Request);

        Assert.False(result);
    }

    [Fact]
    public async Task IsRazorWireFormRequestAsync_WithNonRazorWireTurboForm_ReturnsFalse()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Accept = "text/vnd.turbo-stream.html";
        var classifier = CreateClassifier();

        var result = await classifier.IsRazorWireFormRequestAsync(context.Request);

        Assert.False(result);
    }

    private static RazorWireFormRequestClassifier CreateClassifier()
    {
        return new RazorWireFormRequestClassifier(NullLogger<RazorWireFormRequestClassifier>.Instance);
    }

    public static TheoryData<Exception> FormReadExceptions()
    {
        return new TheoryData<Exception>
        {
            new InvalidDataException("Invalid form"),
            new IOException("Read failed"),
            new InvalidOperationException("Form already read"),
            new BadHttpRequestException("Bad form")
        };
    }

    private sealed class TestFormFeature : IFormFeature
    {
        private readonly Exception? _exception;

        public TestFormFeature(Exception? exception = null)
        {
            _exception = exception;
        }

        public bool HasFormContentType => true;

        public IFormCollection? Form { get; set; }

        public IFormCollection ReadForm()
        {
            if (_exception is not null)
            {
                throw _exception;
            }

            return new FormCollection([]);
        }

        public Task<IFormCollection> ReadFormAsync(CancellationToken cancellationToken)
        {
            if (_exception is not null)
            {
                return Task.FromException<IFormCollection>(_exception);
            }

            return Task.FromResult<IFormCollection>(new FormCollection([]));
        }
    }
}
