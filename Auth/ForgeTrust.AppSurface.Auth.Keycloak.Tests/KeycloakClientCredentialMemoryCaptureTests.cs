using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ForgeTrust.AppSurface.Auth.Keycloak.Tests;

// protects: bounded opaque memory custody, exact bindings/incarnations, native failures and resource closure.
// fails_when: mutable/replaced/expired captures pass, interruptions spin, or partial resources remain open.
// no_claim: fake native I/O verifies the protocol; separate Linux container evidence qualifies actual syscalls.
public sealed class KeycloakClientCredentialMemoryCaptureTests
{
    private const int StatxBufferSize = 256;
    private const string RunId = "0123456789abcdef0123456789abcdef";
    private static readonly Uri Authority = new("https://identity.example/realms/sample");
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CapturesOpaqueUtf8AndReadsThroughPartialIoAndInterruptedCalls()
    {
        const string value = "opaque-π-🔐";
        var native = new FakeNative
        {
            MaximumWrite = 7,
            MaximumRead = 5,
            InterruptedWrites = 2,
            InterruptedReads = 2,
            InterruptedTrailingReads = 2
        };
        var clock = new MutableClock(Now);

        using var capture = Create(native, clock, value);

        Assert.Equal(value, KeycloakClientCredentialMemoryCapture.Read(capture.Reference, native, clock));
        Assert.Equal(string.Empty, capture.Reference.RunDirectory);
        Assert.Equal(RunId, capture.Reference.RunId);
        Assert.Equal(Authority, capture.Reference.Authority);
        Assert.Equal("web-client", capture.Reference.ClientId);
        Assert.Equal(Environment.ProcessId, capture.Reference.MemoryIdentity!.ProcessId);
        Assert.Equal(native.ProcessStartCounter, capture.Reference.MemoryIdentity.ProcessStartCounter);
        Assert.Equal(15, native.Fcntl(capture.Reference.MemoryIdentity.FileDescriptor, 1034, 0));
        Assert.Equal(1, native.ActiveHandleCount); // the capture retains only its holder descriptor
    }

    [Fact]
    public void OwnerLifetimeSurvivesFiveMinutesWhileExpiringProfileDoesNot()
    {
        var ownerNative = new FakeNative();
        var ownerClock = new MutableClock(Now);
        using var ownerCapture = CreateForOwnerLifetime(ownerNative, ownerClock, "opaque-secret");
        var expiringNative = new FakeNative();
        var expiringClock = new MutableClock(Now);
        using var expiringCapture = Create(expiringNative, expiringClock, "opaque-secret");

        ownerClock.Now = Now.AddYears(5);
        expiringClock.Now = Now.AddMinutes(5).AddTicks(1);

        Assert.Equal("opaque-secret", KeycloakClientCredentialMemoryCapture.Read(ownerCapture.Reference, ownerNative, ownerClock));
        Assert.Throws<InvalidDataException>(() => KeycloakClientCredentialMemoryCapture.Read(
            expiringCapture.Reference, expiringNative, expiringClock));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("owner-expiry")]
    [InlineData("expiring-no-expiry")]
    [InlineData("duplicate-lifetime")]
    [InlineData("non-object")]
    public void RejectsMalformedOrAmbiguousLifetimeHeaders(string mutation)
    {
        Func<string, string> transform = mutation switch
        {
            "unknown" => header => header.Replace("\"lifetime\":\"owner\"", "\"lifetime\":\"mystery!\"", StringComparison.Ordinal),
            "owner-expiry" => header => header.Replace(
                "\"lifetime\":\"owner\"}", "\"lifetime\":\"owner\",\"expiresUtcTicks\":1}", StringComparison.Ordinal),
            "expiring-no-expiry" => header => RemoveJsonProperty(header, "expiresUtcTicks"),
            "duplicate-lifetime" => header => header.Replace(
                "\"lifetime\":\"owner\"", "\"lifetime\":\"owner\",\"lifetime\":\"owner\"", StringComparison.Ordinal),
            "non-object" => _ => "[]",
            _ => throw new InvalidOperationException()
        };
        var native = new FakeNative();
        var clock = new MutableClock(Now);
        // Leave enough payload space for duplicate/extra header fields while keeping total frame size constant.
        var value = new string('x', 256);
        using var capture = mutation == "expiring-no-expiry"
            ? Create(native, clock, value)
            : CreateForOwnerLifetime(native, clock, value);
        native.TransformOwnerFrameForTest(transform);

        Assert.Throws<InvalidDataException>(() => KeycloakClientCredentialMemoryCapture.Read(capture.Reference, native, clock));
    }

    [Theory]
    [InlineData("expiry-string")]
    [InlineData("expiry-overflow")]
    [InlineData("expiry-fractional")]
    [InlineData("expiry-null")]
    [InlineData("duplicate-client")]
    [InlineData("unknown-property")]
    [InlineData("client-type")]
    [InlineData("missing-run")]
    [InlineData("missing-authority")]
    [InlineData("missing-client")]
    [InlineData("missing-lifetime")]
    public void RejectsMalformedHeaderFieldsWithMatchingCredentialBinding(string mutation)
    {
        Func<string, string> transform = mutation switch
        {
            "expiry-string" => header => SetHeaderProperty(header, "expiresUtcTicks", "\"not-a-number\""),
            "expiry-overflow" => header => SetHeaderProperty(header, "expiresUtcTicks", "9223372036854775808"),
            "expiry-fractional" => header => SetHeaderProperty(header, "expiresUtcTicks", "1.5"),
            "expiry-null" => header => SetHeaderProperty(header, "expiresUtcTicks", "null"),
            "duplicate-client" => header => header.Replace(
                "\"client\":\"web-client\"", "\"client\":\"web-client\",\"client\":\"web-client\"", StringComparison.Ordinal),
            "unknown-property" => header => SetHeaderProperty(header, "unexpected", "true"),
            "client-type" => header => SetHeaderProperty(header, "client", "false"),
            "missing-run" => header => RemoveJsonProperty(header, "run"),
            "missing-authority" => header => RemoveJsonProperty(header, "authority"),
            "missing-client" => header => RemoveJsonProperty(header, "client"),
            "missing-lifetime" => header => RemoveJsonProperty(header, "lifetime"),
            _ => throw new InvalidOperationException()
        };
        var native = new FakeNative();
        var clock = new MutableClock(Now);
        using var capture = mutation.StartsWith("expiry-", StringComparison.Ordinal)
            ? Create(native, clock, new string('x', 256))
            : CreateForOwnerLifetime(native, clock, new string('x', 256));
        var holderDescriptor = capture.Reference.MemoryIdentity!.FileDescriptor;
        native.TransformOwnerFrameForTest(transform);

        Assert.Throws<InvalidDataException>(() => KeycloakClientCredentialMemoryCapture.Read(
            capture.Reference, native, clock));

        Assert.Equal(new[] { holderDescriptor }, native.ActiveDescriptors);
        Assert.Equal(new[] { holderDescriptor + 1, holderDescriptor + 2, holderDescriptor + 3 },
            native.ClosedDescriptors.Order());
    }

    private static string SetHeaderProperty(string header, string propertyName, string rawJsonValue)
    {
        var json = JsonNode.Parse(header)!.AsObject();
        json[propertyName] = JsonNode.Parse(rawJsonValue);
        return json.ToJsonString();
    }


    [Fact]
    public void DisposalClosesHolderDescriptor()
    {
        var native = new FakeNative();
        var capture = Create(native, new MutableClock(Now), "opaque-secret");
        Assert.Equal(1, native.ActiveHandleCount);

        capture.Dispose();
        capture.Dispose();

        Assert.Equal(0, native.ActiveHandleCount);
        Assert.Throws<IOException>(() => KeycloakClientCredentialMemoryCapture.Read(capture.Reference, native, new MutableClock(Now)));
    }

    [Fact]
    public void RejectsHolderClosedWhileReaderHandleRemainsOpen()
    {
        var native = new FakeNative();
        using var capture = CreateForOwnerLifetime(native, new MutableClock(Now), "opaque-secret");
        native.CloseOwnerAfterPayloadRead = true;

        Assert.Throws<InvalidDataException>(() => KeycloakClientCredentialMemoryCapture.Read(
            capture.Reference, native, new MutableClock(Now)));
        Assert.Equal(0, native.ActiveHandleCount);
    }

    [Fact]
    public void RejectsChangedHolderIncarnationDuringRead()
    {
        var native = new FakeNative { ChangeProcessStartAfterPayloadRead = true };
        using var capture = CreateForOwnerLifetime(native, new MutableClock(Now), "opaque-secret");

        Assert.Throws<InvalidDataException>(() => KeycloakClientCredentialMemoryCapture.Read(
            capture.Reference, native, new MutableClock(Now)));
        Assert.Equal(1, native.ActiveHandleCount);
    }

    [Fact]
    public void RelativeAuthorityIsRejectedBeforeCreatingAResource()
    {
        var native = new FakeNative();
        Assert.Throws<ArgumentException>(() => KeycloakClientCredentialMemoryCapture.Create(
            RunId, new Uri("relative", UriKind.Relative), "web-client", "opaque", native, new MutableClock(Now)));
        Assert.Equal(0, native.CreateCount);
    }

    [Fact]
    public void RejectsMalformedRunIdBeforeCreatingANativeResource()
    {
        var native = new FakeNative();

        Assert.Throws<ArgumentException>(() => KeycloakClientCredentialMemoryCapture.Create(
            "not-a-guid", Authority, "web-client", "opaque", native, new MutableClock(Now)));

        Assert.Equal(0, native.CreateCount);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void RejectsNonPositiveHolderStartCounterBeforeCreatingMemfd(long startCounter)
    {
        var native = new FakeNative { ProcessStartCounter = startCounter };

        Assert.Throws<InvalidDataException>(() => Create(native, new MutableClock(Now), "opaque-secret"));

        Assert.Equal(0, native.CreateCount);
        Assert.Empty(native.ActiveDescriptors);
    }

    [Theory]
    [InlineData("mask")]
    [InlineData("type")]
    [InlineData("group-read")]
    [InlineData("setuid")]
    [InlineData("uid")]
    [InlineData("links")]
    [InlineData("inode")]
    [InlineData("short")]
    [InlineData("oversize")]
    public void InvalidOpenedMetadataIsRejectedAndReadHandleIsClosed(string field)
    {
        var native = new FakeNative();
        using var capture = Create(native, new MutableClock(Now), "opaque-secret");
        native.StatusTransform = status => field switch
        {
            "mask" => status with { Mask = 0 },
            "type" => status with { Mode = 0x4100 },
            "group-read" => status with { Mode = 0x8120 },
            "setuid" => status with { Mode = 0x8900 },
            "uid" => status with { UserId = 999 },
            "links" => status with { LinkCount = 1 },
            "inode" => status with { Inode = 0 },
            "short" => status with { Size = 11 },
            "oversize" => status with { Size = 8205 },
            _ => throw new InvalidOperationException()
        };

        Assert.Throws<InvalidDataException>(() => KeycloakClientCredentialMemoryCapture.Read(capture.Reference, native, new MutableClock(Now)));
        Assert.Equal(1, native.ActiveHandleCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(0x100)]
    [InlineData(0x200)]
    public void MissingOpenedMetadataFieldIsRejectedAndOnlyTemporaryDescriptorsAreClosed(uint missingField)
    {
        var native = new FakeNative();
        using var capture = Create(native, new MutableClock(Now), "opaque-secret");
        var owner = capture.Reference.MemoryIdentity!.FileDescriptor;
        native.StatusTransform = status => status with { Mask = status.Mask & ~missingField };

        Assert.Throws<InvalidDataException>(() => KeycloakClientCredentialMemoryCapture.Read(
            capture.Reference, native, new MutableClock(Now)));

        Assert.Equal(1, native.ActiveHandleCount);
        Assert.Equal(new[] { owner + 1 }, native.ClosedDescriptors);
        Assert.Contains(owner, native.ActiveDescriptors);
    }

    [Theory]
    [InlineData("invalid-utf8")]
    [InlineData("control-byte")]
    public void RejectsFakeNativeCredentialBytesAndClosesEveryReadDescriptor(string corruption)
    {
        var native = new FakeNative
        {
            CorruptSecretUtf8AfterSeal = corruption == "invalid-utf8",
            CorruptSecretControlAfterSeal = corruption == "control-byte"
        };
        using var capture = Create(native, new MutableClock(Now), "opaque-secret");
        var owner = capture.Reference.MemoryIdentity!.FileDescriptor;

        Assert.Throws<InvalidDataException>(() => KeycloakClientCredentialMemoryCapture.Read(
            capture.Reference, native, new MutableClock(Now)));

        Assert.Equal(new[] { owner + 1, owner + 2, owner + 3 }, native.ClosedDescriptors.Order());
        Assert.Equal(new[] { owner }, native.ActiveDescriptors);
    }

    [Fact]
    public void ExpiringFrameIsRejectedAtItsExactExpiryTick()
    {
        var native = new FakeNative();
        var clock = new MutableClock(Now);
        using var capture = Create(native, clock, "opaque-secret");
        clock.Now = Now.AddMinutes(5);

        Assert.Throws<InvalidDataException>(() => KeycloakClientCredentialMemoryCapture.Read(capture.Reference, native, clock));
        Assert.Equal(1, native.ActiveHandleCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t")]
    [InlineData("credential\nvalue")]
    public void RejectsInvalidCredentialsBeforeNativeCreation(string value)
    {
        var native = new FakeNative();

        Assert.Throws<InvalidDataException>(() => Create(native, new MutableClock(Now), value));
        Assert.Equal(0, native.CreateCount);
    }

    [Fact]
    public void RejectsRuntimeConstructedUnpairedSurrogateBeforeNativeCreation()
    {
        var value = new string((char)0xD800, 1);
        var native = new FakeNative();

        Assert.Throws<InvalidDataException>(() => Create(native, new MutableClock(Now), value));
        Assert.Equal(0, native.CreateCount);
    }

    [Fact]
    public void AcceptsMaximumCredentialSizeAndRejectsOversize()
    {
        var maximum = new string('é', 2048);
        Assert.Equal(4096, Encoding.UTF8.GetByteCount(maximum));
        var native = new FakeNative();
        using (var capture = Create(native, new MutableClock(Now), maximum))
            Assert.Equal(maximum, KeycloakClientCredentialMemoryCapture.Read(capture.Reference, native, new MutableClock(Now)));

        Assert.Throws<InvalidDataException>(() => Create(new FakeNative(), new MutableClock(Now), new string('x', 4097)));
    }

    [Fact]
    public void RejectsNegativeGetSealsResultDuringCaptureAndClosesPartialResource()
    {
        var native = new FakeNative { GetSealsOverride = -1 };

        Assert.Throws<IOException>(() => Create(native, new MutableClock(Now), "opaque-secret"));

        Assert.Equal(0, native.ActiveHandleCount);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("chmod")]
    [InlineData("add-seals")]
    [InlineData("missing-seals")]
    [InlineData("metadata")]
    [InlineData("size")]
    public void CaptureFinalizationFailuresCloseTheCreatedDescriptor(string failure)
    {
        var native = new FakeNative();
        switch (failure)
        {
            case "create": native.FailCreate = true; break;
            case "chmod": native.FchmodResult = -1; break;
            case "add-seals": native.AddSealsResult = -1; break;
            case "missing-seals": native.GetSealsOverride = 7; break;
            case "metadata": native.StatusTransform = status => status with { UserId = status.UserId + 1 }; break;
            case "size": native.ReportedSize = 12; break;
            default: throw new InvalidOperationException();
        }

        var error = Record.Exception(() => Create(native, new MutableClock(Now), "opaque-secret"));
        if (failure is "metadata" or "size")
            Assert.IsType<InvalidDataException>(error);
        else
            Assert.IsType<IOException>(error);
        Assert.Equal(0, native.ActiveHandleCount);
    }

    [Fact]
    public void RejectsHolderProcessChangeDuringCaptureFinalizationAndClosesDescriptor()
    {
        var native = new FakeNative();
        native.StatusTransform = status =>
        {
            native.ProcessStartCounter++;
            return status;
        };

        var error = Assert.Throws<InvalidDataException>(() => Create(native, new MutableClock(Now), "opaque-secret"));

        Assert.Equal("Sealed credential capture identity was rejected.", error.Message);
        Assert.Empty(native.ActiveDescriptors);
        Assert.Single(native.ClosedDescriptors);
    }

    [Fact]
    public void ReaderAndHolderOpenFailuresAreRejectedWithoutLeakingHandles()
    {
        var readerOpenNative = new FakeNative();
        using (var capture = Create(readerOpenNative, new MutableClock(Now), "opaque-secret"))
        {
            readerOpenNative.FailOpenCall = 1;
            Assert.Throws<IOException>(() => KeycloakClientCredentialMemoryCapture.Read(
                capture.Reference, readerOpenNative, new MutableClock(Now)));
            Assert.Equal(1, readerOpenNative.ActiveHandleCount);
        }

        var holderOpenNative = new FakeNative();
        using (var capture = Create(holderOpenNative, new MutableClock(Now), "opaque-secret"))
        {
            holderOpenNative.FailOpenCall = 2;
            Assert.Throws<InvalidDataException>(() => KeycloakClientCredentialMemoryCapture.Read(
                capture.Reference, holderOpenNative, new MutableClock(Now)));
            Assert.Equal(1, holderOpenNative.ActiveHandleCount);
        }
    }

    [Fact]
    public void MemoryReaderRefusesPrivateFileReferenceBeforeNativeAccess()
    {
        var native = new FakeNative();
        var reference = new KeycloakClientCredentialReference(
            Path.GetTempPath(), RunId, Authority, "web-client");

        Assert.Throws<InvalidDataException>(() => KeycloakClientCredentialMemoryCapture.Read(
            reference, native, new MutableClock(Now)));

        Assert.Equal(0, native.ProcessStartCounterReadCount);
        Assert.Equal(0, native.FileStatusReadCount);
    }

    [Fact]
    public void RejectsTrailingFrameBytesAndClosesReaderHandle()
    {
        var native = new FakeNative();
        using var capture = Create(native, new MutableClock(Now), "opaque-secret");
        native.ReportedSize = (ulong)(native.GetOwnerFileLength() - 1);

        Assert.Throws<InvalidDataException>(() => KeycloakClientCredentialMemoryCapture.Read(
            capture.Reference, native, new MutableClock(Now)));
        Assert.Equal(1, native.ActiveHandleCount);
    }

    [Fact]
    public void RejectsChangedHolderMetadataDuringOwnershipCheck()
    {
        var native = new FakeNative();
        using var capture = Create(native, new MutableClock(Now), "opaque-secret");
        native.StatusTransformOnRead = 3;
        native.StatusTransform = status => status with { Inode = status.Inode + 1 };

        Assert.Throws<InvalidDataException>(() => KeycloakClientCredentialMemoryCapture.Read(
            capture.Reference, native, new MutableClock(Now)));
        var owner = capture.Reference.MemoryIdentity!.FileDescriptor;
        Assert.Equal(1, native.ActiveHandleCount);
        Assert.Equal(new[] { owner }, native.ActiveDescriptors);
        Assert.Equal(new[] { owner + 1, owner + 2 }, native.ClosedDescriptors.Order());
    }

    [Fact]
    public void RejectsChangedFileMetadataAfterCopyAndClosesReaderDescriptor()
    {
        var native = new FakeNative();
        using var capture = Create(native, new MutableClock(Now), "opaque-secret");
        native.StatusTransformOnRead = 4;
        native.StatusTransform = status => status with { Inode = status.Inode + 1 };

        Assert.Throws<InvalidDataException>(() => KeycloakClientCredentialMemoryCapture.Read(
            capture.Reference, native, new MutableClock(Now)));

        var holder = capture.Reference.MemoryIdentity!.FileDescriptor;
        Assert.Equal(new[] { holder }, native.ActiveDescriptors);
        Assert.Contains(holder + 1, native.ClosedDescriptors);
    }

    [Fact]
    public void RejectsChangedHolderImmediatelyAfterOpeningTheReadDescriptor()
    {
        var native = new FakeNative();
        using var capture = Create(native, new MutableClock(Now), "opaque-secret");
        native.StatusTransformOnRead = native.FileStatusReadCount + 1;
        native.StatusTransform = status =>
        {
            native.ProcessStartCounter++;
            return status;
        };

        var failure = Assert.Throws<InvalidDataException>(() => KeycloakClientCredentialMemoryCapture.Read(
            capture.Reference, native, new MutableClock(Now)));

        var holder = capture.Reference.MemoryIdentity!.FileDescriptor;
        Assert.Equal("Sealed credential holder identity was rejected.", failure.Message);
        Assert.Equal(new[] { holder }, native.ActiveDescriptors);
        Assert.Equal(new[] { holder + 1 }, native.ClosedDescriptors);
    }

    [Fact]
    public void RejectsChangedHolderIncarnationDuringReadbackAndClosesTemporaryDescriptors()
    {
        var native = new FakeNative();
        using var capture = Create(native, new MutableClock(Now), "opaque-secret");
        native.StatusTransformOnRead = 3;
        native.StatusTransform = status =>
        {
            native.ProcessStartCounter++;
            return status;
        };

        Assert.Throws<InvalidDataException>(() => KeycloakClientCredentialMemoryCapture.Read(
            capture.Reference, native, new MutableClock(Now)));

        var owner = capture.Reference.MemoryIdentity!.FileDescriptor;
        Assert.Equal(new[] { owner }, native.ActiveDescriptors);
        Assert.Equal(new[] { owner + 1, owner + 2 }, native.ClosedDescriptors.Order());
    }

    [Fact]
    public void RejectsOwnerReadbackIoctlFailureAndClosesTemporaryDescriptors()
    {
        var native = new FakeNative { FailGetSealsCall = 3 };
        using var capture = Create(native, new MutableClock(Now), "opaque-secret");

        Assert.Throws<InvalidDataException>(() => KeycloakClientCredentialMemoryCapture.Read(
            capture.Reference, native, new MutableClock(Now)));

        var owner = capture.Reference.MemoryIdentity!.FileDescriptor;
        Assert.Equal(new[] { owner }, native.ActiveDescriptors);
        Assert.Equal(new[] { owner + 1, owner + 2 }, native.ClosedDescriptors.Order());
    }

    [Fact]
    public void RejectsTruncatedNativeCopyAndClosesReaderDescriptor()
    {
        var native = new FakeNative();
        using var capture = Create(native, new MutableClock(Now), "opaque-secret");
        native.ReportedSize = (ulong)native.GetOwnerFileLength() + 1;

        Assert.Throws<InvalidDataException>(() => KeycloakClientCredentialMemoryCapture.Read(
            capture.Reference, native, new MutableClock(Now)));

        var owner = capture.Reference.MemoryIdentity!.FileDescriptor;
        Assert.Equal(new[] { owner }, native.ActiveDescriptors);
        Assert.Equal(new[] { owner + 1, owner + 2 }, native.ClosedDescriptors.Order());
    }

    [Fact]
    public void RejectsNegativeGetSealsResultEvenWhenOpenedFileMetadataIsOtherwiseValid()
    {
        var native = new FakeNative();
        using var capture = Create(native, new MutableClock(Now), "opaque-secret");
        native.GetSealsOverride = -1;
        var statusReadsBefore = native.FileStatusReadCount;

        Assert.Throws<InvalidDataException>(() => KeycloakClientCredentialMemoryCapture.Read(capture.Reference, native, new MutableClock(Now)));
        Assert.Equal(statusReadsBefore, native.FileStatusReadCount);
    }

    [Fact]
    public void RejectsUnsealedOrReplacedFileDescriptors()
    {
        var unsealedNative = new FakeNative();
        using (var capture = Create(unsealedNative, new MutableClock(Now), "opaque-secret"))
        {
            unsealedNative.GetSealsOverride = 0;
            Assert.Throws<InvalidDataException>(() => KeycloakClientCredentialMemoryCapture.Read(capture.Reference, unsealedNative, new MutableClock(Now)));
        }

        var replacedNative = new FakeNative { ReturnReplacementFile = true };
        using var replacedCapture = Create(replacedNative, new MutableClock(Now), "opaque-secret");
        Assert.Throws<InvalidDataException>(() => KeycloakClientCredentialMemoryCapture.Read(replacedCapture.Reference, replacedNative, new MutableClock(Now)));
    }

    [Fact]
    public void RejectsWrongBindingExpiredFrameAndMalformedFrame()
    {
        var native = new FakeNative();
        using var capture = Create(native, new MutableClock(Now), "opaque-secret");
        var wrongRun = KeycloakClientCredentialReference.ForSealedMemory(
            capture.Reference.MemoryIdentity!, "fedcba9876543210fedcba9876543210", Authority, "web-client");
        Assert.Throws<InvalidDataException>(() => KeycloakClientCredentialMemoryCapture.Read(
            wrongRun, native, new MutableClock(Now)));

        var wrongClient = KeycloakClientCredentialReference.ForSealedMemory(
            capture.Reference.MemoryIdentity!, RunId, Authority, "other-client");
        Assert.Throws<InvalidDataException>(() => KeycloakClientCredentialMemoryCapture.Read(
            wrongClient, native, new MutableClock(Now)));

        var wrongAuthority = KeycloakClientCredentialReference.ForSealedMemory(
            capture.Reference.MemoryIdentity!, RunId, new Uri("https://identity.example/realms/other"), "web-client");
        Assert.Throws<InvalidDataException>(() => KeycloakClientCredentialMemoryCapture.Read(wrongAuthority, native, new MutableClock(Now)));

        var expiredClock = new MutableClock(Now.AddMinutes(6));
        Assert.Throws<InvalidDataException>(() => KeycloakClientCredentialMemoryCapture.Read(capture.Reference, native, expiredClock));

        var malformedNative = new FakeNative { CorruptMagicAfterSeal = true };
        using var malformedCapture = Create(malformedNative, new MutableClock(Now), "opaque-secret");
        Assert.Throws<InvalidDataException>(() => KeycloakClientCredentialMemoryCapture.Read(malformedCapture.Reference, malformedNative, new MutableClock(Now)));

        var malformedHeaderNative = new FakeNative { CorruptHeaderAfterSeal = true };
        using var malformedHeaderCapture = Create(malformedHeaderNative, new MutableClock(Now), "opaque-secret");
        Assert.Throws<InvalidDataException>(() => KeycloakClientCredentialMemoryCapture.Read(malformedHeaderCapture.Reference, malformedHeaderNative, new MutableClock(Now)));

        var invalidUtf8Native = new FakeNative { CorruptSecretUtf8AfterSeal = true };
        using var invalidUtf8Capture = Create(invalidUtf8Native, new MutableClock(Now), "opaque-secret");
        Assert.Throws<InvalidDataException>(() => KeycloakClientCredentialMemoryCapture.Read(invalidUtf8Capture.Reference, invalidUtf8Native, new MutableClock(Now)));

        var whitespaceNative = new FakeNative();
        using var whitespaceCapture = Create(whitespaceNative, new MutableClock(Now), "opaque-secret");
        whitespaceNative.TransformFrameForTest(frame =>
        {
            var headerLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(4, 4)));
            var secretLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(8, 4)));
            Array.Fill(frame, (byte)' ', 12 + headerLength, secretLength);
        });
        Assert.Throws<InvalidDataException>(() => KeycloakClientCredentialMemoryCapture.Read(
            whitespaceCapture.Reference, whitespaceNative, new MutableClock(Now)));
    }

    [Theory]
    [InlineData("zero-header")]
    [InlineData("oversized-header")]
    [InlineData("zero-secret")]
    [InlineData("oversized-secret")]
    [InlineData("inconsistent-total")]
    public void RejectsMalformedFrameLengthsBeforeDecodingHeader(string mutation)
    {
        var native = new FakeNative();
        using var capture = Create(native, new MutableClock(Now), "opaque-secret");
        var holder = capture.Reference.MemoryIdentity!.FileDescriptor;
        var closedBeforeRead = native.ClosedDescriptors.Count;
        native.TransformFrameForTest(frame =>
        {
            switch (mutation)
            {
                case "zero-header": BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4, 4), 0); break;
                case "oversized-header": BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4, 4), 4097); break;
                case "zero-secret": BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(8, 4), 0); break;
                case "oversized-secret": BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(8, 4), 4097); break;
                case "inconsistent-total": BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(8, 4), 1); break;
                default: throw new InvalidOperationException($"Unexpected frame mutation: {mutation}.");
            }
        });

        var failure = Assert.Throws<InvalidDataException>(() => KeycloakClientCredentialMemoryCapture.Read(
            capture.Reference, native, new MutableClock(Now)));

        Assert.Equal("Sealed credential frame length was rejected.", failure.Message);
        Assert.Equal(new[] { holder }, native.ActiveDescriptors);
        Assert.Equal(new[] { holder + 1, holder + 2, holder + 3 }, native.ClosedDescriptors.Skip(closedBeforeRead).Order());
    }

    [Fact]
    public void RejectsOversizedFrameAndChangedHolderIncarnation()
    {
        var oversizedNative = new FakeNative();
        using (var capture = Create(oversizedNative, new MutableClock(Now), "opaque-secret"))
        {
            oversizedNative.ReportedSize = 12UL + 4096 + 4096 + 1;
            Assert.Throws<InvalidDataException>(() => KeycloakClientCredentialMemoryCapture.Read(capture.Reference, oversizedNative, new MutableClock(Now)));
        }

        var replacedProcess = new FakeNative();
        using var processCapture = Create(replacedProcess, new MutableClock(Now), "opaque-secret");
        replacedProcess.ProcessStartCounter++;
        Assert.Throws<InvalidDataException>(() => KeycloakClientCredentialMemoryCapture.Read(processCapture.Reference, replacedProcess, new MutableClock(Now)));
    }

    [Fact]
    public void BoundsRepeatedEintrAndClosesDescriptors()
    {
        var partialWriteNative = new FakeNative { StopWritingAfterBytes = 5 };
        Assert.Throws<IOException>(() => Create(partialWriteNative, new MutableClock(Now), "opaque-secret"));
        Assert.Equal(0, partialWriteNative.ActiveHandleCount);

        var writeNative = new FakeNative { InterruptedWrites = 17 };
        Assert.Throws<IOException>(() => Create(writeNative, new MutableClock(Now), "opaque-secret"));
        Assert.Equal(0, writeNative.ActiveHandleCount);

        var readNative = new FakeNative();
        using var capture = Create(readNative, new MutableClock(Now), "opaque-secret");
        readNative.InterruptedReads = 17;
        Assert.Throws<InvalidDataException>(() => KeycloakClientCredentialMemoryCapture.Read(capture.Reference, readNative, new MutableClock(Now)));
        Assert.Equal(1, readNative.ActiveHandleCount); // the capture's owner handle remains until disposal

        var trailingReadNative = new FakeNative { InterruptedTrailingReads = 17 };
        using var trailingReadCapture = Create(trailingReadNative, new MutableClock(Now), "opaque-secret");
        var holder = trailingReadCapture.Reference.MemoryIdentity!.FileDescriptor;
        Assert.Throws<InvalidDataException>(() => KeycloakClientCredentialMemoryCapture.Read(
            trailingReadCapture.Reference, trailingReadNative, new MutableClock(Now)));
        Assert.Equal(new[] { holder }, trailingReadNative.ActiveDescriptors);
        Assert.Equal(new[] { holder + 1, holder + 2 }, trailingReadNative.ClosedDescriptors.Order());
    }

    [Fact]
    public void ParsesProcessStartCounterAfterParenthesizedProcessName()
    {
        var stat = CreateProcStat("987654321", processName: "worker name with ) inside");

        Assert.Equal(987654321L, KeycloakClientCredentialMemoryCapture.ParseProcessStartCounter(stat));
    }

    [Theory]
    [InlineData("missing-parenthesis")]
    [InlineData("truncated-after-name")]
    [InlineData("missing-separator")]
    [InlineData("too-few-fields")]
    [InlineData("invalid-counter")]
    [InlineData("zero-counter")]
    public void RejectsMalformedProcessStartCounterRecords(string mutation)
    {
        var stat = mutation switch
        {
            "missing-parenthesis" => "123 worker S 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1",
            "truncated-after-name" => "123 (worker)",
            "missing-separator" => CreateProcStat("987654321").Replace(") S ", ")S ", StringComparison.Ordinal),
            "too-few-fields" => CreateProcStat("987654321", fieldCount: 18),
            "invalid-counter" => CreateProcStat("not-a-number"),
            "zero-counter" => CreateProcStat("0"),
            _ => throw new InvalidOperationException($"Unexpected process stat mutation: {mutation}.")
        };

        Assert.Throws<InvalidDataException>(() => KeycloakClientCredentialMemoryCapture.ParseProcessStartCounter(stat));
    }

    [Fact]
    public void ParsesFixedLinuxStatxOffsetsIntoMemoryFileStatus()
    {
        const uint requiredMask = 1U | 2U | 4U | 8U | 0x100U | 0x200U;
        const uint mask = requiredMask | 0x800U;
        const ushort mode = 0x81A5;
        const uint userId = 0x10203040;
        const uint linkCount = 0x50607080;
        const ulong inode = 0x1122334455667788UL;
        const ulong size = 0x8877665544332211UL;
        var statx = new byte[StatxBufferSize];
        BinaryPrimitives.WriteUInt32LittleEndian(statx, mask);
        BinaryPrimitives.WriteUInt32LittleEndian(statx.AsSpan(16), linkCount);
        BinaryPrimitives.WriteUInt32LittleEndian(statx.AsSpan(20), userId);
        BinaryPrimitives.WriteUInt16LittleEndian(statx.AsSpan(28), mode);
        BinaryPrimitives.WriteUInt64LittleEndian(statx.AsSpan(32), inode);
        BinaryPrimitives.WriteUInt64LittleEndian(statx.AsSpan(40), size);

        var status = KeycloakClientCredentialMemoryCapture.ParseStatxMemoryStatus(statx);

        Assert.Equal(new KeycloakClientCredentialMemoryCapture.MemoryFileStatus(
            mask, mode, userId, linkCount, inode, size), status);
    }

    [Theory]
    [InlineData(255)]
    [InlineData(257)]
    public void RejectsStatxBuffersWithUnexpectedSize(int size)
    {
        Assert.Throws<InvalidDataException>(() => KeycloakClientCredentialMemoryCapture.ParseStatxMemoryStatus(
            new byte[size]));
    }

    [Fact]
    public void ProductionEntryPointFailsClosedOffLinuxArm64()
    {
        if (OperatingSystem.IsLinux() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
            System.Runtime.InteropServices.Architecture.Arm64) return;

        Assert.Throws<PlatformNotSupportedException>(() => KeycloakClientCredentialMemoryCapture.Create(
            RunId, Authority, "web-client", "opaque-secret"));
        Assert.Throws<PlatformNotSupportedException>(() => KeycloakClientCredentialMemoryCapture.Create(
            RunId, Authority, "web-client", "opaque-secret", new MutableClock(Now)));
        Assert.Throws<PlatformNotSupportedException>(() => KeycloakClientCredentialMemoryCapture.CreateForOwnerLifetime(
            RunId, Authority, "web-client", "opaque-secret"));

        using var capture = Create(new FakeNative(), new MutableClock(Now), "opaque-secret");
        Assert.Throws<PlatformNotSupportedException>(() => capture.Reference.Read());
        Assert.Throws<PlatformNotSupportedException>(() => KeycloakClientCredentialMemoryCapture.Read(
            capture.Reference, new MutableClock(Now)));
    }

    private static KeycloakClientCredentialMemoryCapture Create(FakeNative native, MutableClock clock, string value) =>
        KeycloakClientCredentialMemoryCapture.Create(RunId, Authority, "web-client", value, native, clock);

    private static KeycloakClientCredentialMemoryCapture CreateForOwnerLifetime(FakeNative native, MutableClock clock, string value) =>
        KeycloakClientCredentialMemoryCapture.CreateForOwnerLifetime(RunId, Authority, "web-client", value, native, clock);

    private static string CreateProcStat(string startCounter, string processName = "worker", int fieldCount = 19)
    {
        var fields = Enumerable.Repeat("1", fieldCount).ToArray();
        if (fieldCount > 18) fields[18] = startCounter;
        return $"123 ({processName}) S {string.Join(" ", fields)}";
    }

    private static string RemoveJsonProperty(string json, string propertyName)
    {
        var document = JsonNode.Parse(json)!.AsObject();
        document.Remove(propertyName);
        return document.ToJsonString();
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    // These fakes exercise protocol and failure behavior only; Linux qualification comes from the ARM64 probe.
    private sealed class FakeNative : KeycloakClientCredentialMemoryCapture.IMemoryNative
    {
        private const uint StatxFields = 1 | 2 | 4 | 8 | 0x100 | 0x200;
        private readonly Dictionary<int, Handle> _handles = [];
        private int _nextDescriptor = 20;
        private ulong _nextInode = 100;
        private int _lastError;

        internal int ActiveHandleCount => _handles.Count;
        internal int[] ActiveDescriptors => _handles.Keys.Order().ToArray();
        internal List<int> ClosedDescriptors { get; } = [];
        internal int CreateCount { get; private set; }
        internal int ProcessStartCounterReadCount { get; private set; }
        internal int FileStatusReadCount { get; private set; }
        internal int MaximumWrite { get; set; } = int.MaxValue;
        internal int MaximumRead { get; set; } = int.MaxValue;
        internal int InterruptedWrites { get; set; }
        internal int InterruptedReads { get; set; }
        internal int InterruptedTrailingReads { get; set; }
        internal int FailGetSealsCall { get; set; }
        internal int? StopWritingAfterBytes { get; set; }
        internal int? GetSealsOverride { get; set; }
        internal bool ReturnReplacementFile { get; set; }
        internal bool CorruptMagicAfterSeal { get; set; }
        internal bool CorruptHeaderAfterSeal { get; set; }
        internal bool CorruptSecretUtf8AfterSeal { get; set; }
        internal bool CorruptSecretControlAfterSeal { get; set; }
        internal bool CloseOwnerAfterPayloadRead { get; set; }
        internal bool ChangeProcessStartAfterPayloadRead { get; set; }
        internal bool FailCreate { get; set; }
        internal int FchmodResult { get; set; }
        internal int AddSealsResult { get; set; }
        internal int FailOpenCall { get; set; }
        internal ulong? ReportedSize { get; set; }
        internal int StatusTransformOnRead { get; set; }
        internal Func<KeycloakClientCredentialMemoryCapture.MemoryFileStatus, KeycloakClientCredentialMemoryCapture.MemoryFileStatus>? StatusTransform { get; set; }
        internal long ProcessStartCounter { get; set; } = 987654321;
        private int _ownerDescriptor = -1;
        private int _openCallCount;
        private int _getSealsCallCount;
        public int LastError => _lastError;
        public uint EffectiveUserId => 1000;

        public int CreateMemfd(string name, int flags)
        {
            CreateCount++;
            if (FailCreate) return -1;
            var descriptor = _nextDescriptor++;
            _handles.Add(descriptor, new Handle(new FileObject(_nextInode++)));
            _ownerDescriptor = descriptor;
            return descriptor;
        }

        public int Write(int descriptor, byte[] buffer, int offset, int count)
        {
            if (InterruptedWrites > 0)
            {
                InterruptedWrites--;
                _lastError = 4;
                return -1;
            }
            _lastError = 0;
            var file = _handles[descriptor].File;
            if (file.Sealed) return -1;
            if (StopWritingAfterBytes is { } stop && file.Bytes.Count >= stop) return 0;
            var limit = Math.Min(count, MaximumWrite);
            if (StopWritingAfterBytes is { } byteLimit) limit = Math.Min(limit, byteLimit - file.Bytes.Count);
            for (var index = 0; index < limit; index++) file.Bytes.Add(buffer[offset + index]);
            return limit;
        }

        public int Read(int descriptor, byte[] buffer, int offset, int count)
        {
            if (InterruptedReads > 0)
            {
                InterruptedReads--;
                _lastError = 4;
                return -1;
            }
            _lastError = 0;
            var handle = _handles[descriptor];
            if (InterruptedTrailingReads > 0 && handle.Position == handle.File.Bytes.Count)
            {
                InterruptedTrailingReads--;
                _lastError = 4;
                return -1;
            }
            var available = handle.File.Bytes.Count - handle.Position;
            if (available <= 0) return 0;
            var length = Math.Min(Math.Min(count, MaximumRead), available);
            handle.File.Bytes.CopyTo(handle.Position, buffer, offset, length);
            handle.Position += length;
            if (CloseOwnerAfterPayloadRead && handle.Position == handle.File.Bytes.Count)
            {
                _handles.Remove(_ownerDescriptor);
                CloseOwnerAfterPayloadRead = false;
            }
            if (ChangeProcessStartAfterPayloadRead && handle.Position == handle.File.Bytes.Count)
            {
                ProcessStartCounter++;
                ChangeProcessStartAfterPayloadRead = false;
            }
            return length;
        }

        public int Fchmod(int descriptor, uint mode)
        {
            if (FchmodResult != 0) return FchmodResult;
            _handles[descriptor].File.Mode = checked((ushort)mode);
            return 0;
        }

        public int Fcntl(int descriptor, int command, int argument)
        {
            var file = _handles[descriptor].File;
            if (command == 1033)
            {
                if (AddSealsResult != 0) return AddSealsResult;
                file.Sealed = true;
                if (CorruptMagicAfterSeal) file.Bytes[0] ^= 0xff;
                if (CorruptHeaderAfterSeal) file.Bytes[12] = 0xff;
                if (CorruptSecretUtf8AfterSeal) file.Bytes[^1] = 0xff;
                if (CorruptSecretControlAfterSeal) file.Bytes[^1] = (byte)'\n';
                return 0;
            }
            if (command == 1034)
            {
                if (++_getSealsCallCount == FailGetSealsCall) return -1;
                return GetSealsOverride ?? (file.Sealed ? 15 : 0);
            }
            return -1;
        }

        public int OpenForRead(int processId, int descriptor)
        {
            _openCallCount++;
            if (_openCallCount == FailOpenCall) return -1;
            if (processId != Environment.ProcessId || !_handles.TryGetValue(descriptor, out var owner)) return -1;
            var file = owner.File;
            if (ReturnReplacementFile)
            {
                var replacement = new FileObject(file.Inode + 1) { Mode = file.Mode, Sealed = true };
                replacement.Bytes.AddRange(file.Bytes);
                file = replacement;
            }
            var opened = _nextDescriptor++;
            _handles.Add(opened, new Handle(file));
            return opened;
        }

        public KeycloakClientCredentialMemoryCapture.MemoryFileStatus GetFileStatus(int descriptor)
        {
            FileStatusReadCount++;
            var file = _handles[descriptor].File;
            var size = ReportedSize ?? (ulong)file.Bytes.Count;
            var status = new KeycloakClientCredentialMemoryCapture.MemoryFileStatus(
                StatxFields, (ushort)(0x8000 | file.Mode), EffectiveUserId, 0, file.Inode, size);
            return (StatusTransformOnRead == 0 || FileStatusReadCount == StatusTransformOnRead)
                ? StatusTransform?.Invoke(status) ?? status
                : status;
        }

        public long GetProcessStartCounter(int processId)
        {
            ProcessStartCounterReadCount++;
            return processId == Environment.ProcessId ? ProcessStartCounter : throw new InvalidDataException();
        }

        public void Close(int descriptor)
        {
            if (_handles.Remove(descriptor)) ClosedDescriptors.Add(descriptor);
        }

        internal void TransformOwnerFrameForTest(Func<string, string> transform) =>
            TransformHeader(_handles[_ownerDescriptor].File, transform);

        internal void TransformFrameForTest(Action<byte[]> transform)
        {
            var bytes = _handles[_ownerDescriptor].File.Bytes.ToArray();
            transform(bytes);
            _handles[_ownerDescriptor].File.Bytes.Clear();
            _handles[_ownerDescriptor].File.Bytes.AddRange(bytes);
        }

        internal int GetOwnerFileLength() => _handles[_ownerDescriptor].File.Bytes.Count;

        private static void TransformHeader(FileObject file, Func<string, string> transform)
        {
            var lengthBytes = file.Bytes.GetRange(4, 8).ToArray();
            var originalHeaderLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(lengthBytes));
            var originalSecretLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(lengthBytes.AsSpan(4)));
            var originalHeaderBytes = file.Bytes.GetRange(12, originalHeaderLength).ToArray();
            var transformedHeaderBytes = Encoding.UTF8.GetBytes(transform(Encoding.UTF8.GetString(originalHeaderBytes)));
            try
            {
                var lengthDelta = transformedHeaderBytes.Length - originalHeaderLength;
                var transformedSecretLength = originalSecretLength - lengthDelta;
                if (transformedSecretLength < 1) throw new InvalidOperationException("Malformed test header exceeds the fake frame bounds.");
                file.Bytes.RemoveRange(12, originalHeaderLength);
                file.Bytes.InsertRange(12, transformedHeaderBytes);
                var secretOffset = 12 + transformedHeaderBytes.Length;
                if (lengthDelta > 0)
                    file.Bytes.RemoveRange(secretOffset + transformedSecretLength, lengthDelta);
                else if (lengthDelta < 0)
                    file.Bytes.InsertRange(secretOffset, Enumerable.Repeat((byte)0, -lengthDelta));
                file.Bytes[4] = (byte)(transformedHeaderBytes.Length >> 24);
                file.Bytes[5] = (byte)(transformedHeaderBytes.Length >> 16);
                file.Bytes[6] = (byte)(transformedHeaderBytes.Length >> 8);
                file.Bytes[7] = (byte)transformedHeaderBytes.Length;
                file.Bytes[8] = (byte)(transformedSecretLength >> 24);
                file.Bytes[9] = (byte)(transformedSecretLength >> 16);
                file.Bytes[10] = (byte)(transformedSecretLength >> 8);
                file.Bytes[11] = (byte)transformedSecretLength;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(originalHeaderBytes);
                CryptographicOperations.ZeroMemory(transformedHeaderBytes);
                CryptographicOperations.ZeroMemory(lengthBytes);
            }
        }

        private sealed class Handle(FileObject file)
        {
            internal FileObject File { get; } = file;
            internal int Position { get; set; }
        }

        private sealed class FileObject(ulong inode)
        {
            internal ulong Inode { get; } = inode;
            internal ushort Mode { get; set; }
            internal bool Sealed { get; set; }
            internal List<byte> Bytes { get; } = [];
        }
    }
}
