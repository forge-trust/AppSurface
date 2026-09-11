using System.Collections.Concurrent;
using ForgeTrust.AppSurface.Durable;

namespace ForgeTrust.AppSurface.Durable.Tests;

public sealed class DurablePayloadCodecSnapshotTests
{
    [Fact]
    public void Capture_reads_each_metadata_getter_once_and_freezes_the_view()
    {
        var counts = new ConcurrentDictionary<string, int>();
        var codec = new DefinitionTestCodec<string>(getterCounts: counts);
        var snapshot = DurablePayloadCodecSnapshot.Capture<string>(codec);
        var view = Assert.IsAssignableFrom<IDurablePayloadCodec<string>>(snapshot.CreateView());

        _ = view.PayloadType;
        _ = view.ContractName;
        _ = view.ContractVersion;
        _ = view.Classification;
        _ = view.RetentionPolicyId;

        Assert.All(new[] { "PayloadType", "ContractName", "ContractVersion", "Classification", "RetentionPolicyId" },
            property => Assert.Equal(1, counts[property]));
    }

    [Theory]
    [InlineData("ContractName", "")]
    [InlineData("ContractName", "line\nbreak")]
    [InlineData("ContractVersion", "")]
    [InlineData("ContractVersion", "line\nbreak")]
    [InlineData("ContractVersion", "vvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvv")]
    [InlineData("RetentionPolicyId", "")]
    [InlineData("RetentionPolicyId", "line\nbreak")]
    [InlineData("RetentionPolicyId", "rrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrr")]
    public void Capture_rejects_invalid_metadata_fields(string property, string value)
    {
        var codec = new DefinitionTestCodec<string>(
            contractName: property == "ContractName" ? value : "tests.payload",
            contractVersion: property == "ContractVersion" ? value : "v1",
            retentionPolicyId: property == "RetentionPolicyId" ? value : "retention");

        Assert.Throws<ArgumentException>(() => DurablePayloadCodecSnapshot.Capture<string>(codec));
    }

    [Fact]
    public void Capture_rejects_null_and_one_past_limit_for_each_text_metadata_field()
    {
        Assert.Throws<ArgumentException>(() => DurablePayloadCodecSnapshot.Capture<string>(
            new DefinitionTestCodec<string>(contractName: null!)));
        Assert.Throws<ArgumentException>(() => DurablePayloadCodecSnapshot.Capture<string>(
            new DefinitionTestCodec<string>(contractVersion: null!)));
        Assert.Throws<ArgumentException>(() => DurablePayloadCodecSnapshot.Capture<string>(
            new DefinitionTestCodec<string>(retentionPolicyId: null!)));

        Assert.Throws<ArgumentException>(() => DurablePayloadCodecSnapshot.Capture<string>(
            new DefinitionTestCodec<string>(contractName: new string('n', 201))));
        Assert.Throws<ArgumentException>(() => DurablePayloadCodecSnapshot.Capture<string>(
            new DefinitionTestCodec<string>(contractVersion: new string('v', 101))));
        Assert.Throws<ArgumentException>(() => DurablePayloadCodecSnapshot.Capture<string>(
            new DefinitionTestCodec<string>(retentionPolicyId: new string('r', 129))));
    }

    [Fact]
    public void Capture_accepts_exact_metadata_limits()
    {
        var snapshot = DurablePayloadCodecSnapshot.Capture<string>(new DefinitionTestCodec<string>(
            contractName: new string('n', 200),
            contractVersion: new string('v', 100),
            retentionPolicyId: new string('r', 128)));

        Assert.Equal(200, snapshot.Facts.ContractName.Length);
        Assert.Equal(100, snapshot.Facts.ContractVersion.Length);
        Assert.Equal(128, snapshot.Facts.RetentionPolicyId.Length);
    }

    [Fact]
    public void Capture_rejects_null_type_and_undefined_classification()
    {
        var nullType = new DefinitionTestCodec<string>(returnNullPayloadType: true);
        Assert.Throws<ArgumentNullException>(() => DurablePayloadCodecSnapshot.Capture<string>(nullType));

        var undefined = new DefinitionTestCodec<string>(classification: (DurableDataClassification)999);
        Assert.Throws<ArgumentOutOfRangeException>(() => DurablePayloadCodecSnapshot.Capture<string>(undefined));
    }

    [Fact]
    public void Capture_rejects_a_null_codec()
    {
        Assert.Throws<ArgumentNullException>(() => DurablePayloadCodecSnapshot.Capture((IDurablePayloadCodec)null!));
    }

    [Fact]
    public void Capture_freezes_metadata_when_source_values_change_after_capture()
    {
        var source = new DefinitionTestCodec<string>(
            contractName: "original", contractVersion: "v1", retentionPolicyId: "retention-original");
        var snapshot = DurablePayloadCodecSnapshot.Capture<string>(source);
        var view = Assert.IsAssignableFrom<IDurablePayloadCodec<string>>(snapshot.CreateView());

        source.ContractNameValue = "changed";
        source.ContractVersionValue = "v2";
        source.ClassificationValue = DurableDataClassification.ApprovedApplication;
        source.RetentionPolicyIdValue = "retention-changed";
        source.PayloadTypeValue = typeof(int);

        Assert.Equal("original", view.ContractName);
        Assert.Equal("v1", view.ContractVersion);
        Assert.Equal(DurableDataClassification.Operational, view.Classification);
        Assert.Equal("retention-original", view.RetentionPolicyId);
        Assert.Equal(typeof(string), view.PayloadType);
    }

    [Fact]
    public void Compatible_checks_accept_raw_and_equal_views_but_reject_equal_metadata_from_another_source()
    {
        var source = new DefinitionTestCodec<string>();
        var sameSnapshot = DurablePayloadCodecSnapshot.Capture<string>(source);
        var firstView = sameSnapshot.CreateView();
        var secondView = sameSnapshot.CreateView();
        var otherSource = new DefinitionTestCodec<string>();
        var otherView = DurablePayloadCodecSnapshot.Capture<string>(otherSource).CreateView();

        Assert.True(DurablePayloadCodecSnapshot.AreCompatible(source, firstView));
        Assert.True(DurablePayloadCodecSnapshot.AreCompatible(firstView, source));
        Assert.True(DurablePayloadCodecSnapshot.AreCompatible(firstView, secondView));
        Assert.False(DurablePayloadCodecSnapshot.AreCompatible(source, otherView));
        Assert.False(DurablePayloadCodecSnapshot.AreCompatible(firstView, otherView));
    }

    [Fact]
    public void Typed_view_guards_encode_decode_metadata_and_preserves_source_payload()
    {
        var source = new DefinitionTestCodec<string>();
        var view = Assert.IsAssignableFrom<IDurablePayloadCodec<string>>(
            DurablePayloadCodecSnapshot.Capture<string>(source).CreateView());
        var encoded = view.Encode("hello");

        Assert.Equal("hello", view.Decode(encoded));
        var decodeCountBeforeMismatch = source.DecodeCalls;
        Assert.Throws<InvalidOperationException>(() => view.Decode(
            new DurableEncodedPayload("other", encoded.ContractVersion, encoded.Classification, encoded.Content)));
        Assert.Equal(decodeCountBeforeMismatch, source.DecodeCalls);
        Assert.Throws<ArgumentNullException>(() => view.Encode(null!));
        Assert.Equal(1, source.EncodeCalls);
        Assert.Equal(1, source.DecodeCalls);
    }

    [Fact]
    public void Typed_view_rejects_null_decode_output()
    {
        var source = new DefinitionTestCodec<string>(decode: _ => null!);
        var view = Assert.IsAssignableFrom<IDurablePayloadCodec<string>>(
            DurablePayloadCodecSnapshot.Capture<string>(source).CreateView());
        var payload = view.Encode("hello");

        Assert.Throws<InvalidOperationException>(() => view.Decode(payload));
        Assert.Equal(1, source.DecodeCalls);
    }

    [Fact]
    public void Typed_view_preserves_policy_serializer_and_size_exceptions()
    {
        var policyFailure = new ArgumentException("policy rejected", "value");
        var serializerFailure = new InvalidOperationException("serializer failed");
        var policyCodec = new DefinitionTestCodec<string>(encode: _ => throw policyFailure);
        var serializerCodec = new DefinitionTestCodec<string>(encode: _ => throw serializerFailure);
        var oversizedCodec = new DefinitionTestCodec<string>(encode: _ =>
            new DurableEncodedPayload("tests.typed.payload", "v1", DurableDataClassification.Operational,
                new byte[DurableEncodedPayload.ProtocolMaximumBytes + 1]));

        var policyView = Assert.IsAssignableFrom<IDurablePayloadCodec<string>>(
            DurablePayloadCodecSnapshot.Capture<string>(policyCodec).CreateView());
        var serializerView = Assert.IsAssignableFrom<IDurablePayloadCodec<string>>(
            DurablePayloadCodecSnapshot.Capture<string>(serializerCodec).CreateView());
        var oversizedView = Assert.IsAssignableFrom<IDurablePayloadCodec<string>>(
            DurablePayloadCodecSnapshot.Capture<string>(oversizedCodec).CreateView());

        Assert.Same(policyFailure, Assert.Throws<ArgumentException>(() => policyView.Encode("x")));
        Assert.Same(serializerFailure, Assert.Throws<InvalidOperationException>(() => serializerView.Encode("x")));
        Assert.Throws<ArgumentException>(() => oversizedView.Encode("x"));
    }

    [Fact]
    public void Typed_view_rejects_null_encode_output_and_all_metadata_mismatches()
    {
        var source = new DefinitionTestCodec<string>(encode: _ => null!);
        var view = Assert.IsAssignableFrom<IDurablePayloadCodec<string>>(
            DurablePayloadCodecSnapshot.Capture<string>(source).CreateView());
        Assert.Throws<InvalidOperationException>(() => view.Encode("x"));

        var mismatches = new[]
        {
            new DurableEncodedPayload("other", "v1", DurableDataClassification.Operational, "x"u8.ToArray()),
            new DurableEncodedPayload("tests.typed.payload", "v2", DurableDataClassification.Operational, "x"u8.ToArray()),
            new DurableEncodedPayload("tests.typed.payload", "v1", DurableDataClassification.ApprovedApplication, "x"u8.ToArray()),
            new DurableEncodedPayload("tests.typed.payload", "v1", DurableDataClassification.Operational, "x"u8.ToArray(), "other-retention"),
        };
        foreach (var payload in mismatches)
        {
            Assert.Throws<InvalidOperationException>(() => view.Decode(payload));
        }
        Assert.Equal(0, source.DecodeCalls);

        foreach (var payload in mismatches)
        {
            var encodeSource = new DefinitionTestCodec<string>(encode: _ => payload);
            var encodeView = Assert.IsAssignableFrom<IDurablePayloadCodec<string>>(
                DurablePayloadCodecSnapshot.Capture<string>(encodeSource).CreateView());

            Assert.Throws<InvalidOperationException>(() => encodeView.Encode("x"));
            Assert.Equal(1, encodeSource.EncodeCalls);
        }
    }

    [Fact]
    public void Untyped_view_rejects_wrong_decoded_result_type()
    {
        var source = new DefinitionTestCodec<object>(
            payloadType: typeof(string),
            decode: _ => 42);
        var view = DurablePayloadCodecSnapshot.Capture((IDurablePayloadCodec)source).CreateView();
        var payload = new DurableEncodedPayload("tests.typed.payload", "v1", DurableDataClassification.Operational, "x"u8.ToArray());

        Assert.Throws<InvalidOperationException>(() => view.DecodeObject(payload));

        var nullResultSource = new DefinitionTestCodec<object>(
            payloadType: typeof(string), decode: _ => null!);
        var nullResultView = DurablePayloadCodecSnapshot.Capture((IDurablePayloadCodec)nullResultSource).CreateView();
        Assert.Throws<InvalidOperationException>(() => nullResultView.DecodeObject(payload));
    }

    [Fact]
    public void Typed_and_untyped_views_reject_wrong_input_types()
    {
        var source = new DefinitionTestCodec<string>();
        var typed = Assert.IsAssignableFrom<IDurablePayloadCodec<string>>(
            DurablePayloadCodecSnapshot.Capture<string>(source).CreateView());
        var untyped = DurablePayloadCodecSnapshot.Capture((IDurablePayloadCodec)source).CreateView();

        Assert.Throws<ArgumentException>(() => untyped.EncodeObject(42));
        Assert.Throws<ArgumentNullException>(() => untyped.EncodeObject(null!));
        Assert.Throws<ArgumentNullException>(() => typed.Encode(null!));
    }

    [Fact]
    public void Typed_capture_rejects_a_codec_with_the_wrong_declared_payload_type()
    {
        var codec = new DefinitionTestCodec<string>(payloadType: typeof(int));

        Assert.Throws<ArgumentException>(() => DurablePayloadCodecSnapshot.Capture<string>(codec));
    }

    [Fact]
    public void Capture_propagates_the_original_exception_from_each_metadata_getter()
    {
        foreach (var property in new[] { "PayloadType", "ContractName", "ContractVersion", "Classification", "RetentionPolicyId" })
        {
            var expected = new InvalidOperationException(property);
            var codec = new DefinitionTestCodec<string>(getterFailure: name => name == property ? expected : null);

            Assert.Same(expected, Assert.Throws<InvalidOperationException>(
                () => DurablePayloadCodecSnapshot.Capture<string>(codec)));
        }
    }
}
