namespace iRoute.Tests.Benchmarks;

internal sealed class BenchmarkHttpHandler : DelegatingHandler
{
    public BenchmarkHttpHandler() => InnerHandler = new HttpClientHandler { AllowAutoRedirect = false };

    public int ModelCalls { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/responses")
        {
            if (++ModelCalls > 20) throw new InvalidOperationException("The per-model benchmark request ceiling was exceeded.");
        }
        return base.SendAsync(request, cancellationToken);
    }
}
