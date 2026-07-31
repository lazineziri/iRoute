package dev.iroute;

import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.time.Duration;

public final class IRouteClient {
    private final HttpClient client = HttpClient.newHttpClient();
    private final URI baseUri;
    private final String token;

    public IRouteClient(URI baseUri, String token) {
        this.baseUri = baseUri;
        this.token = token;
    }

    public String executeJson(String requestJson) throws Exception {
        var builder = HttpRequest.newBuilder(baseUri.resolve("/v1/executions"))
            .timeout(Duration.ofSeconds(30))
            .header("Content-Type", "application/json")
            .POST(HttpRequest.BodyPublishers.ofString(requestJson));
        if (token != null && !token.isBlank()) builder.header("Authorization", "Bearer " + token);
        var response = client.send(builder.build(), HttpResponse.BodyHandlers.ofString());
        if (response.statusCode() < 200 || response.statusCode() >= 300) {
            throw new IllegalStateException("iRoute request failed with HTTP " + response.statusCode());
        }
        return response.body();
    }
}
