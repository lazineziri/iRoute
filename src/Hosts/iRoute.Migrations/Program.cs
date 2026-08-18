using System.Text.Json;
using iRoute.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace iRoute.Migrations;

public static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            Console.WriteLine(HelpText);
            return 0;
        }

        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Configuration
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                .AddEnvironmentVariables();
            // AddIRouteInfrastructure validates Storage:Provider and reports what to use instead.
            builder.Services.AddIRouteInfrastructure(builder.Configuration);
            using var host = builder.Build();
            var manager = host.Services.GetRequiredService<SchemaMigrationManager>();
            var status = args[0] switch
            {
                "status" when args.Length == 1 => await manager.GetStatusAsync(),
                "up" when args.Length <= 2 => await manager.UpgradeAsync(
                    args.Length == 2 ? args[1] : null),
                "down" when args.Length is 2 or 3 => await manager.RollbackAsync(
                    args[1],
                    args.Length == 3 && string.Equals(args[2], "--confirm", StringComparison.Ordinal)),
                _ => throw new ArgumentException("Invalid migration command or arguments.")
            };
            Console.WriteLine(JsonSerializer.Serialize(status, JsonOptions));
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Migration failed: {exception.Message}");
            Console.Error.WriteLine("Run 'iRoute.Migrations help' for usage.");
            return 2;
        }
    }

    private const string HelpText = """
        iRoute schema migration runner

        Usage:
          iRoute.Migrations status
          iRoute.Migrations up [target-migration]
          iRoute.Migrations down <target-migration> --confirm

        Configuration uses standard .NET environment variables:
          Storage__Provider=Sqlite|Postgres
          ConnectionStrings__iRoute=<connection-string>

        'up' applies the latest migration by default. 'down' is intentionally
        explicit because reverting a schema can destroy data. Prefer rolling
        the application back while leaving an additive schema in place.
        """;
}
