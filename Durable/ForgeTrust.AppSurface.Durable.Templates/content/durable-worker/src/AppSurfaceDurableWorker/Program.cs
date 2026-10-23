using AppSurfaceDurableWorker;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

var builder = WorkerApplication.CreateBuilder(args);
if (!builder.Environment.IsDevelopment())
{
    ConfigureProductionAuthentication(builder);
}

try
{
    await using var app = await WorkerApplication.BuildAsync(builder);
    await WorkerApplication.StartAsync(app);
    await app.WaitForShutdownAsync();
    return 0;
}
catch (Exception exception) when (exception is not StackOverflowException
    and not OutOfMemoryException and not AccessViolationException)
{
    // Provider and application failures can use the same exception types as host validation;
    // a type allowlist cannot prove that their messages are safe to print.
    Console.Error.WriteLine(
        "The Durable worker could not start or stop safely. Check the configured authentication, restricted PostgreSQL "
        + "roles, StoreId, and runtime epoch; see the AppSurface Durable worker start guide.");
    return 1;
}

static void ConfigureProductionAuthentication(WebApplicationBuilder builder)
{
    ArgumentNullException.ThrowIfNull(builder);

    // Replace this marker with the identity-provider package and handler chosen by this application. Register its
    // actual scheme under the same name before BuildAsync; an unregistered name is rejected before database access.
    builder.Services.AddAuthentication("ReplaceWithApplicationScheme");
    builder.Services.AddAuthorization(options => options.AddPolicy(
        WorkerApplication.ActivationAuthorizationPolicy,
        policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.RequireClaim("permission", "durable-activation");
        }));
}
