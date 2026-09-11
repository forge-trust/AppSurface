using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Workers;

namespace ForgeTrust.AppSurface.Durable.Tests;

public sealed class DurableWorkDefinitionClosureBoundaryTests
{
    [Theory]
    [InlineData("\0", "workName")]
    [InlineData("\u0001", "workName")]
    [InlineData("\u001f", "workName")]
    [InlineData("\u007f", "workName")]
    [InlineData("\r\n", "workVersion")]
    [InlineData("\u000b", "workVersion")]
    public void Define_rejects_control_characters_in_work_identity(string invalidValue, string parameterName)
    {
        var codec = new DefinitionTestCodec<string>();
        var workName = parameterName == "workName" ? invalidValue : "tests.work";
        var workVersion = parameterName == "workVersion" ? invalidValue : "v1";

        var exception = Assert.ThrowsAny<ArgumentException>(() => DurableWork.Define(
            workName, workVersion, codec, codec, DurableProviderSafety.Idempotent,
            DurableWorkRetryPolicy.Default));

        Assert.Equal(parameterName, exception.ParamName);
    }

    [Theory]
    [InlineData("ContractName")]
    [InlineData("ContractVersion")]
    [InlineData("RetentionPolicyId")]
    public void Define_rejects_control_characters_in_codec_metadata(string property)
    {
        var value = "valid\u0001metadata";
        var codec = new DefinitionTestCodec<string>(
            contractName: property == "ContractName" ? value : "tests.codec",
            contractVersion: property == "ContractVersion" ? value : "v1",
            retentionPolicyId: property == "RetentionPolicyId" ? value : "retention");

        Assert.Throws<ArgumentException>(() => DurableWork.Define(
            "tests.work", "v1", codec, codec, DurableProviderSafety.Idempotent,
            DurableWorkRetryPolicy.Default));
    }

    [Fact]
    public void Define_accepts_exact_codec_metadata_boundaries()
    {
        var codec = new DefinitionTestCodec<string>(
            contractName: new string('n', 200),
            contractVersion: new string('v', 100),
            retentionPolicyId: new string('r', 128));

        var definition = DurableWork.Define(
            new string('w', 200), new string('x', 100), codec, codec,
            DurableProviderSafety.Idempotent, DurableWorkRetryPolicy.Default);

        Assert.Equal(200, definition.WorkName.Length);
        Assert.Equal(100, definition.WorkVersion.Length);
        Assert.Equal(new DurableWorkContractIdentity(definition.WorkName, definition.WorkVersion), definition.ContractIdentity);
        Assert.Equal(200, definition.WorkCodec.ContractName.Length);
        Assert.Equal(100, definition.WorkCodec.ContractVersion.Length);
        Assert.Equal(128, definition.WorkCodec.RetentionPolicyId.Length);
    }

    [Fact]
    public void Consumer_registration_preserves_raw_codec_identity_and_registry_uses_captured_facts()
    {
        var workCodec = new DefinitionTestCodec<string>(contractName: "consumer.work");
        var resultCodec = new DefinitionTestCodec<string>(contractName: "consumer.result");
        var registration = new ConsumerRegistration(
            "consumer.contract", "v1", DurableProviderSafety.Idempotent, workCodec, resultCodec);

        Assert.Equal("consumer.contract", registration.WorkName);
        Assert.Equal("v1", registration.WorkVersion);
        Assert.Same(workCodec, registration.WorkCodec);
        Assert.Same(resultCodec, registration.ResultCodec);

        var workRegistry = new DurableWorkRegistry([registration]);
        Assert.Same(registration, workRegistry.GetRequired("consumer.contract", "v1"));

        var codecRegistry = new DurablePayloadCodecRegistry([registration.WorkCodec, registration.ResultCodec]);
        workCodec.ContractNameValue = "consumer.work.changed";
        resultCodec.ContractNameValue = "consumer.result.changed";

        Assert.Same(workCodec, codecRegistry.GetRequired("consumer.work", "v1"));
        Assert.Same(resultCodec, codecRegistry.GetRequired("consumer.result", "v1"));

        var guarded = DurablePayloadCodecSnapshot.Capture(workCodec).CreateView();
        var mismatch = new DurableEncodedPayload(
            "consumer.work", "v1", DurableDataClassification.Operational, "value"u8.ToArray());
        Assert.Throws<InvalidOperationException>(() => guarded.DecodeObject(mismatch));
        Assert.Equal(0, workCodec.DecodeCalls);
    }

    [Fact]
    public void Consumer_registration_rejects_conflicting_shared_snapshot_facts()
    {
        var source = new DefinitionTestCodec<string>(contractName: "shared.original");
        var oldView = DurablePayloadCodecSnapshot.Capture(source).CreateView();
        source.ContractNameValue = "shared.changed";
        var newView = DurablePayloadCodecSnapshot.Capture(source).CreateView();

        var exception = Assert.Throws<InvalidOperationException>(() => new ConsumerRegistration(
            "shared.contract", "v1", DurableProviderSafety.Idempotent, oldView, newView));

        Assert.Equal(
            "A durable payload codec source was contributed with conflicting contract metadata.",
            exception.Message);
    }

    [Fact]
    public void Define_reuses_one_shared_dual_interface_codec_snapshot_for_matching_generic_types()
    {
        var codec = new DualPayloadCodec(typeof(string));

        var definition = DurableWork.Define<string, string>(
            "dual.shared", "v1", codec, codec, DurableProviderSafety.Idempotent,
            DurableWorkRetryPolicy.Default);

        Assert.Same(definition.WorkCodec, definition.ResultCodec);
        Assert.Equal(typeof(string), definition.WorkCodec.PayloadType);
    }

    [Theory]
    [InlineData("work")]
    [InlineData("result")]
    public void Define_checks_generic_payload_type_independently_for_shared_dual_interface_codec(
        string mismatch)
    {
        var source = new DualPayloadCodec(mismatch == "work" ? typeof(int) : typeof(string));
        Action action = mismatch == "work"
            ? () => _ = DurableWork.Define<string, int>(
                "dual.work", "v1", (IDurablePayloadCodec<string>)source,
                (IDurablePayloadCodec<int>)DurablePayloadCodecSnapshot.Capture<int>(
                    (IDurablePayloadCodec<int>)source).CreateView(), DurableProviderSafety.Idempotent,
                DurableWorkRetryPolicy.Default)
            : () => _ = DurableWork.Define<string, int>(
                "dual.work", "v1", (IDurablePayloadCodec<string>)source,
                (IDurablePayloadCodec<int>)source, DurableProviderSafety.Idempotent,
                DurableWorkRetryPolicy.Default);

        Assert.Throws<ArgumentException>(action);
    }

    private sealed class ConsumerRegistration(
        string workName,
        string workVersion,
        DurableProviderSafety providerSafety,
        IDurablePayloadCodec workCodec,
        IDurablePayloadCodec resultCodec)
        : DurableWorkRegistration(workName, workVersion, providerSafety, workCodec, resultCodec)
    {
        public override bool CanReconcile => false;

        public override DurablePreparedWork Prepare(IServiceProvider services, DurableWorkExecutionContext work) =>
            throw new NotSupportedException();

        public override ValueTask<DurableEncodedPayload> InvokeAsync(
            IServiceProvider services, DurableWorkExecutionContext work,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public override ValueTask<DurableEncodedEffectReconciliation> ReconcileAsync(
            IServiceProvider services, DurableWorkExecutionContext work,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class DualPayloadCodec(Type payloadType) :
        IDurablePayloadCodec<string>, IDurablePayloadCodec<int>
    {
        public Type PayloadType => payloadType;
        public string ContractName => "dual.payload";
        public string ContractVersion => "v1";
        public DurableDataClassification Classification => DurableDataClassification.Operational;
        public string RetentionPolicyId => DurableEncodedPayload.DefaultRetentionPolicyId;

        DurableEncodedPayload IDurablePayloadCodec<string>.Encode(string value) => EncodeValue(value);
        DurableEncodedPayload IDurablePayloadCodec<int>.Encode(int value) => EncodeValue(value.ToString());
        string IDurablePayloadCodec<string>.Decode(DurableEncodedPayload payload) =>
            System.Text.Encoding.UTF8.GetString(payload.Content.Span);
        int IDurablePayloadCodec<int>.Decode(DurableEncodedPayload payload) =>
            int.Parse(System.Text.Encoding.UTF8.GetString(payload.Content.Span));
        public DurableEncodedPayload EncodeObject(object value) => value switch
        {
            string text => ((IDurablePayloadCodec<string>)this).Encode(text),
            int number => ((IDurablePayloadCodec<int>)this).Encode(number),
            _ => throw new ArgumentException("Unsupported dual codec value.", nameof(value)),
        };
        public object DecodeObject(DurableEncodedPayload payload) =>
            ((IDurablePayloadCodec<string>)this).Decode(payload);

        private static DurableEncodedPayload EncodeValue(string value) => new(
            "dual.payload", "v1", DurableDataClassification.Operational,
            System.Text.Encoding.UTF8.GetBytes(value));
    }
}
