using ForgeTrust.AppSurface.Durable.Examples;

namespace ForgeTrust.AppSurface.Durable.Tests;

public sealed class TypedWorkDefinitionProofTests
{
    [Fact]
    public void Complete_typed_work_example_is_passive_and_preserves_request_parity() =>
        TypedWorkDefinitionProof.Run();
}
