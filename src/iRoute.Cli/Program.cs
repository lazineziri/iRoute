using System.Text.Json;
using iRoute.Contracts;
using iRoute.Sdk.DotNet;

return await Cli.RunAsync(args);

internal static class Cli
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            Console.WriteLine(HelpText);
            return 0;
        }

        try
        {
            var arguments = new Arguments(args);
            using var httpClient = new HttpClient
            {
                BaseAddress = new Uri(arguments.Option("--base-url")
                    ?? Environment.GetEnvironmentVariable("IROUTE_URL")
                    ?? "http://localhost:8080"),
                Timeout = TimeSpan.FromMinutes(5)
            };
            var client = new IRouteClient(httpClient, new IRouteClientOptions(
                TenantId: arguments.Option("--tenant")
                    ?? Environment.GetEnvironmentVariable("IROUTE_TENANT")
                    ?? "local",
                ActorId: arguments.Option("--actor")
                    ?? Environment.GetEnvironmentVariable("IROUTE_ACTOR")
                    ?? "cli",
                PermissionScopes: arguments.Options("--scope"),
                BearerToken: arguments.Option("--token")
                    ?? Environment.GetEnvironmentVariable("IROUTE_TOKEN")));

            return await ExecuteCommandAsync(arguments, client);
        }
        catch (IRouteApiException error)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                status = (int?)error.StatusCode,
                error.Code,
                error.Title,
                error.Detail
            }, JsonOptions));
            return 1;
        }
        catch (JsonException error)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                code = "invalid_response",
                title = "iRoute returned invalid JSON",
                detail = error.Message
            }, JsonOptions));
            return 1;
        }
        catch (Exception error) when (error is ArgumentException or FormatException)
        {
            Console.Error.WriteLine($"error: {error.Message}");
            Console.Error.WriteLine("Run 'iroute help' for usage.");
            return 2;
        }
    }

    private static async Task<int> ExecuteCommandAsync(Arguments arguments, IRouteClient client)
    {
        switch (arguments.Command)
        {
            case "execute":
            {
                var requestPath = arguments.Option("--request");
                TaskRequest request;
                if (requestPath is not null)
                {
                    request = ParseRequest(ReadValue(requestPath));
                    if (arguments.Option("--idempotency-key") is { } idempotencyKey)
                    {
                        request = request with { IdempotencyKey = idempotencyKey };
                    }
                }
                else
                {
                    var taskType = arguments.Positional(0, "execute requires a task type or --request.");
                    using var inputDocument = ParseInput(ReadValue(arguments.RequireOption("--input")));
                    var input = inputDocument.RootElement.Clone();
                    request = new TaskRequest(
                        taskType,
                        input,
                        ProjectId: arguments.Option("--project"),
                        IdempotencyKey: arguments.Option("--idempotency-key"));
                }
                Write(await client.ExecuteAsync(request));
                return 0;
            }
            case "get":
                Write(await client.GetAsync(ParseId(arguments.Positional(0, "get requires an execution ID."))));
                return 0;
            case "cancel":
                Write(new { cancelled = await client.CancelAsync(ParseId(arguments.Positional(0, "cancel requires an execution ID."))) });
                return 0;
            case "events":
                await foreach (var item in client.StreamEventsAsync(
                                   ParseId(arguments.Positional(0, "events requires an execution ID.")),
                                   arguments.LongOption("--after", 0)))
                {
                    Write(item);
                }
                return 0;
            case "approve":
                Write(await client.SubmitApprovalAsync(
                    ParseId(arguments.Positional(0, "approve requires an execution ID.")),
                    new ApprovalDecision(
                        arguments.Positional(1, "approve requires an action ID."),
                        true,
                        arguments.Option("--reason"))));
                return 0;
            case "deny":
                Write(await client.SubmitApprovalAsync(
                    ParseId(arguments.Positional(0, "deny requires an execution ID.")),
                    new ApprovalDecision(
                        arguments.Positional(1, "deny requires an action ID."),
                        false,
                        arguments.Option("--reason"))));
                return 0;
            case "artifact":
                Write(await client.GetArtifactAsync(ParseId(arguments.Positional(0, "artifact requires an artifact ID."))));
                return 0;
            case "observe":
                Write(await client.GetObservabilitySummaryAsync(new ObservabilityQueryOptions(
                    From: arguments.DateOption("--from"),
                    To: arguments.DateOption("--to"),
                    TaskType: arguments.Option("--task-type"),
                    PolicyVersion: arguments.Option("--policy-version"))));
                return 0;
            case "timeline":
                Write(await client.GetExecutionTimelineAsync(ParseId(
                    arguments.Positional(0, "timeline requires an execution ID."))));
                return 0;
            case "health":
                Write(await client.GetModelGatewayHealthAsync());
                return 0;
            default:
                throw new ArgumentException($"Unknown command '{arguments.Command}'.");
        }
    }

    private static Guid ParseId(string value) => Guid.TryParse(value, out var result)
        ? result
        : throw new FormatException($"'{value}' is not a valid ID.");

    private static string ReadValue(string value) => value.StartsWith('@')
        ? File.ReadAllText(value[1..])
        : value;

    private static TaskRequest ParseRequest(string value)
    {
        try
        {
            return JsonSerializer.Deserialize<TaskRequest>(value, JsonOptions)
                ?? throw new ArgumentException("--request must contain a task request object.");
        }
        catch (JsonException error)
        {
            throw new ArgumentException("--request must contain valid task request JSON.", error);
        }
    }

    private static JsonDocument ParseInput(string value)
    {
        try
        {
            return JsonDocument.Parse(value);
        }
        catch (JsonException error)
        {
            throw new ArgumentException("--input must contain valid JSON.", error);
        }
    }

    private static void Write<T>(T value) =>
        Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));

    private const string HelpText = """
        iRoute CLI — thin client for the iRoute runtime

        Usage:
          iroute execute <task-type> --input <json|@file> [--project <id>] [--idempotency-key <key>]
          iroute execute --request <json|@file> [--idempotency-key <key>]
          iroute get <execution-id>
          iroute events <execution-id> [--after <sequence>]
          iroute cancel <execution-id>
          iroute approve <execution-id> <action-id> [--reason <text>]
          iroute deny <execution-id> <action-id> [--reason <text>]
          iroute artifact <artifact-id>
          iroute observe [--from <timestamp>] [--to <timestamp>] [--task-type <type>]
          iroute timeline <execution-id>
          iroute health

        Connection options (or environment variables):
          --base-url <url>   IROUTE_URL       default http://localhost:8080
          --token <token>    IROUTE_TOKEN
          --tenant <id>      IROUTE_TENANT    default local
          --actor <id>       IROUTE_ACTOR     default cli
          --scope <scope>                     repeat for each permission
        """;

    private sealed class Arguments
    {
        private readonly string[] _values;
        private readonly List<string> _positionals;

        public Arguments(string[] values)
        {
            _values = values;
            Command = values[0];
            var positionals = new List<string>();
            for (var index = 1; index < values.Length; index++)
            {
                if (values[index].StartsWith("--", StringComparison.Ordinal))
                {
                    index++;
                    if (index >= values.Length)
                    {
                        throw new ArgumentException($"{values[index - 1]} requires a value.");
                    }
                }
                else
                {
                    positionals.Add(values[index]);
                }
            }
            _positionals = positionals;
        }

        public string Command { get; }

        public string? Option(string name)
        {
            var values = Options(name);
            return values.Count == 0 ? null : values[^1];
        }

        public List<string> Options(string name)
        {
            var values = new List<string>();
            for (var index = 1; index < _values.Length - 1; index++)
            {
                if (string.Equals(_values[index], name, StringComparison.Ordinal))
                {
                    values.Add(_values[index + 1]);
                }
            }
            return values;
        }

        public string RequireOption(string name) => Option(name)
            ?? throw new ArgumentException($"{name} is required.");

        public string Positional(int index, string message) => index < _positionals.Count
            ? _positionals[index]
            : throw new ArgumentException(message);

        public long LongOption(string name, long defaultValue) => Option(name) is { } value
            ? long.Parse(value, System.Globalization.CultureInfo.InvariantCulture)
            : defaultValue;

        public DateTimeOffset? DateOption(string name) => Option(name) is { } value
            ? DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture)
            : null;
    }
}
