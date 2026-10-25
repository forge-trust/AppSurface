using ForgeTrust.AppSurface.Evidence.Supervision;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Checks only the detached NSS buffer retry procedure; no lookup, owner, or account is constructed.</summary>
public sealed class LinuxNssBufferProcedureTests
{
    [Fact]
    public void RetriesOnlyRangeErrorWithOriginalDoublingSizesAndReturnsSuccess()
    {
        var sizes = new List<int>();
        var result = LinuxNssBufferProcedure.Execute<byte[]>(buffer =>
        {
            sizes.Add(buffer.Length);
            return sizes.Count < 3 ? (34, null) : (0, buffer);
        }, Failure);

        Assert.Equal([1024, 2048, 4096], sizes);
        Assert.NotNull(result);
        Assert.Equal(4096, result.Length);
    }

    [Fact]
    public void ExhaustedRangeRetriesUseEveryOriginalSizeAndFixedFailure()
    {
        var sizes = new List<int>();
        var error = Assert.Throws<InvalidOperationException>(() => LinuxNssBufferProcedure.Execute<string>(buffer =>
        {
            sizes.Add(buffer.Length);
            return (34, null);
        }, Failure));

        Assert.Equal([1024, 2048, 4096, 8192, 16384, 32768, 65536], sizes);
        Assert.Equal("fixed NSS failure", error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void NonRangeErrorFailsImmediatelyWithoutRetry()
    {
        var calls = 0;
        var error = Assert.Throws<InvalidOperationException>(() => LinuxNssBufferProcedure.Execute<string>(buffer =>
        {
            calls++;
            Assert.Equal(1024, buffer.Length);
            return (2, null);
        }, Failure));

        Assert.Equal(1, calls);
        Assert.Equal("fixed NSS failure", error.Message);
    }

    [Fact]
    public void NonRangeErrorDiscardsCallbackValueAndFailsWithoutRetry()
    {
        var calls = 0;
        var error = Assert.Throws<InvalidOperationException>(() => LinuxNssBufferProcedure.Execute<string>(buffer =>
        {
            calls++;
            Assert.Equal(1024, buffer.Length);
            return (2, "must be discarded");
        }, Failure));

        Assert.Equal(1, calls);
        Assert.Equal("fixed NSS failure", error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void SuccessfulNullResultReturnsAbsenceWithoutRetry()
    {
        var calls = 0;
        var result = LinuxNssBufferProcedure.Execute<string>(buffer =>
        {
            calls++;
            Assert.Equal(1024, buffer.Length);
            return (0, null);
        }, Failure);

        Assert.Null(result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void LookupExceptionPropagatesUnchangedWithoutRetryOrFailureFactory()
    {
        var original = new IOException("lookup failure");
        var calls = 0;
        var failureFactoryCalls = 0;

        var error = Assert.Throws<IOException>(() => LinuxNssBufferProcedure.Execute<string>(_ =>
        {
            calls++;
            throw original;
        }, () =>
        {
            failureFactoryCalls++;
            return new InvalidOperationException("must not replace lookup exception");
        }));

        Assert.Same(original, error);
        Assert.Equal(1, calls);
        Assert.Equal(0, failureFactoryCalls);
    }

    [Fact]
    public void SuccessfulResultAtMaximumBufferUsesTheInclusiveLastSize()
    {
        var sizes = new List<int>();
        byte[]? finalBuffer = null;

        var result = LinuxNssBufferProcedure.Execute<byte[]>(buffer =>
        {
            sizes.Add(buffer.Length);
            if (buffer.Length < 65_536) return (34, null);
            finalBuffer = buffer;
            return (0, buffer);
        }, Failure);

        Assert.Equal([1024, 2048, 4096, 8192, 16384, 32768, 65536], sizes);
        Assert.Same(finalBuffer, result);
    }

    private static Exception Failure() => new InvalidOperationException("fixed NSS failure");
}
