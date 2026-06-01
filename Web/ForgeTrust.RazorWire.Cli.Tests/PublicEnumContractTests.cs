namespace ForgeTrust.RazorWire.Cli.Tests;

public class PublicEnumContractTests
{
    [Theory]
    [InlineData(ExportSourceKind.Url, 0)]
    [InlineData(ExportSourceKind.Project, 1)]
    [InlineData(ExportSourceKind.Dll, 2)]
    public void ExportSourceKind_NumericValues_AreStable(
        ExportSourceKind value,
        int expected)
    {
        Assert.Equal(expected, (int)value);
    }
}
