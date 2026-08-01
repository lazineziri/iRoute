package dev.iroute;

import java.io.IOException;
import java.net.URI;
import java.net.URLEncoder;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.nio.charset.StandardCharsets;
import java.time.Duration;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Optional;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

public final class IRouteClient {
    private static final Pattern JSON_STRING = Pattern.compile(
        "\\\"%s\\\"\\s*:\\s*\\\"([^\\\"]*)\\\"");

    private final URI baseUri;
    private final Options options;
    private final Transport transport;

    public IRouteClient(URI baseUri, Options options) {
        this(baseUri, options, new JdkTransport(options.timeout()));
    }

    public IRouteClient(URI baseUri, Options options, Transport transport) {
        this.baseUri = baseUri;
        this.options = options;
        this.transport = transport;
    }

    public String executeJson(String requestJson, String idempotencyKey)
        throws IOException, InterruptedException {
        var headers = new LinkedHashMap<String, String>();
        headers.put("Content-Type", "application/json");
        if (idempotencyKey != null && !idempotencyKey.isBlank()) {
            headers.put("Idempotency-Key", idempotencyKey);
        }
        return requireSuccess(send("POST", "/v1/executions", requestJson, headers)).body();
    }

    public String executeJson(String requestJson) throws IOException, InterruptedException {
        return executeJson(requestJson, null);
    }

    public Optional<String> getJson(String executionId) throws IOException, InterruptedException {
        return optional(send("GET", "/v1/executions/" + encode(executionId), null, Map.of()));
    }

    public boolean cancel(String executionId) throws IOException, InterruptedException {
        var response = send("POST", "/v1/executions/" + encode(executionId) + "/cancel", null, Map.of());
        if (response.status() == 404) return false;
        requireSuccess(response);
        return true;
    }

    public String submitApprovalJson(String executionId, String decisionJson)
        throws IOException, InterruptedException {
        return requireSuccess(send(
            "POST",
            "/v1/executions/" + encode(executionId) + "/approvals",
            decisionJson,
            Map.of("Content-Type", "application/json"))).body();
    }

    public Optional<String> getArtifactJson(String artifactId) throws IOException, InterruptedException {
        return optional(send("GET", "/v1/artifacts/" + encode(artifactId), null, Map.of()));
    }

    public String getModelGatewayHealthJson() throws IOException, InterruptedException {
        var response = send("GET", "/health/model-gateway", null, Map.of());
        if (response.status() != 200 && response.status() != 503) requireSuccess(response);
        return response.body();
    }

    public String getObservabilitySummaryJson(Map<String, String> query)
        throws IOException, InterruptedException {
        var parameters = new ArrayList<String>();
        for (var entry : query.entrySet()) {
            if (entry.getValue() != null && !entry.getValue().isBlank()) {
                parameters.add(encode(entry.getKey()) + "=" + encode(entry.getValue()));
            }
        }
        var path = "/v1/observability/summary" +
            (parameters.isEmpty() ? "" : "?" + String.join("&", parameters));
        return requireSuccess(send("GET", path, null, Map.of())).body();
    }

    public Optional<String> getExecutionTimelineJson(String executionId)
        throws IOException, InterruptedException {
        return optional(send(
            "GET",
            "/v1/observability/executions/" + encode(executionId),
            null,
            Map.of()));
    }

    public List<String> streamEventsJson(String executionId, long afterSequence)
        throws IOException, InterruptedException {
        var response = requireSuccess(send(
            "GET",
            "/v1/executions/" + encode(executionId) + "/events?after=" + afterSequence,
            null,
            Map.of("Accept", "text/event-stream")));
        return parseSse(response.body());
    }

    static List<String> parseSse(String body) {
        var events = new ArrayList<String>();
        var data = new ArrayList<String>();
        for (var line : body.replace("\r\n", "\n").split("\n", -1)) {
            if (line.isEmpty()) {
                if (!data.isEmpty()) {
                    events.add(String.join("\n", data));
                    data.clear();
                }
            } else if (line.startsWith("data:")) {
                data.add(line.substring(5).stripLeading());
            }
        }
        if (!data.isEmpty()) events.add(String.join("\n", data));
        return List.copyOf(events);
    }

    private Response send(String method, String path, String body, Map<String, String> requestHeaders)
        throws IOException, InterruptedException {
        var headers = new LinkedHashMap<String, String>();
        if (options.token() != null && !options.token().isBlank()) {
            headers.put("Authorization", "Bearer " + options.token());
        }
        if (options.tenantId() != null && !options.tenantId().isBlank()) {
            headers.put("X-Tenant-Id", options.tenantId());
        }
        if (options.actorId() != null && !options.actorId().isBlank()) {
            headers.put("X-Actor-Id", options.actorId());
        }
        if (!options.permissionScopes().isEmpty()) {
            headers.put("X-Permission-Scopes", String.join(" ", options.permissionScopes()));
        }
        headers.putAll(requestHeaders);
        return transport.send(new Request(method, baseUri.resolve(path), Map.copyOf(headers), body));
    }

    private static Optional<String> optional(Response response) throws IRouteApiException {
        if (response.status() == 404) return Optional.empty();
        return Optional.of(requireSuccess(response).body());
    }

    private static Response requireSuccess(Response response) throws IRouteApiException {
        if (response.status() >= 200 && response.status() < 300) return response;
        throw new IRouteApiException(
            response.status(),
            jsonString(response.body(), "code"),
            jsonString(response.body(), "title"),
            jsonString(response.body(), "detail"),
            response.body());
    }

    private static String jsonString(String json, String name) {
        Matcher matcher = Pattern.compile(JSON_STRING.pattern().formatted(Pattern.quote(name))).matcher(json);
        return matcher.find() ? matcher.group(1) : null;
    }

    private static String encode(String value) {
        return URLEncoder.encode(value, StandardCharsets.UTF_8).replace("+", "%20");
    }

    public record Options(
        String token,
        String tenantId,
        String actorId,
        List<String> permissionScopes,
        Duration timeout) {
        public Options {
            permissionScopes = permissionScopes == null ? List.of() : List.copyOf(permissionScopes);
            timeout = timeout == null ? Duration.ofSeconds(30) : timeout;
        }

        public static Options defaults() {
            return new Options(null, null, null, List.of(), Duration.ofSeconds(30));
        }
    }

    public record Request(String method, URI uri, Map<String, String> headers, String body) {}

    public record Response(int status, String body) {}

    @FunctionalInterface
    public interface Transport {
        Response send(Request request) throws IOException, InterruptedException;
    }

    public static final class IRouteApiException extends IOException {
        private static final long serialVersionUID = 1L;

        private final int status;
        private final String code;
        private final String title;
        private final String detail;
        private final String responseBody;

        IRouteApiException(int status, String code, String title, String detail, String responseBody) {
            super(detail != null ? detail : title != null ? title : "iRoute request failed with HTTP " + status);
            this.status = status;
            this.code = code;
            this.title = title;
            this.detail = detail;
            this.responseBody = responseBody;
        }

        public int status() { return status; }
        public String code() { return code; }
        public String title() { return title; }
        public String detail() { return detail; }
        public String responseBody() { return responseBody; }
    }

    private static final class JdkTransport implements Transport {
        private final HttpClient client;
        private final Duration timeout;

        JdkTransport(Duration timeout) {
            this.client = HttpClient.newHttpClient();
            this.timeout = timeout;
        }

        @Override
        public Response send(Request request) throws IOException, InterruptedException {
            var builder = HttpRequest.newBuilder(request.uri()).timeout(timeout);
            request.headers().forEach(builder::header);
            var publisher = request.body() == null
                ? HttpRequest.BodyPublishers.noBody()
                : HttpRequest.BodyPublishers.ofString(request.body());
            builder.method(request.method(), publisher);
            var response = client.send(builder.build(), HttpResponse.BodyHandlers.ofString());
            return new Response(response.statusCode(), response.body());
        }
    }
}
