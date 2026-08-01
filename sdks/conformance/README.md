# SDK conformance corpus

`v1.properties` is the language-neutral source for the official SDK request, SSE stream, and RFC 9457 error fixtures. Payloads use Base64 so every supported standard library can read the same bytes without language-specific escaping rules.

Every SDK conformance runner must prove:

- the execution request method, path, authentication, tenant/actor/scope, idempotency, content type, and JSON body match the fixture;
- both ordered SSE `data` records are emitted, including a final frame terminated by end-of-stream rather than an extra blank line;
- the error fixture becomes the SDK's typed API exception with the same HTTP status, code, title, detail, and response body;
- the client exposes protocol operations only and contains no task decomposition, model selection, prompt, memory, quality-scoring, or routing policy.

Run all locally available conformance runners with `npm run test:sdks`. CI executes the same corpus in the native .NET, Node.js, Python, Java, PHP, and Rust toolchains.
