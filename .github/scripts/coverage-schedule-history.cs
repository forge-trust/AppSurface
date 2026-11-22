#!/usr/bin/env dotnet
// Run from the repository root with its pinned SDK.
// dotnet run --file .github/scripts/coverage-schedule-history.cs -- restore --output /absolute/path/timings.json
#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property ImplicitUsings=enable
#:property Nullable=enable
#:property PublishAot=false
#:package SharpCompress
#:include CoverageScheduleHistory.cs
#:include CoverageHistoryCaseFoldData.cs
#:include CoverageHistoryData.cs
#:include CoverageHistoryGh.cs

return await CoverageScheduleHistory.RunAsync(
    args,
    Environment.GetEnvironmentVariable,
    Console.Out,
    Console.Error,
    CoverageHistoryGh.RequestAsync,
    TimeProvider.System);
