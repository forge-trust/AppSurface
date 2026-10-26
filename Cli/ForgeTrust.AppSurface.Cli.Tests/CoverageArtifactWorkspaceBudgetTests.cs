using ForgeTrust.AppSurface.Evidence.Aspire;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Verifies bounded retries around workspace inventory without weakening ownership or budget checks.</summary>
public sealed class CoverageArtifactWorkspaceBudgetTests
{
    [Theory]
    [InlineData(typeof(IOException), unchecked((int)0x80070005))]
    [InlineData(typeof(IOException), unchecked((int)0x80070020))]
    [InlineData(typeof(IOException), unchecked((int)0x8007012F))]
    [InlineData(typeof(UnauthorizedAccessException), unchecked((int)0x80070005))]
    [InlineData(typeof(UnauthorizedAccessException), unchecked((int)0x80070020))]
    [InlineData(typeof(UnauthorizedAccessException), unchecked((int)0x8007012F))]
    public void MeasureWithRetry_RetriesOnlyApprovedTransientWindowsErrors(Type exceptionType, int hresult)
    {
        var attempts = 0;
        var validations = 0;

        var measured = EvidenceWorkspace.MeasureWithRetry(
            () => ++attempts < 3 ? throw CreateException(exceptionType, hresult) : 73,
            () => validations++, retryTransientWindowsErrors: true);

        Assert.Equal(73, measured);
        Assert.Equal(3, attempts);
        Assert.Equal(4, validations); // Before each attempt, and once after the successful full measurement.
    }

    [Theory]
    [InlineData(typeof(IOException), unchecked((int)0x80004005))]
    [InlineData(typeof(IOException), unchecked((int)0x80070006))]
    [InlineData(typeof(UnauthorizedAccessException), unchecked((int)0x80004005))]
    [InlineData(typeof(UnauthorizedAccessException), unchecked((int)0x80070006))]
    [InlineData(typeof(InvalidOperationException), unchecked((int)0x80070020))]
    public void MeasureWithRetry_DoesNotRetryOtherExceptionTypesOrHResults(Type exceptionType, int hresult)
    {
        var failure = CreateException(exceptionType, hresult);
        var attempts = 0;

        var thrown = Assert.ThrowsAny<Exception>(() => EvidenceWorkspace.MeasureWithRetry(
            () => { attempts++; throw failure; }, static () => { }, retryTransientWindowsErrors: true));

        Assert.Same(failure, thrown);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void MeasureWithRetry_DisabledRetryRethrowsTheSameExceptionImmediately()
    {
        var failure = CreateException(typeof(IOException), unchecked((int)0x80070020));
        var attempts = 0;

        var thrown = Assert.ThrowsAny<IOException>(() => EvidenceWorkspace.MeasureWithRetry(
            () => { attempts++; throw failure; }, static () => { }, retryTransientWindowsErrors: false));

        Assert.Same(failure, thrown);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void MeasureWithRetry_DoesNotRetryWorkspaceBudgetExceptions()
    {
        var failure = new EvidenceWorkspaceBudgetException("limit exceeded", 11, 10, 100, 0);
        var attempts = 0;

        var thrown = Assert.Throws<EvidenceWorkspaceBudgetException>(() => EvidenceWorkspace.MeasureWithRetry(
            () => { attempts++; throw failure; }, static () => { }, retryTransientWindowsErrors: true));

        Assert.Same(failure, thrown);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void MeasureWithRetry_StopsAfterThreeFailuresAndPreservesTheLastExceptionInstance()
    {
        var attempts = 0;
        var failures = new IOException[3];

        var thrown = Assert.ThrowsAny<IOException>(() => EvidenceWorkspace.MeasureWithRetry(
            () =>
            {
                var failure = (IOException)CreateException(typeof(IOException), unchecked((int)0x80070020));
                failures[attempts++] = failure;
                throw failure;
            }, static () => { }, retryTransientWindowsErrors: true));

        Assert.Equal(3, attempts);
        Assert.Same(failures[2], thrown);
    }

    [Fact]
    public void MeasureWithRetry_ValidatesBeforeEachAttemptAndFailsClosedIfOwnershipChanges()
    {
        var validationFailure = new IOException("owned root changed");
        var attempts = 0;
        var validations = 0;

        var thrown = Assert.Throws<IOException>(() => EvidenceWorkspace.MeasureWithRetry(
            () =>
            {
                attempts++;
                throw CreateException(typeof(IOException), unchecked((int)0x80070020));
            },
            () =>
            {
                if (++validations == 2) throw validationFailure;
            }, retryTransientWindowsErrors: true));

        Assert.Same(validationFailure, thrown);
        Assert.Equal(1, attempts);
        Assert.Equal(2, validations);
    }

    [Fact]
    public void MeasureWithRetry_ValidatesAfterSuccessAndDoesNotRetryValidationFailure()
    {
        var validationFailure = new IOException("owned root changed after inventory");
        var attempts = 0;
        var validations = 0;

        var thrown = Assert.Throws<IOException>(() => EvidenceWorkspace.MeasureWithRetry(
            () => { attempts++; return 91; },
            () =>
            {
                if (++validations == 2) throw validationFailure;
            }, retryTransientWindowsErrors: true));

        Assert.Same(validationFailure, thrown);
        Assert.Equal(1, attempts);
        Assert.Equal(2, validations);
    }

    [Fact]
    public void MeasureWithRetry_ReturnsTheFreshCompleteMeasurementAfterTransientFailure()
    {
        var attempts = 0;
        var validations = 0;

        var measured = EvidenceWorkspace.MeasureWithRetry(
            () =>
            {
                if (++attempts == 1) throw CreateException(typeof(IOException), unchecked((int)0x8007012F));
                return 123456;
            },
            () => validations++, retryTransientWindowsErrors: true);

        Assert.Equal(123456, measured);
        Assert.Equal(2, attempts);
        Assert.Equal(3, validations);
    }

    private static Exception CreateException(Type exceptionType, int hresult)
    {
        if (exceptionType == typeof(IOException)) return new HResultIOException(hresult);
        if (exceptionType == typeof(UnauthorizedAccessException)) return new HResultUnauthorizedAccessException(hresult);
        if (exceptionType == typeof(InvalidOperationException)) return new HResultInvalidOperationException(hresult);
        throw new ArgumentOutOfRangeException(nameof(exceptionType));
    }

    private sealed class HResultIOException : IOException
    {
        public HResultIOException(int hresult) : base("fixture transient inventory error") => HResult = hresult;
    }

    private sealed class HResultUnauthorizedAccessException : UnauthorizedAccessException
    {
        public HResultUnauthorizedAccessException(int hresult) : base("fixture transient inventory error") => HResult = hresult;
    }

    private sealed class HResultInvalidOperationException : InvalidOperationException
    {
        public HResultInvalidOperationException(int hresult) : base("fixture unexpected measurement error") => HResult = hresult;
    }
}
