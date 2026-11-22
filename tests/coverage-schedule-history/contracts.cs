#!/usr/bin/env dotnet
#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property ImplicitUsings=enable
#:property Nullable=enable
#:property PublishAot=false
#:package SharpCompress
#:include ../../.github/scripts/CoverageHistoryCaseFoldData.cs
#:include ../../.github/scripts/CoverageScheduleHistory.cs
#:include ../../.github/scripts/CoverageHistoryData.cs
#:include ../../.github/scripts/CoverageHistoryGh.cs
#:include CoverageScheduleHistoryContracts.cs
#:include GhProcessContracts.cs

if (args is ["--gh-fixture", ..]) return await GhProcessContracts.RunFixtureAsync(args[1..]);

if (args.Length != 0)
{
    Console.Error.WriteLine("Usage: dotnet run --file tests/coverage-schedule-history/contracts.cs");
    return 2;
}

return await CoverageScheduleHistoryContracts.RunAsync();
