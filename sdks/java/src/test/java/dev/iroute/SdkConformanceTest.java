package dev.iroute;

import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Duration;
import java.util.ArrayDeque;
import java.util.Base64;
import java.util.List;
import java.util.Properties;

public final class SdkConformanceTest {
    private static final Properties FIXTURE = loadFixture();

    public static void main(String[] args) throws Exception {
        requestFixture();
        streamFixture();
        errorFixture();
        System.out.println("PASS Java SDK conformance");
    }

    private static void requestFixture() throws Exception {
        var transport = new StubTransport(new IRouteClient.Response(
            integer("response.status"),
            decode("response.body.base64")));
        var client = client(transport);

        var response = client.executeJson(
            decode("request.body.base64"),
            FIXTURE.getProperty("request.header.idempotency-key"));

        var request = transport.lastRequest;
        require(request != null, "request was not captured");
        require(request.method().equals(FIXTURE.getProperty("request.method")), "request method");
        require(request.uri().getPath().equals(FIXTURE.getProperty("request.path")), "request path");
        require(request.headers().get("Authorization").equals(FIXTURE.getProperty("request.header.authorization")), "authorization");
        require(request.headers().get("Content-Type").equals(FIXTURE.getProperty("request.header.content-type")), "content type");
        require(request.headers().get("X-Tenant-Id").equals(FIXTURE.getProperty("request.header.tenant")), "tenant");
        require(request.headers().get("X-Actor-Id").equals(FIXTURE.getProperty("request.header.actor")), "actor");
        require(request.headers().get("X-Permission-Scopes").equals(FIXTURE.getProperty("request.header.permission-scopes")), "scopes");
        require(request.headers().get("Idempotency-Key").equals(FIXTURE.getProperty("request.header.idempotency-key")), "idempotency");
        require(request.body().equals(decode("request.body.base64")), "request body");
        require(response.equals(decode("response.body.base64")), "response body");
    }

    private static void streamFixture() throws Exception {
        var transport = new StubTransport(new IRouteClient.Response(
            integer("stream.status"),
            decode("stream.body.base64")));
        var events = client(transport).streamEventsJson("019fbc00-0000-7000-8000-000000000014", 0);

        require(events.size() == integer("stream.expected.count"), "stream count");
        var expectedTypes = FIXTURE.getProperty("stream.expected.types").split(",");
        for (var index = 0; index < expectedTypes.length; index++) {
            require(events.get(index).contains("\"type\":\"" + expectedTypes[index] + "\""), "stream type");
        }
    }

    private static void errorFixture() throws Exception {
        var transport = new StubTransport(new IRouteClient.Response(
            integer("error.status"),
            decode("error.body.base64")));
        try {
            client(transport).executeJson(decode("request.body.base64"));
            throw new AssertionError("expected IRouteApiException");
        } catch (IRouteClient.IRouteApiException error) {
            require(error.status() == integer("error.status"), "error status");
            require(error.code().equals(FIXTURE.getProperty("error.expected.code")), "error code");
            require(error.title().equals(FIXTURE.getProperty("error.expected.title")), "error title");
            require(error.detail().equals(FIXTURE.getProperty("error.expected.detail")), "error detail");
            require(error.responseBody().equals(decode("error.body.base64")), "error body");
        }
    }

    private static IRouteClient client(StubTransport transport) {
        return new IRouteClient(
            java.net.URI.create("http://sdk-fixture.test"),
            new IRouteClient.Options(
                "fixture-token",
                FIXTURE.getProperty("request.header.tenant"),
                FIXTURE.getProperty("request.header.actor"),
                List.of(FIXTURE.getProperty("request.header.permission-scopes").split(" ")),
                Duration.ofSeconds(30)),
            transport);
    }

    private static Properties loadFixture() {
        try {
            var properties = new Properties();
            try (var reader = Files.newBufferedReader(
                Path.of("..", "conformance", "v1.properties"),
                StandardCharsets.UTF_8)) {
                properties.load(reader);
            }
            return properties;
        } catch (Exception error) {
            throw new ExceptionInInitializerError(error);
        }
    }

    private static int integer(String key) {
        return Integer.parseInt(FIXTURE.getProperty(key));
    }

    private static String decode(String key) {
        return new String(Base64.getDecoder().decode(FIXTURE.getProperty(key)), StandardCharsets.UTF_8);
    }

    private static void require(boolean condition, String message) {
        if (!condition) throw new AssertionError(message);
    }

    private static final class StubTransport implements IRouteClient.Transport {
        private final ArrayDeque<IRouteClient.Response> responses = new ArrayDeque<>();
        private IRouteClient.Request lastRequest;

        StubTransport(IRouteClient.Response response) {
            responses.add(response);
        }

        @Override
        public IRouteClient.Response send(IRouteClient.Request request) {
            lastRequest = request;
            return responses.remove();
        }
    }
}
