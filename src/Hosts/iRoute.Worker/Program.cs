using iRoute.Infrastructure;
using iRoute.Runtime;
using iRoute.Worker;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddOptions<WorkflowSchedulerOptions>()
    .BindConfiguration("Workflow")
    .Validate(
        options => options.RetryMaxDelayMilliseconds >= options.RetryBaseDelayMilliseconds,
        "Workflow:RetryMaxDelayMilliseconds must be greater than or equal to RetryBaseDelayMilliseconds.")
    .ValidateOnStart();
builder.Services.AddIRouteRuntime();
builder.Services.AddIRouteInfrastructure(builder.Configuration);
builder.Services.AddSingleton<IValidateOptions<ExecutionWorkerOptions>, ExecutionWorkerOptionsValidator>();
builder.Services.AddOptions<ExecutionWorkerOptions>()
    .BindConfiguration("ExecutionWorker")
    .ValidateOnStart();
if (builder.Configuration.GetValue("ExecutionWorker:Enabled", true))
{
    builder.Services.AddHostedService<ExecutionWorker>();
}

if (builder.Configuration.GetValue("Lifecycle:Enabled", true))
{
    builder.Services.AddHostedService<LifecycleWorker>();
}
await builder.Build().RunAsync();
