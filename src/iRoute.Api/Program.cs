using iRoute.Api;
using iRoute.Contracts;
using iRoute.Core;
using iRoute.Infrastructure;
using iRoute.Runtime;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi("v1");
builder.Services.AddHealthChecks();
var identityOptions = builder.Services.AddIRouteIdentity(builder.Configuration);
builder.Services.AddIRouteRuntime(
    builder.Configuration.GetSection("Workflow").Get<WorkflowSchedulerOptions>());
builder.Services.AddIRouteInfrastructure(builder.Configuration);

var telemetry = builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("iRoute.Api"))
    .WithTracing(tracing => tracing
        .AddSource(RuntimeTelemetry.ActivitySourceName)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation())
    .WithMetrics(metrics => metrics
        .AddMeter(RuntimeTelemetry.MeterName)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation());

if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    telemetry.UseOtlpExporter();
}

var app = builder.Build();
app.UseExceptionHandler();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.MapOpenApi();
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready")
});
app.MapGet("/health/model-gateway", async (
    IModelGateway gateway,
    CancellationToken cancellationToken) =>
{
    var health = await gateway.CheckHealthAsync(cancellationToken);
    return Results.Json(
        health,
        statusCode: health.Status == ModelGatewayHealthStatus.Unavailable ? 503 : 200);
}).AllowAnonymous().WithName("getModelGatewayHealth");
app.MapIRouteEndpoints(identityOptions.UsesJwt);
app.MapIRouteObservabilityEndpoints(identityOptions.UsesJwt);
app.Run();

public partial class Program;
