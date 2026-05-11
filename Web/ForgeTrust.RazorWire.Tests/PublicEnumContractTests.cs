namespace ForgeTrust.RazorWire.Tests;

public class PublicEnumContractTests
{
    [Theory]
    [InlineData(RazorWireFormFailureMode.Auto, 0)]
    [InlineData(RazorWireFormFailureMode.Manual, 1)]
    [InlineData(RazorWireFormFailureMode.Off, 2)]
    public void RazorWireFormFailureMode_NumericValues_AreStable(
        RazorWireFormFailureMode value,
        int expected)
    {
        Assert.Equal(expected, (int)value);
    }
}
