using iRoute.Infrastructure;
using iRoute.Runtime;
using iRoute.Worker;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddIRouteRuntime(
    builder.Configuration.GetSection("Workflow").Get<WorkflowSchedulerOptions>());
builder.Services.AddIRouteInfrastructure(builder.Configuration);
builder.Services.Configure<ExecutionWorkerOptions>(builder.Configuration.GetSection("ExecutionWorker"));
if (builder.Configuration.GetValue("ExecutionWorker:Enabled", true))
{
    builder.Services.AddHostedService<ExecutionWorker>();
}

if (builder.Configuration.GetValue("Lifecycle:Enabled", true))
{
    builder.Services.AddHostedService<LifecycleWorker>();
}
await builder.Build().RunAsync();
