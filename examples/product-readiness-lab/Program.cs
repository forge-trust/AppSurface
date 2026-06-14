using ProductReadinessLab;

if (ProductReadinessLabCli.IsReportCommand(args))
{
    return await ProductReadinessLabCli.RunReportAsync(args);
}

await ProductReadinessLabApp.RunAsync(args);

return 0;
