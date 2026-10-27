using System.Text;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactPolicyTests
{
    private static readonly string[] PolicyFields =
    [
        "historyMaxBytes",
        "historyMaxAgeDays",
        "repositoryWarningBytes",
        "domainWarningBytes",
        "warningCooldownHours",
    ];

    [Fact]
    public void Read_AppliesDefaultsToEveryOmittedField()
    {
        var policy = Read("{\"schemaVersion\":1}");

        Assert.Equal(2_147_483_648, policy.HistoryMaxBytes);
        Assert.Equal(7, policy.HistoryMaxAgeDays);
        Assert.Equal(1_073_741_824, policy.RepositoryWarningBytes);
        Assert.Equal(2_147_483_648, policy.DomainWarningBytes);
        Assert.Equal(24, policy.WarningCooldownHours);
    }

    [Fact]
    public void Read_UsesDefaultsForFieldsOmittedFromAPartialPolicy()
    {
        var policy = Read("{\"schemaVersion\":1,\"historyMaxBytes\":0,\"domainWarningBytes\":1234}");

        Assert.Equal(0, policy.HistoryMaxBytes);
        Assert.Equal(7, policy.HistoryMaxAgeDays);
        Assert.Equal(1_073_741_824, policy.RepositoryWarningBytes);
        Assert.Equal(1234, policy.DomainWarningBytes);
        Assert.Equal(24, policy.WarningCooldownHours);
    }

    [Fact]
    public void Read_AcceptsValidValuesForAllPolicyFields()
    {
        var policy = Read(
            "{\"schemaVersion\":1,\"historyMaxBytes\":4096,\"historyMaxAgeDays\":30,"
            + "\"repositoryWarningBytes\":8192,\"domainWarningBytes\":16384,\"warningCooldownHours\":12}");

        Assert.Equal(4096, policy.HistoryMaxBytes);
        Assert.Equal(30, policy.HistoryMaxAgeDays);
        Assert.Equal(8192, policy.RepositoryWarningBytes);
        Assert.Equal(16384, policy.DomainWarningBytes);
        Assert.Equal(12, policy.WarningCooldownHours);
    }

    [Theory]
    [InlineData("historyMaxBytes", "0", 0L)]
    [InlineData("historyMaxBytes", "1", 1L)]
    [InlineData("historyMaxBytes", "1099511627776", 1_099_511_627_776L)]
    [InlineData("historyMaxAgeDays", "1", 1L)]
    [InlineData("historyMaxAgeDays", "365", 365L)]
    [InlineData("repositoryWarningBytes", "1", 1L)]
    [InlineData("repositoryWarningBytes", "1099511627776", 1_099_511_627_776L)]
    [InlineData("domainWarningBytes", "1", 1L)]
    [InlineData("domainWarningBytes", "1099511627776", 1_099_511_627_776L)]
    [InlineData("warningCooldownHours", "1", 1L)]
    [InlineData("warningCooldownHours", "168", 168L)]
    public void Read_AcceptsExactFieldBoundaries(string field, string value, long expected)
    {
        var policy = Read($"{{\"schemaVersion\":1,\"{field}\":{value}}}");

        AssertFieldValue(policy, field, expected);
    }

    [Theory]
    [InlineData("historyMaxBytes", "-1")]
    [InlineData("historyMaxBytes", "1099511627777")]
    [InlineData("historyMaxAgeDays", "-1")]
    [InlineData("historyMaxAgeDays", "0")]
    [InlineData("historyMaxAgeDays", "366")]
    [InlineData("repositoryWarningBytes", "-1")]
    [InlineData("repositoryWarningBytes", "0")]
    [InlineData("repositoryWarningBytes", "1099511627777")]
    [InlineData("domainWarningBytes", "-1")]
    [InlineData("domainWarningBytes", "0")]
    [InlineData("domainWarningBytes", "1099511627777")]
    [InlineData("warningCooldownHours", "-1")]
    [InlineData("warningCooldownHours", "0")]
    [InlineData("warningCooldownHours", "169")]
    public void Read_RejectsValuesOutsideFieldBoundaries(string field, string value)
    {
        AssertInvalid($"{{\"schemaVersion\":1,\"{field}\":{value}}}");
    }

    [Theory]
    [InlineData("historyMaxBytes")]
    [InlineData("historyMaxAgeDays")]
    [InlineData("repositoryWarningBytes")]
    [InlineData("domainWarningBytes")]
    [InlineData("warningCooldownHours")]
    public void Read_RejectsSignedIntegerOverflowForEveryPolicyField(string field)
    {
        AssertInvalid($"{{\"schemaVersion\":1,\"{field}\":9223372036854775808}}");
    }

    [Fact]
    public void Limits_ExposeTheFixedResourceCapsAndBudgets()
    {
        Assert.Equal(65_536, CoverageArtifactLimits.MaximumPolicyBytes);
        Assert.Equal(256L * 1024 * 1024, CoverageArtifactLimits.AutomaticSnapshotMaxOriginalBytes);
        Assert.Equal(256L * 1024 * 1024, CoverageArtifactLimits.AutomaticSnapshotMaxStagingBytes);
        Assert.Equal(512L * 1024 * 1024, CoverageArtifactLimits.DomainStagingMaxBytes);
        Assert.Equal(64L * 1024 * 1024 * 1024, CoverageArtifactLimits.ExplicitExpandedMaxBytes);
        Assert.Equal(10_000, CoverageArtifactLimits.MaximumFiles);
        Assert.Equal(10_000, CoverageArtifactLimits.MaximumRegistrations);
        Assert.Equal(2L * 1024 * 1024, CoverageArtifactLimits.RunInventoryMaxBytes);
        Assert.Equal(16L * 1024 * 1024, CoverageArtifactLimits.RegistryMaxBytes);
        Assert.Equal(64L * 1024, CoverageArtifactLimits.OverflowObservationReserveBytes);
        Assert.Equal(32, CoverageArtifactLimits.MaximumOverflowObservations);
        Assert.Equal(1024 * 1024, CoverageArtifactLimits.OperationBufferMaxBytes);
        Assert.Equal(1_000, CoverageArtifactLimits.InspectionBudgetMilliseconds);
        Assert.Equal(10_000, CoverageArtifactLimits.InspectionMaxEntries);
        Assert.Equal(5_000, CoverageArtifactLimits.RoutineMaintenanceBudgetMilliseconds);
        Assert.Equal(1_000, CoverageArtifactLimits.WarningStateMaxConditions);
        Assert.Equal(1L * 1024 * 1024, CoverageArtifactLimits.WarningStateMaxBytes);
        Assert.Equal(10_000, CoverageArtifactLimits.ExpiryReceiptsMaxEntries);
        Assert.Equal(4L * 1024 * 1024, CoverageArtifactLimits.ExpiryReceiptsMaxBytes);
        Assert.Equal(7, CoverageArtifactLimits.ExpiryReceiptMaxAgeDays);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":0}")]
    [InlineData("{\"schemaVersion\":2}")]
    [InlineData("{\"schemaVersion\":1.0}")]
    [InlineData("{\"schemaVersion\":1e0}")]
    [InlineData("{\"schemaVersion\":\"1\"}")]
    [InlineData("{\"schemaVersion\":true}")]
    [InlineData("{\"schemaVersion\":null}")]
    public void Read_RequiresSchemaVersionExactlyOne(string json) => AssertInvalid(json);

    [Fact]
    public void Read_RejectsWrongJsonTypesForEveryPolicyField()
    {
        string[] wrongTypes = ["null", "true", "\"1\"", "[]", "{}"];

        foreach (var field in PolicyFields)
        {
            foreach (var value in wrongTypes)
            {
                AssertInvalid($"{{\"schemaVersion\":1,\"{field}\":{value}}}");
            }
        }
    }

    [Theory]
    [InlineData("historyMaxBytes")]
    [InlineData("historyMaxAgeDays")]
    [InlineData("repositoryWarningBytes")]
    [InlineData("domainWarningBytes")]
    [InlineData("warningCooldownHours")]
    public void Read_RejectsFractionalAndNonIntegerNumberForms(string field)
    {
        foreach (var value in new[] { "0.5", "1.0", "1e0", "1E+0" })
        {
            AssertInvalid($"{{\"schemaVersion\":1,\"{field}\":{value}}}");
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("1")]
    [InlineData("[]")]
    [InlineData("\"policy\"")]
    public void Read_RejectsNonObjectRoots(string json) => AssertInvalid(json);

    [Fact]
    public void Read_RejectsDuplicateAndUnknownFields()
    {
        AssertInvalid("{\"schemaVersion\":1,\"schemaVersion\":1}");
        AssertInvalid("{\"schemaVersion\":1,\"historyMaxBytes\":1,\"historyMaxBytes\":1}");
        AssertInvalid("{\"schemaVersion\":1,\"futureSetting\":true}");
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{\"schemaVersion\":1,")]
    [InlineData("{\"schemaVersion\":1,}")]
    [InlineData("{\"schemaVersion\":1} false")]
    [InlineData("{\"schemaVersion\":1} {\"schemaVersion\":1}")]
    public void Read_RejectsMalformedOrTrailingJson(string json) => AssertInvalid(json);

    [Fact]
    public void Read_RejectsExcessiveJsonNesting()
    {
        var nested = new string('[', 10_000) + "0" + new string(']', 10_000);

        AssertInvalid($"{{\"schemaVersion\":1,\"historyMaxBytes\":{nested}}}");
    }

    [Fact]
    public void Read_AcceptsExactlyMaximumPolicyBytesWithTrailingJsonWhitespace()
    {
        var prefix = "{\"schemaVersion\":1}";
        var json = prefix + new string(' ', CoverageArtifactLimits.MaximumPolicyBytes - Encoding.UTF8.GetByteCount(prefix));
        var bytes = Encoding.UTF8.GetBytes(json);
        using var stream = new CountingReadStream(bytes);

        var policy = CoverageArtifactPolicy.Read(stream);

        Assert.Equal(CoverageArtifactLimits.MaximumPolicyBytes, bytes.Length);
        Assert.Equal(7, policy.HistoryMaxAgeDays);
        Assert.Equal(CoverageArtifactLimits.MaximumPolicyBytes, stream.BytesRead);
    }

    [Fact]
    public void Read_RejectsOneByteOverMaximumPolicyBytesAfterReadingOnlyTheDetectionByte()
    {
        var prefix = "{\"schemaVersion\":1}";
        var json = prefix + new string(' ', CoverageArtifactLimits.MaximumPolicyBytes + 1 - Encoding.UTF8.GetByteCount(prefix));
        using var stream = new CountingReadStream(Encoding.UTF8.GetBytes(json));

        Assert.Throws<InvalidDataException>(() => CoverageArtifactPolicy.Read(stream));

        Assert.Equal(CoverageArtifactLimits.MaximumPolicyBytes + 1, stream.BytesRead);
        Assert.InRange(stream.ReadCalls, 1, (CoverageArtifactLimits.MaximumPolicyBytes + 1 + CountingReadStream.MaximumChunkBytes - 1) / CountingReadStream.MaximumChunkBytes);
    }

    private static CoverageArtifactPolicy Read(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return CoverageArtifactPolicy.Read(stream);
    }

    private static void AssertInvalid(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        Assert.Throws<InvalidDataException>(() => CoverageArtifactPolicy.Read(stream));
    }

    private static void AssertFieldValue(CoverageArtifactPolicy policy, string field, long expected)
    {
        var actual = field switch
        {
            "historyMaxBytes" => policy.HistoryMaxBytes,
            "historyMaxAgeDays" => policy.HistoryMaxAgeDays,
            "repositoryWarningBytes" => policy.RepositoryWarningBytes,
            "domainWarningBytes" => policy.DomainWarningBytes,
            "warningCooldownHours" => policy.WarningCooldownHours,
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        Assert.Equal(expected, actual);
    }

    private sealed class CountingReadStream(byte[] contents) : Stream
    {
        public const int MaximumChunkBytes = 4096;

        private int _position;

        public int BytesRead { get; private set; }

        public int ReadCalls { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            ReadCalls++;
            var count = Math.Min(Math.Min(buffer.Length, MaximumChunkBytes), contents.Length - _position);
            contents.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            BytesRead += count;
            return count;
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
