using iRoute.Runtime.Composition;

namespace iRoute.Runtime.Hosting;

internal static class WorkerHost
{
    public static async Task RunAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.Services.AddIRouteRuntime(builder.Configuration);
        builder.Services.AddIRoutePlatform(builder.Configuration);
        builder.Services.AddIRouteBackgroundWorkers(builder.Configuration);
        using var host = builder.Build();
        await host.RunAsync();
    }
}
