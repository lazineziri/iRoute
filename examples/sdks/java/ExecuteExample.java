import dev.iroute.IRouteClient;
import java.net.URI;
import java.time.Duration;
import java.util.List;
import java.util.UUID;

public final class ExecuteExample {
    public static void main(String[] args) throws Exception {
        var environment = System.getenv();
        var client = new IRouteClient(
            URI.create(environment.getOrDefault("IROUTE_URL", "http://localhost:8080")),
            new IRouteClient.Options(
                environment.get("IROUTE_TOKEN"),
                environment.getOrDefault("IROUTE_TENANT", "demo"),
                environment.getOrDefault("IROUTE_ACTOR", "sdk-example"),
                List.of(),
                Duration.ofSeconds(30)));
        var request = """
            {"taskType":"email.draft","input":{"purpose":"Confirm the SDK quick start."},"idempotencyKey":"%s"}
            """.formatted("java-example-" + UUID.randomUUID());
        System.out.println(client.executeJson(request));
    }
}
