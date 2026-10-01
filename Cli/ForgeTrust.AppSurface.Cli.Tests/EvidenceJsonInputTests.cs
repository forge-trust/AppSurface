using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceJsonInputTests
{
    [Fact]
    public async Task DeserializeAsync_AcceptsExactLowerByteLimitFromChunkedNonSeekableStream()
    {
        await using var stream = new ChunkedNonSeekableStream("null"u8.ToArray(), maximumChunkBytes: 2);

        var value = await EvidenceCanonicalJson.DeserializeAsync<JsonElement>(stream, maximumBytes: 4);

        Assert.Equal(JsonValueKind.Null, value.ValueKind);
        Assert.False(stream.CanSeek);
        Assert.Equal(3, stream.ReadCount);
    }

    [Fact]
    public async Task DeserializeAsync_AcceptsExactProtectedDefaultLimit()
    {
        var json = new byte[EvidenceCanonicalJson.MaximumInputBytes];
        Array.Fill(json, (byte)' ');
        json[^1] = (byte)'0';
        await using var stream = new ChunkedNonSeekableStream(json, maximumChunkBytes: 64 * 1024);

        var value = await EvidenceCanonicalJson.DeserializeAsync<JsonElement>(stream);

        Assert.Equal(0, value.GetInt32());
        Assert.Equal(EvidenceCanonicalJson.MaximumInputBytes, stream.BytesServed);
    }

    [Fact]
    public async Task DeserializeAsync_RejectsFirstByteOverLowerLimitWithoutReadingRemainder()
    {
        await using var stream = new ChunkedNonSeekableStream("null extra secret"u8.ToArray(), maximumChunkBytes: 3);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await EvidenceCanonicalJson.DeserializeAsync<JsonElement>(stream, maximumBytes: 4));

        Assert.DoesNotContain("secret", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, stream.ReadCount);
    }

    [Fact]
    public async Task DeserializeAsync_RejectsInputBeyondProtectedDefaultLimit()
    {
        await using var stream = new ChunkedNonSeekableStream(
            new byte[EvidenceCanonicalJson.MaximumInputBytes + 1],
            maximumChunkBytes: 64 * 1024);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await EvidenceCanonicalJson.DeserializeAsync<JsonElement>(stream));

        Assert.DoesNotContain("secret", exception.Message, StringComparison.Ordinal);
        Assert.Equal(EvidenceCanonicalJson.MaximumInputBytes + 1L, stream.BytesServed);
    }

    [Fact]
    public async Task DeserializeAsync_PropagatesCancellationWhileReading()
    {
        using var cancellation = new CancellationTokenSource();
        await using var stream = new CancellationWaitStream();
        var read = EvidenceCanonicalJson.DeserializeAsync<JsonElement>(stream, cancellationToken: cancellation.Token).AsTask();
        await stream.ReadStarted;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("unauthorized")]
    [InlineData("unsupported")]
    [InlineData("disposed")]
    public async Task DeserializeAsync_SanitizesStreamReadFailures(string failure)
    {
        Exception readFailure = failure switch
        {
            "io" => new IOException("read-secret"),
            "unauthorized" => new UnauthorizedAccessException("read-secret"),
            "unsupported" => new NotSupportedException("read-secret"),
            "disposed" => new ObjectDisposedException("read-secret"),
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };
        await using var stream = new FailedReadStream(readFailure);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await EvidenceCanonicalJson.DeserializeAsync<JsonElement>(stream));

        Assert.DoesNotContain("read-secret", exception.Message, StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void Deserialize_EnforcesSpanLimitBeforeParsingAndAllowsExactLimit()
    {
        var exact = "true"u8.ToArray();

        Assert.True(EvidenceCanonicalJson.Deserialize<JsonElement>(exact, maximumBytes: exact.Length).GetBoolean());
        var exception = Assert.Throws<InvalidDataException>(() =>
            EvidenceCanonicalJson.Deserialize<JsonElement>(exact, maximumBytes: exact.Length - 1));

        Assert.DoesNotContain("true", exception.Message, StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => EvidenceCanonicalJson.Deserialize<JsonElement>([], maximumBytes: 0));
    }

    [Fact]
    public async Task DeserializeAsync_RejectsInvalidArgumentsBeforeReading()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await EvidenceCanonicalJson.DeserializeAsync<JsonElement>(null!));
        await using var stream = new ChunkedNonSeekableStream("true"u8.ToArray(), maximumChunkBytes: 1);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await EvidenceCanonicalJson.DeserializeAsync<JsonElement>(stream, -1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await EvidenceCanonicalJson.DeserializeAsync<JsonElement>(stream, EvidenceCanonicalJson.MaximumInputBytes + 1));

        Assert.Equal(0, stream.ReadCount);
    }

    [Fact]
    public async Task DeserializeAsync_DoesNotDisposeCallerStreamOnSuccessOrFailure()
    {
        await using var accepted = new MemoryStream("true"u8.ToArray());
        await using var rejected = new MemoryStream("true extra"u8.ToArray());

        Assert.True((await EvidenceCanonicalJson.DeserializeAsync<JsonElement>(accepted)).GetBoolean());
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await EvidenceCanonicalJson.DeserializeAsync<JsonElement>(rejected, 4));

        accepted.Position = 0;
        rejected.Position = 0;
        Assert.Equal('t', accepted.ReadByte());
        Assert.Equal('t', rejected.ReadByte());
    }

    [Fact]
    public void Serialize_PreservesUndefinedEnumBytesWhileDeserializeRejectsThem()
    {
        var profile = new EvidenceProfile("invalid", (EvidenceProfileScope)int.MaxValue, [], [], []);

        var json = EvidenceCanonicalJson.Serialize(profile);

        Assert.Equal(
            "{\"Id\":\"invalid\",\"Obligations\":[],\"Producers\":[],\"Resources\":[],\"Scope\":2147483647}",
            Encoding.UTF8.GetString(json));
        Assert.Throws<JsonException>(() => EvidenceCanonicalJson.Deserialize<EvidenceProfile>(json));
    }

    [Fact]
    public void Deserialize_HandlesEmptyObjectsAndIndependentSiblingPropertyScopes()
    {
        var json = "[{},{\"id\":1},{\"ID\":2},{\"nested\":{\"id\":3}},{}]"u8.ToArray();

        var value = EvidenceCanonicalJson.Deserialize<JsonElement>(json);

        Assert.Equal(5, value.GetArrayLength());
        Assert.Equal(1, value[1].GetProperty("id").GetInt32());
        Assert.Equal(2, value[2].GetProperty("ID").GetInt32());
        Assert.Equal(3, value[3].GetProperty("nested").GetProperty("id").GetInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deserialize_AcceptsExactly64NestedLevels(bool useObjects)
    {
        const int Depth = 64;
        var opening = useObjects ? "{\"child\":" : "[";
        var closing = useObjects ? "}" : "]";
        var json = Encoding.UTF8.GetBytes(
            string.Concat(Enumerable.Repeat(opening, Depth)) + "\"nesting-canary\"" +
            string.Concat(Enumerable.Repeat(closing, Depth)));
        var spanValue = EvidenceCanonicalJson.Deserialize<JsonElement>(json);
        await using var stream = new ChunkedNonSeekableStream(json, maximumChunkBytes: 1);
        var streamValue = await EvidenceCanonicalJson.DeserializeAsync<JsonElement>(stream);

        foreach (var value in new[] { spanValue, streamValue })
        {
            var leaf = value;
            for (var level = 0; level < Depth; level++)
            {
                leaf = useObjects ? leaf.GetProperty("child") : leaf[0];
            }

            Assert.Equal("nesting-canary", leaf.GetString());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deserialize_Rejects65NestedLevelsWithSafeJsonException(bool useObjects)
    {
        const int Depth = 65;
        var opening = useObjects ? "{\"child\":" : "[";
        var closing = useObjects ? "}" : "]";
        var json = Encoding.UTF8.GetBytes(
            string.Concat(Enumerable.Repeat(opening, Depth)) + "\"nesting-secret-canary\"" +
            string.Concat(Enumerable.Repeat(closing, Depth)));

        var spanFailure = Assert.Throws<JsonException>(() => EvidenceCanonicalJson.Deserialize<JsonElement>(json));
        await using var stream = new ChunkedNonSeekableStream(json, maximumChunkBytes: 1);
        var streamFailure = await Assert.ThrowsAsync<JsonException>(async () =>
            await EvidenceCanonicalJson.DeserializeAsync<JsonElement>(stream));

        foreach (var failure in new[] { spanFailure, streamFailure })
        {
            Assert.Equal("Evidence JSON is malformed or does not match the supported contract.", failure.Message);
            Assert.Null(failure.InnerException);
            Assert.DoesNotContain("nesting-secret-canary", failure.ToString(), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"Id\":\"json-secret\"")]
    public void Deserialize_UsesSafeJsonExceptionForNullOrMalformedInput(string json)
    {
        var exception = Assert.Throws<JsonException>(() => EvidenceCanonicalJson.Deserialize<EvidencePolicy>(Encoding.UTF8.GetBytes(json)));

        Assert.DoesNotContain("json-secret", exception.Message, StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    [Theory]
    [InlineData("FF")]
    [InlineData("ED A0 80")]
    [InlineData("5C 75 44 38 30 30")]
    [InlineData("5C 75 44 43 30 30")]
    public async Task Deserialize_RejectsInvalidPropertyEncodingWithSafeJsonException(string encodedProperty)
    {
        var property = Convert.FromHexString(encodedProperty.Replace(" ", "", StringComparison.Ordinal));
        byte[] json = [.. "{\""u8.ToArray(), .. property, .. "\":\"unicode-secret-canary\"}"u8.ToArray()];

        var spanFailure = Assert.Throws<JsonException>(() => EvidenceCanonicalJson.Deserialize<JsonElement>(json));
        await using var stream = new ChunkedNonSeekableStream(json, maximumChunkBytes: 1);
        var streamFailure = await Assert.ThrowsAsync<JsonException>(async () =>
            await EvidenceCanonicalJson.DeserializeAsync<JsonElement>(stream));

        foreach (var failure in new[] { spanFailure, streamFailure })
        {
            Assert.DoesNotContain("unicode-secret-canary", failure.ToString(), StringComparison.Ordinal);
            Assert.Null(failure.InnerException);
        }
    }

    [Fact]
    public void Deserialize_AcceptsValidUnicodePropertyNamesAndSurrogatePairs()
    {
        var value = EvidenceCanonicalJson.Deserialize<JsonElement>(
            "{\"é\":1,\"\\uD83D\\uDE00\":2}"u8);

        Assert.Equal(1, value.GetProperty("é").GetInt32());
        Assert.Equal(2, value.GetProperty("😀").GetInt32());
    }

    [Fact]
    public void Deserialize_RejectsNegativeAndRaisedLimits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EvidenceCanonicalJson.Deserialize<JsonElement>(new byte[] { 110, 117, 108, 108 }, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => EvidenceCanonicalJson.Deserialize<JsonElement>(
            new byte[] { 110, 117, 108, 108 },
            EvidenceCanonicalJson.MaximumInputBytes + 1));
    }

    [Theory]
    [InlineData("{\"Id\":\"first\",\"Id\":\"second\"}")]
    [InlineData("{\"Id\":\"first\",\"id\":\"second\"}")]
    [InlineData("{\"Id\":\"first\",\"Profiles\":[{\"Id\":\"profile\",\"id\":\"collision\"}]}")]
    public void Deserialize_RejectsDuplicateAndCaseCollidingPropertiesRecursively(string json)
    {
        var exception = Assert.Throws<JsonException>(() => EvidenceCanonicalJson.Deserialize<EvidencePolicy>(Encoding.UTF8.GetBytes(json)));

        Assert.DoesNotContain("first", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("second", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"not-a-scope-secret\"")]
    [InlineData("0")]
    [InlineData("\"0\"")]
    public void Deserialize_RejectsUnknownAndNumericEnumValues(string enumJson)
    {
        var policy = CreatePolicy();
        var json = Encoding.UTF8.GetString(EvidenceCanonicalJson.Serialize(policy))
            .Replace("\"Scope\":\"Targeted\"", $"\"Scope\":{enumJson}", StringComparison.Ordinal);

        var exception = Assert.Throws<JsonException>(() => EvidenceCanonicalJson.Deserialize<EvidencePolicy>(Encoding.UTF8.GetBytes(json)));

        Assert.DoesNotContain("not-a-scope-secret", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Deserialize_ContinuesToAcceptKnownEnumNamesWithoutCaseSensitivity()
    {
        var json = Encoding.UTF8.GetString(EvidenceCanonicalJson.Serialize(CreatePolicy()))
            .Replace("\"Scope\":\"Targeted\"", "\"Scope\":\"targeted\"", StringComparison.Ordinal);

        var policy = EvidenceCanonicalJson.Deserialize<EvidencePolicy>(Encoding.UTF8.GetBytes(json));

        Assert.Equal(EvidenceProfileScope.Targeted, policy.Profiles[0].Scope);
    }

    [Fact]
    public void Serialize_PreservesCanonicalV1EnumAndPropertyBytes()
    {
        var profile = new EvidenceProfile("coverage", EvidenceProfileScope.Targeted, [], [], []);

        Assert.Equal(
            "{\"Id\":\"coverage\",\"Obligations\":[],\"Producers\":[],\"Resources\":[],\"Scope\":\"Targeted\"}",
            Encoding.UTF8.GetString(EvidenceCanonicalJson.Serialize(profile)));
    }

    [Theory]
    [InlineData("\"9.9\"")]
    [InlineData("null")]
    [InlineData("3")]
    [InlineData("missing")]
    public void Deserialize_RejectsUnsupportedOrMissingPlanVersion(string versionJson)
    {
        var plan = CreatePlan();
        var json = Encoding.UTF8.GetString(EvidenceCanonicalJson.Serialize(plan));
        json = versionJson == "missing"
            ? json.Replace("\"ContractVersion\":\"1.0\",", string.Empty, StringComparison.Ordinal)
            : json.Replace("\"ContractVersion\":\"1.0\"", $"\"ContractVersion\":{versionJson}", StringComparison.Ordinal);

        var exception = Assert.Throws<JsonException>(() => EvidenceCanonicalJson.Deserialize<EvidencePlan>(Encoding.UTF8.GetBytes(json)));

        Assert.DoesNotContain("9.9", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"9.9\"")]
    [InlineData("null")]
    [InlineData("3")]
    [InlineData("missing")]
    public void Deserialize_RejectsUnsupportedOrMissingManifestVersion(string versionJson)
    {
        var manifest = CreateManifest();
        var json = Encoding.UTF8.GetString(EvidenceCanonicalJson.Serialize(manifest));
        json = versionJson == "missing"
            ? json.Replace("\"ContractVersion\":\"1.0\",", string.Empty, StringComparison.Ordinal)
            : json.Replace("\"ContractVersion\":\"1.0\"", $"\"ContractVersion\":{versionJson}", StringComparison.Ordinal);

        var exception = Assert.Throws<JsonException>(() => EvidenceCanonicalJson.Deserialize<EvidenceManifest>(Encoding.UTF8.GetBytes(json)));

        Assert.DoesNotContain("9.9", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Deserialize_PreservesSafeAdditiveFieldsAndCanonicalBytes()
    {
        var expected = CreatePolicy();
        var canonicalBytes = EvidenceCanonicalJson.Serialize(expected);
        var jsonWithAdditiveField = Encoding.UTF8.GetString(canonicalBytes)
            .Replace("{", "{\"futureOptional\":{\"enabled\":true},", StringComparison.Ordinal);

        var actual = EvidenceCanonicalJson.Deserialize<EvidencePolicy>(Encoding.UTF8.GetBytes(jsonWithAdditiveField));

        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(expected.Profiles[0].Id, actual.Profiles[0].Id);
        Assert.Equal(expected.Profiles[0].Scope, actual.Profiles[0].Scope);
        Assert.Equal(canonicalBytes, EvidenceCanonicalJson.Serialize(actual));
    }

    [Fact]
    public void Deserialize_RetainsSupportedV10PlanAndManifestCanonicalSerialization()
    {
        var plan = CreatePlan();
        var manifest = CreateManifest();
        var planBytes = EvidenceCanonicalJson.Serialize(plan);
        var manifestBytes = EvidenceCanonicalJson.Serialize(manifest);
        var parsedPlan = EvidenceCanonicalJson.Deserialize<EvidencePlan>(planBytes);
        var parsedManifest = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(manifestBytes);

        Assert.Equal("1.0", parsedPlan.ContractVersion);
        Assert.Equal("1.0", parsedManifest.ContractVersion);
        Assert.Equal(planBytes, EvidenceCanonicalJson.Serialize(parsedPlan));
        Assert.Equal(manifestBytes, EvidenceCanonicalJson.Serialize(parsedManifest));
        Assert.True(EvidenceManifestBuilder.Verify(parsedPlan, parsedManifest));
    }

    private static EvidencePolicy CreatePolicy()
    {
        var profile = new EvidenceProfile("coverage", EvidenceProfileScope.Targeted, [], [], []);
        return new EvidencePolicy("test-policy", "1", "coverage", [profile], []);
    }

    private static EvidencePlan CreatePlan()
    {
        var policy = CreatePolicy();
        var draft = new EvidencePlan(
            "1.0",
            policy.Id,
            EvidenceDigest.CanonicalSha256(policy),
            EvidenceDigest.CanonicalSha256(Array.Empty<NormalizedDiffPath>()),
            policy.Profiles[0],
            [],
            [],
            string.Empty,
            policy);
        return draft with { PlanDigest = EvidenceDigest.CanonicalSha256(draft) };
    }

    private static EvidenceManifest CreateManifest() => EvidenceManifestBuilder.Build(CreatePlan(), []);

    private sealed class ChunkedNonSeekableStream(byte[] bytes, int maximumChunkBytes) : Stream
    {
        private int _offset;

        public int ReadCount { get; private set; }

        public long BytesServed => _offset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            var bytesToCopy = Math.Min(Math.Min(buffer.Length, maximumChunkBytes), bytes.Length - _offset);
            bytes.AsMemory(_offset, bytesToCopy).CopyTo(buffer);
            _offset += bytesToCopy;
            return ValueTask.FromResult(bytesToCopy);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CancellationWaitStream : Stream
    {
        private readonly TaskCompletionSource _readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ReadStarted => _readStarted.Task;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(static state =>
                ((TaskCompletionSource<int>)state!).TrySetCanceled(), completion);
            _readStarted.TrySetResult();
            return await completion.Task.ConfigureAwait(false);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FailedReadStream(Exception exception) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(exception);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
