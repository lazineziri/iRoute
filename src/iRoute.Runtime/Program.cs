using iRoute.Runtime.Cli;
using iRoute.Runtime.Hosting;
using iRoute.Runtime.Migrations;

return await RuntimeCommand.RunAsync(args);

public partial class Program;

internal static class RuntimeCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            Console.WriteLine(HelpText);
            return 0;
        }

        var command = args[0].ToLowerInvariant();
        var commandArguments = args[1..];
        switch (command)
        {
            case "serve":
                await iRoute.Runtime.ApiHost.RunAsync(commandArguments);
                return 0;
            case "worker":
                await WorkerHost.RunAsync(commandArguments);
                return 0;
            case "migrate":
                return await MigrationCli.RunAsync(commandArguments);
            case "client":
                return await ClientCli.RunAsync(commandArguments);
            default:
                return await ClientCli.RunAsync(args);
        }
    }

    private const string HelpText = """
        iRoute .NET runtime

        Usage:
          iroute serve [ASP.NET options]       Run API and local background workers
          iroute worker                       Run background workers only
          iroute migrate <command>             Manage the database schema
          iroute client <command>              Call a running iRoute server
          iroute <client-command>              Client command shorthand

        Run 'iroute client help' or 'iroute migrate help' for command details.
        """;
}
