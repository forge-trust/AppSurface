using ForgeTrust.AppSurface.Web;
using WebAppExample;

await WebApp<ExampleModule>.RunAsync(
    args,
    options =>
    {
        options.Cors.EnableCors = true;
        options.Cors.AllowedOrigins = ["myothersite.com"];
        options.MapEndpoints = endpoints =>
        {
            endpoints.MapGet("/", () => "Hello World from the root!");
        };
    });
