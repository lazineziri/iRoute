using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using iRoute.Common;

namespace iRoute.Services;

public sealed class ProviderHttpTransport(HttpClient client, ModelGatewayDeploymentOptions route, TimeProvider clock,
    IChatGPTAccessTokenProvider? chatGPTTokens = null)
{
    private const int MaximumResponseBytes = 2 * 1024 * 1024;

    internal async Task<JsonElement> SendAsync(string path, JsonObject body, bool anthropic,
        ModelGatewayRequest request, CancellationToken cancellationToken)
    {
        var subscription = route.Adapter.Equals("OpenAIChatGPT", StringComparison.OrdinalIgnoreCase);
        if (subscription) SubscriptionGatewaySupport.EnsureRequest(request, route);
        else EnsureBudget(request);
        var key = subscription
            ? !string.IsNullOrWhiteSpace(route.ChatGPTAccessToken) ? route.ChatGPTAccessToken : Environment.GetEnvironmentVariable("IROUTE_CHATGPT_ACCESS_TOKEN")
            : !string.IsNullOrWhiteSpace(route.ApiKey) ? route.ApiKey : Environment.GetEnvironmentVariable(anthropic ? "ANTHROPIC_API_KEY" : "OPENAI_API_KEY");
        if (subscription && string.IsNullOrWhiteSpace(key) && chatGPTTokens is not null)
        {
            try { key = await chatGPTTokens.GetAsync(route.ChatGPTAccountId, cancellationToken); }
            catch (Exception error) when (error is ChatGPTAuthenticationException or HttpRequestException or IOException or
                OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                throw new ModelGatewayException(ErrorCodes.ModelGatewayUnavailable,
                    "The selected iRoute ChatGPT session is unavailable. Use 'iroute auth chatgpt status' or sign in again; API billing was not used.", false,
                    failureKind: ModelGatewayFailureKind.Authentication, failureClass: GatewayFailureClass.Permanent);
            }
        }
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(route.Model))
            throw new ModelGatewayException(ErrorCodes.ModelGatewayUnavailable,
                "The native provider requires its configured credential and a model identifier.", false,
                failureKind: ModelGatewayFailureKind.Authentication, gatewayId: route.GatewayId,
                failureClass: GatewayFailureClass.Permanent);
        var baseUri = new Uri(string.IsNullOrWhiteSpace(route.BaseUrl)
            ? anthropic ? "https://api.anthropic.com/v1/" : "https://api.openai.com/v1/"
            : route.BaseUrl);
        if ((baseUri.Scheme != Uri.UriSchemeHttps && !baseUri.IsLoopback) ||
            (subscription && !baseUri.IsLoopback && baseUri.AbsoluteUri.TrimEnd('/') != "https://api.openai.com/v1"))
            throw new ModelGatewayException(ErrorCodes.ModelGatewayUnavailable,
                "Native provider credentials require HTTPS; ChatGPT plan tokens require the official OpenAI API host.", false,
                failureClass: GatewayFailureClass.Permanent);
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/" + path));
        message.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        if (anthropic)
        {
            message.Headers.Add("x-api-key", key);
            message.Headers.Add("anthropic-version", "2023-06-01");
        }
        else
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            if (subscription) message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(request.DeadlineMilliseconds ?? 30_000), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            EnsureSuccess(response, request);
            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
                throw ProviderResponse.Invalid(request, "The provider response exceeded the safety limit.");
            await using var stream = await response.Content.ReadAsStreamAsync(linked.Token);
            if (subscription)
            {
                var mediaType = response.Content.Headers.ContentType?.MediaType;
                if (mediaType is not null && !mediaType.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase))
                    throw ProviderResponse.Invalid(request, "ChatGPT plan inference requires a Responses event stream.");
                return await OpenAIResponseStream.ReadAsync(stream, request, linked.Token);
            }
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(bytes, linked.Token)) > 0)
            {
                if (buffer.Length + count > MaximumResponseBytes)
                    throw ProviderResponse.Invalid(request, "The provider response exceeded the safety limit.");
                await buffer.WriteAsync(bytes.AsMemory(0, count), linked.Token);
            }
            using var document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)));
            return document.RootElement.Clone();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ModelGatewayException(ErrorCodes.ModelGatewayUnavailable, "The provider request timed out.", true,
                failureKind: ModelGatewayFailureKind.Timeout, gatewayId: route.GatewayId,
                correlationId: request.CorrelationId, failureClass: GatewayFailureClass.Timeout);
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            throw new ModelGatewayException(ErrorCodes.ModelGatewayUnavailable, "The provider could not be reached.", true,
                failureKind: ModelGatewayFailureKind.Unavailable, gatewayId: route.GatewayId,
                correlationId: request.CorrelationId, failureClass: GatewayFailureClass.Transport);
        }
        catch (JsonException)
        {
            throw ProviderResponse.Invalid(request, "The provider returned malformed JSON.");
        }
    }

    private void EnsureSuccess(HttpResponseMessage response, ModelGatewayRequest request)
    {
        if (response.IsSuccessStatusCode) return;
        var throttled = response.StatusCode == HttpStatusCode.TooManyRequests;
        var auth = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
        var retryable = throttled || response.StatusCode == HttpStatusCode.RequestTimeout || (int)response.StatusCode >= 500;
        var retryAfter = response.Headers.RetryAfter?.Delta ??
            (response.Headers.RetryAfter?.Date is { } date ? date - clock.GetUtcNow() : (TimeSpan?)null);
        throw new ModelGatewayException(ErrorCodes.ModelGatewayHttpError,
            $"The provider returned HTTP {(int)response.StatusCode}.", retryable, (int)response.StatusCode,
            failureKind: auth ? ModelGatewayFailureKind.Authentication : throttled ? ModelGatewayFailureKind.RateLimited : ModelGatewayFailureKind.Unavailable,
            correlationId: request.CorrelationId, retryAfter: retryAfter,
            failureClass: auth || !retryable ? GatewayFailureClass.Permanent : throttled ? GatewayFailureClass.Throttling : GatewayFailureClass.Provider);
    }

    private void EnsureBudget(ModelGatewayRequest request)
    {
        if (request.MaxOutputTokens <= 0 || request.DeadlineMilliseconds is <= 0)
            throw ProviderResponse.Invalid(request, "The provider request must have positive output and deadline limits.");
        if (request.MaximumCost is not { } limit) return;
        if (route.InputCostPerMillionTokens is not { } inputPrice || route.OutputCostPerMillionTokens is not { } outputPrice)
            throw new ModelGatewayException(ErrorCodes.CostBudgetExceeded,
                "Native API cost limits require configured input/output prices; unknown cost is not zero cost.", false,
                failureClass: GatewayFailureClass.Policy);
        var inputBound = Encoding.UTF8.GetByteCount(ProviderTaskFormat.Prompt(request)) +
            Encoding.UTF8.GetByteCount(ProviderTaskFormat.Instructions) + 1024;
        if ((inputBound * inputPrice + request.MaxOutputTokens * outputPrice) / 1_000_000m > limit)
            throw new ModelGatewayException(ErrorCodes.CostBudgetExceeded,
                "The conservative native request estimate exceeds the remaining cost budget.", false,
                failureClass: GatewayFailureClass.Policy);
    }
}
