using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Reports;

namespace AppSurfaceBenchmarks;

public class CustomJsonExporter : JsonExporter
{
    public CustomJsonExporter() : base("-full-compressed", indentJson: false, excludeMeasurements: false)
    {
    }

    protected override IReadOnlyDictionary<string, object> GetDataToSerialize(BenchmarkReport report)
    {
        var baseData = base.GetDataToSerialize(report);

        if (baseData.TryGetValue("FullName", out var fullNameObj) && fullNameObj is string fullName)
        {
            var job = report.BenchmarkCase.Job.ResolvedId;
            var updatedFullName = string.IsNullOrWhiteSpace(job) switch
            {
                true => CleanName(fullName),
                false => $"{CleanName(fullName)}[{job}]"
            };

            var modifiedData = new Dictionary<string, object>(baseData)
            {
                ["FullName"] = updatedFullName
            };

            return modifiedData;
        }

        return baseData;
    }

    private static string CleanName(string name)
    {
        return name.Replace("AppSurfaceBenchmarks.", "");
    }

}
