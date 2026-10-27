using System.Xml.Linq;
using ForgeTrust.AppSurface.Evidence.Validation;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class NativeJUnitReaderTests
{
    [Fact]
    public void Read_RetainsAssertionInputsWithoutRetainingReportOutputOrParentDocuments()
    {
        WithReports(root =>
        {
            File.WriteAllText(Path.Join(root, "junit-one.xml"), "<testsuite tests='3' failures='1' errors='0' skipped='1'><testcase name='good'><system-out>large output</system-out></testcase><testcase name='failed'><failure>details</failure></testcase><testcase name='skipped'><skipped /></testcase></testsuite>");
            var cases = InstalledConsumerProducer.ReadJUnitReportsForTests(root, 1);

            Assert.Equal(3, cases.Count);
            Assert.All(cases, test => { Assert.Null(test.Parent); Assert.Null(test.Document); Assert.Empty(test.Value); });
            Assert.Equal("good", cases[0].Attribute("name")!.Value);
            Assert.Empty(cases[0].Elements());
            Assert.NotNull(cases[1].Element("failure"));
            Assert.NotNull(cases[2].Element("skipped"));
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100001)]
    [InlineData(2)]
    public void Read_RejectsInvalidOrMissingDeclaredReportInventory(int projects) => WithReports(root =>
        Assert.Throws<InvalidDataException>(() => InstalledConsumerProducer.ReadJUnitReportsForTests(root, projects)));

    [Theory]
    [InlineData(0L)]
    [InlineData(16L * 1024 * 1024 + 1)]
    public void Read_RejectsEmptyOrOversizedReportsBeforeParsing(long length) => WithReports(root =>
    {
        using (var stream = File.Create(Path.Join(root, "junit-one.xml"))) stream.SetLength(length);
        var exception = Assert.Throws<InvalidDataException>(() => InstalledConsumerProducer.ReadJUnitReportsForTests(root, 1));
        Assert.Contains("byte limit", exception.Message, StringComparison.Ordinal);
    });

    [Fact]
    public void Read_RejectsAggregateReportBytesEvenWhenEachReportFits() => WithReports(root =>
    {
        for (var index = 0; index < 5; index++)
        {
            using var writer = new StreamWriter(File.Create(Path.Join(root, $"junit-{index}.xml")));
            writer.Write("<testsuite tests='0' failures='0' errors='0' skipped='0'>");
            var padding = new string(' ', 1024 * 1024);
            for (var megabyte = 0; megabyte < 13; megabyte++) writer.Write(padding);
            writer.Write("</testsuite>");
        }
        var exception = Assert.Throws<InvalidDataException>(() => InstalledConsumerProducer.ReadJUnitReportsForTests(root, 5));
        Assert.Contains("aggregate byte limit", exception.Message, StringComparison.Ordinal);
    });

    [Fact]
    public void Read_RejectsExcessiveTestcaseInventory() => WithReports(root =>
    {
        using (var writer = new StreamWriter(File.Create(Path.Join(root, "junit-one.xml"))))
        {
            writer.Write("<testsuite tests='100001' failures='0' errors='0' skipped='0'>");
            for (var index = 0; index < 100001; index++) writer.Write("<testcase name='case' />");
            writer.Write("</testsuite>");
        }
        var exception = Assert.Throws<InvalidDataException>(() => InstalledConsumerProducer.ReadJUnitReportsForTests(root, 1));
        Assert.Contains("testcase limit", exception.Message, StringComparison.Ordinal);
    });

    [Fact]
    public void Read_RejectsNonJUnitRootsAndProhibitedDtds() => WithReports(root =>
    {
        var report = Path.Join(root, "junit-one.xml");
        File.WriteAllText(report, "<coverage />");
        Assert.Throws<InvalidDataException>(() => InstalledConsumerProducer.ReadJUnitReportsForTests(root, 1));
        File.WriteAllText(report, "<!DOCTYPE testsuite><testsuite tests='0' failures='0' errors='0' skipped='0' />");
        Assert.Throws<System.Xml.XmlException>(() => InstalledConsumerProducer.ReadJUnitReportsForTests(root, 1));
    });

    [Fact]
    public void Read_HonorsCancellationBeforeLoadingReportBytes() => WithReports(root =>
    {
        File.WriteAllText(Path.Join(root, "junit-one.xml"), "not parsed");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => InstalledConsumerProducer.ReadJUnitReportsForTests(root, 1, cancellation.Token));
    });

    private static void WithReports(Action<string> assertion)
    {
        var root = Directory.CreateTempSubdirectory("appsurface-native-junit-").FullName;
        try { assertion(root); }
        finally { Directory.Delete(root, recursive: true); }
    }
}
