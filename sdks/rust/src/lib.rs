//! Thin, protocol-only client for the iRoute runtime.
//!
//! The default transport supports the local HTTP quick start without additional
//! crates. Applications that require HTTPS can inject their preferred transport.

use std::collections::BTreeMap;
use std::fmt::{Display, Formatter};
use std::io::{Read, Write};
use std::net::TcpStream;
use std::time::Duration;

pub const API_VERSION: &str = "v1";

#[derive(Clone, Debug, Default)]
pub struct ClientOptions {
    pub token: Option<String>,
    pub tenant_id: Option<String>,
    pub actor_id: Option<String>,
    pub permission_scopes: Vec<String>,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct IRouteRequest {
    pub method: String,
    pub url: String,
    pub headers: BTreeMap<String, String>,
    pub body: Option<String>,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct IRouteResponse {
    pub status: u16,
    pub body: String,
}

pub trait Transport {
    fn send(&self, request: &IRouteRequest) -> Result<IRouteResponse, String>;
}

impl<F> Transport for F
where
    F: Fn(&IRouteRequest) -> Result<IRouteResponse, String>,
{
    fn send(&self, request: &IRouteRequest) -> Result<IRouteResponse, String> {
        self(request)
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct IRouteApiError {
    pub status: u16,
    pub code: Option<String>,
    pub title: Option<String>,
    pub detail: Option<String>,
    pub response_body: String,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum IRouteError {
    Transport(String),
    Api(IRouteApiError),
}

impl Display for IRouteError {
    fn fmt(&self, formatter: &mut Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Transport(message) => write!(formatter, "iRoute transport failed: {message}"),
            Self::Api(error) => write!(
                formatter,
                "{}",
                error
                    .detail
                    .as_deref()
                    .or(error.title.as_deref())
                    .unwrap_or("iRoute API request failed")
            ),
        }
    }
}

impl std::error::Error for IRouteError {}

pub struct IRouteClient<T: Transport> {
    base_url: String,
    options: ClientOptions,
    transport: T,
}

impl<T: Transport> IRouteClient<T> {
    pub fn with_transport(base_url: impl Into<String>, options: ClientOptions, transport: T) -> Self {
        Self {
            base_url: base_url.into().trim_end_matches('/').to_owned(),
            options,
            transport,
        }
    }

    pub fn execute_json(
        &self,
        request_json: &str,
        idempotency_key: Option<&str>,
    ) -> Result<String, IRouteError> {
        let mut headers = BTreeMap::from([("Content-Type".into(), "application/json".into())]);
        if let Some(value) = idempotency_key.filter(|value| !value.is_empty()) {
            headers.insert("Idempotency-Key".into(), value.into());
        }
        Ok(self
            .require_success(self.send("POST", "/v1/executions", Some(request_json), headers)?)?
            .body)
    }

    pub fn get_json(&self, execution_id: &str) -> Result<Option<String>, IRouteError> {
        self.optional(self.send(
            "GET",
            &format!("/v1/executions/{}", percent_encode(execution_id)),
            None,
            BTreeMap::new(),
        )?)
    }

    pub fn cancel(&self, execution_id: &str) -> Result<bool, IRouteError> {
        let response = self.send(
            "POST",
            &format!("/v1/executions/{}/cancel", percent_encode(execution_id)),
            None,
            BTreeMap::new(),
        )?;
        if response.status == 404 {
            return Ok(false);
        }
        self.require_success(response)?;
        Ok(true)
    }

    pub fn submit_approval_json(
        &self,
        execution_id: &str,
        decision_json: &str,
    ) -> Result<String, IRouteError> {
        Ok(self
            .require_success(self.send(
                "POST",
                &format!(
                    "/v1/executions/{}/approvals",
                    percent_encode(execution_id)
                ),
                Some(decision_json),
                BTreeMap::from([("Content-Type".into(), "application/json".into())]),
            )?)?
            .body)
    }

    pub fn get_artifact_json(&self, artifact_id: &str) -> Result<Option<String>, IRouteError> {
        self.optional(self.send(
            "GET",
            &format!("/v1/artifacts/{}", percent_encode(artifact_id)),
            None,
            BTreeMap::new(),
        )?)
    }

    pub fn get_model_gateway_health_json(&self) -> Result<String, IRouteError> {
        let response = self.send("GET", "/health/model-gateway", None, BTreeMap::new())?;
        if response.status == 503 {
            return Ok(response.body);
        }
        Ok(self.require_success(response)?.body)
    }

    pub fn get_observability_summary_json(
        &self,
        query: &BTreeMap<String, String>,
    ) -> Result<String, IRouteError> {
        let parameters = query
            .iter()
            .filter(|(_, value)| !value.is_empty())
            .map(|(key, value)| format!("{}={}", percent_encode(key), percent_encode(value)))
            .collect::<Vec<_>>()
            .join("&");
        let path = if parameters.is_empty() {
            "/v1/observability/summary".into()
        } else {
            format!("/v1/observability/summary?{parameters}")
        };
        Ok(self
            .require_success(self.send("GET", &path, None, BTreeMap::new())?)?
            .body)
    }

    pub fn get_execution_timeline_json(
        &self,
        execution_id: &str,
    ) -> Result<Option<String>, IRouteError> {
        self.optional(self.send(
            "GET",
            &format!(
                "/v1/observability/executions/{}",
                percent_encode(execution_id)
            ),
            None,
            BTreeMap::new(),
        )?)
    }

    pub fn stream_events_json(
        &self,
        execution_id: &str,
        after_sequence: u64,
    ) -> Result<Vec<String>, IRouteError> {
        let response = self.require_success(self.send(
            "GET",
            &format!(
                "/v1/executions/{}/events?after={after_sequence}",
                percent_encode(execution_id)
            ),
            None,
            BTreeMap::from([("Accept".into(), "text/event-stream".into())]),
        )?)?;
        Ok(parse_sse(&response.body))
    }

    fn send(
        &self,
        method: &str,
        path: &str,
        body: Option<&str>,
        mut headers: BTreeMap<String, String>,
    ) -> Result<IRouteResponse, IRouteError> {
        add_header(
            &mut headers,
            "Authorization",
            self.options
                .token
                .as_deref()
                .map(|value| format!("Bearer {value}")),
        );
        add_header(&mut headers, "X-Tenant-Id", self.options.tenant_id.clone());
        add_header(&mut headers, "X-Actor-Id", self.options.actor_id.clone());
        if !self.options.permission_scopes.is_empty() {
            headers.insert(
                "X-Permission-Scopes".into(),
                self.options.permission_scopes.join(" "),
            );
        }
        self.transport
            .send(&IRouteRequest {
                method: method.into(),
                url: format!("{}{path}", self.base_url),
                headers,
                body: body.map(str::to_owned),
            })
            .map_err(IRouteError::Transport)
    }

    fn optional(&self, response: IRouteResponse) -> Result<Option<String>, IRouteError> {
        if response.status == 404 {
            return Ok(None);
        }
        Ok(Some(self.require_success(response)?.body))
    }

    fn require_success(&self, response: IRouteResponse) -> Result<IRouteResponse, IRouteError> {
        if (200..300).contains(&response.status) {
            return Ok(response);
        }
        Err(IRouteError::Api(IRouteApiError {
            status: response.status,
            code: json_string(&response.body, "code"),
            title: json_string(&response.body, "title"),
            detail: json_string(&response.body, "detail"),
            response_body: response.body,
        }))
    }
}

#[derive(Clone, Debug)]
pub struct HttpTransport {
    timeout: Duration,
}

impl HttpTransport {
    pub fn new(timeout: Duration) -> Self {
        Self { timeout }
    }
}

impl Default for HttpTransport {
    fn default() -> Self {
        Self::new(Duration::from_secs(30))
    }
}

impl Transport for HttpTransport {
    fn send(&self, request: &IRouteRequest) -> Result<IRouteResponse, String> {
        let remainder = request
            .url
            .strip_prefix("http://")
            .ok_or_else(|| {
                "default transport supports http:// URLs; inject a TLS transport for HTTPS"
                    .to_owned()
            })?;
        let (authority, path) = remainder.split_once('/').unwrap_or((remainder, ""));
        let mut stream = TcpStream::connect(authority).map_err(|error| error.to_string())?;
        stream
            .set_read_timeout(Some(self.timeout))
            .map_err(|error| error.to_string())?;
        stream
            .set_write_timeout(Some(self.timeout))
            .map_err(|error| error.to_string())?;
        let body = request.body.as_deref().unwrap_or("");
        let mut head = format!(
            "{} /{} HTTP/1.1\r\nHost: {}\r\nConnection: close\r\nContent-Length: {}\r\n",
            request.method,
            path,
            authority,
            body.len()
        );
        for (name, value) in &request.headers {
            head.push_str(&format!("{name}: {value}\r\n"));
        }
        head.push_str("\r\n");
        stream.write_all(head.as_bytes()).map_err(|error| error.to_string())?;
        stream.write_all(body.as_bytes()).map_err(|error| error.to_string())?;
        let mut bytes = Vec::new();
        stream
            .read_to_end(&mut bytes)
            .map_err(|error| error.to_string())?;
        let header_end = bytes
            .windows(4)
            .position(|window| window == b"\r\n\r\n")
            .ok_or_else(|| "invalid HTTP response".to_owned())?;
        let response_head = std::str::from_utf8(&bytes[..header_end])
            .map_err(|error| error.to_string())?;
        let response_body = &bytes[header_end + 4..];
        let status = response_head
            .lines()
            .next()
            .and_then(|line| line.split_whitespace().nth(1))
            .and_then(|value| value.parse::<u16>().ok())
            .ok_or_else(|| "invalid HTTP status".to_owned())?;
        let body_bytes = if response_head
            .lines()
            .any(|line| line.eq_ignore_ascii_case("transfer-encoding: chunked"))
        {
            decode_chunked(response_body)?
        } else {
            response_body.to_vec()
        };
        let body = String::from_utf8(body_bytes).map_err(|error| error.to_string())?;
        Ok(IRouteResponse { status, body })
    }
}

impl IRouteClient<HttpTransport> {
    pub fn new(base_url: impl Into<String>, options: ClientOptions) -> Self {
        Self::with_transport(base_url, options, HttpTransport::default())
    }
}

pub fn parse_sse(body: &str) -> Vec<String> {
    let normalized = body.replace("\r\n", "\n");
    let mut events = Vec::new();
    let mut data = Vec::new();
    for line in normalized.split('\n') {
        if line.is_empty() {
            if !data.is_empty() {
                events.push(data.join("\n"));
                data.clear();
            }
        } else if let Some(value) = line.strip_prefix("data:") {
            data.push(value.strip_prefix(' ').unwrap_or(value));
        }
    }
    if !data.is_empty() {
        events.push(data.join("\n"));
    }
    events
}

fn add_header(headers: &mut BTreeMap<String, String>, name: &str, value: Option<String>) {
    if let Some(value) = value.filter(|value| !value.is_empty()) {
        headers.insert(name.into(), value);
    }
}

fn percent_encode(value: &str) -> String {
    value
        .bytes()
        .map(|byte| match byte {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'_' | b'.' | b'~' => {
                (byte as char).to_string()
            }
            _ => format!("%{byte:02X}"),
        })
        .collect()
}

fn json_string(json: &str, name: &str) -> Option<String> {
    let marker = format!("\"{name}\"");
    let value = json.split_once(&marker)?.1.split_once(':')?.1.trim_start();
    let quoted = value.strip_prefix('"')?;
    let mut output = String::new();
    let mut escaped = false;
    for character in quoted.chars() {
        if escaped {
            output.push(character);
            escaped = false;
        } else if character == '\\' {
            escaped = true;
        } else if character == '"' {
            return Some(output);
        } else {
            output.push(character);
        }
    }
    None
}

fn decode_chunked(body: &[u8]) -> Result<Vec<u8>, String> {
    let mut offset = 0;
    let mut output = Vec::new();
    loop {
        let line_end = body[offset..]
            .windows(2)
            .position(|window| window == b"\r\n")
            .ok_or_else(|| "invalid chunked response".to_owned())?;
        let size_line = std::str::from_utf8(&body[offset..offset + line_end])
            .map_err(|error| error.to_string())?;
        let size = usize::from_str_radix(size_line.split(';').next().unwrap_or(""), 16)
            .map_err(|error| error.to_string())?;
        offset += line_end + 2;
        if size == 0 {
            return Ok(output);
        }
        if body.len() < offset + size + 2 || &body[offset + size..offset + size + 2] != b"\r\n" {
            return Err("truncated chunked response".into());
        }
        output.extend_from_slice(&body[offset..offset + size]);
        offset += size + 2;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;
    use std::sync::{Arc, Mutex};

    #[test]
    fn matches_shared_request_fixture() {
        let fixture = fixture();
        let captured = Arc::new(Mutex::new(None));
        let capture = Arc::clone(&captured);
        let response = response(&fixture, "response");
        let client = client(&fixture, move |request: &IRouteRequest| {
            *capture.lock().expect("capture lock") = Some(request.clone());
            Ok(response.clone())
        });

        let result = client
            .execute_json(
                &decode(&fixture, "request.body.base64"),
                Some(&fixture["request.header.idempotency-key"]),
            )
            .expect("execution response");
        let request = captured
            .lock()
            .expect("capture lock")
            .clone()
            .expect("captured request");
        assert_eq!(request.method, fixture["request.method"]);
        assert_eq!(
            request.url,
            format!("http://sdk-fixture.test{}", fixture["request.path"])
        );
        assert_eq!(
            request.headers["Authorization"],
            fixture["request.header.authorization"]
        );
        assert_eq!(request.headers["Content-Type"], fixture["request.header.content-type"]);
        assert_eq!(
            request.headers["X-Tenant-Id"],
            fixture["request.header.tenant"]
        );
        assert_eq!(request.headers["X-Actor-Id"], fixture["request.header.actor"]);
        assert_eq!(
            request.headers["X-Permission-Scopes"],
            fixture["request.header.permission-scopes"]
        );
        assert_eq!(
            request.headers["Idempotency-Key"],
            fixture["request.header.idempotency-key"]
        );
        let expected_body = decode(&fixture, "request.body.base64");
        assert_eq!(request.body.as_deref(), Some(expected_body.as_str()));
        assert!(result.contains("019fbc00-0000-7000-8000-000000000014"));
    }

    #[test]
    fn matches_shared_end_of_stream_fixture() {
        let fixture = fixture();
        let response = response(&fixture, "stream");
        let client = client(&fixture, move |_request: &IRouteRequest| Ok(response.clone()));

        let events = client
            .stream_events_json("019fbc00-0000-7000-8000-000000000014", 0)
            .expect("stream events");
        assert_eq!(
            events.len(),
            fixture["stream.expected.count"].parse().unwrap()
        );
        let types = events
            .iter()
            .map(|event| json_string(event, "type").unwrap())
            .collect::<Vec<_>>()
            .join(",");
        assert_eq!(types, fixture["stream.expected.types"]);
    }

    #[test]
    fn matches_shared_typed_error_fixture() {
        let fixture = fixture();
        let response = response(&fixture, "error");
        let client = client(&fixture, move |_request: &IRouteRequest| Ok(response.clone()));

        let error = client.execute_json("{}", None).expect_err("typed error");
        let IRouteError::Api(error) = error else {
            panic!("expected API error");
        };
        assert_eq!(error.status.to_string(), fixture["error.status"]);
        assert_eq!(
            error.code.as_deref(),
            Some(fixture["error.expected.code"].as_str())
        );
        assert_eq!(
            error.title.as_deref(),
            Some(fixture["error.expected.title"].as_str())
        );
        assert_eq!(
            error.detail.as_deref(),
            Some(fixture["error.expected.detail"].as_str())
        );
        assert_eq!(error.response_body, decode(&fixture, "error.body.base64"));
    }

    fn client<F>(fixture: &BTreeMap<String, String>, transport: F) -> IRouteClient<F>
    where
        F: Fn(&IRouteRequest) -> Result<IRouteResponse, String>,
    {
        IRouteClient::with_transport(
            "http://sdk-fixture.test",
            ClientOptions {
                token: Some("fixture-token".into()),
                tenant_id: Some(fixture["request.header.tenant"].clone()),
                actor_id: Some(fixture["request.header.actor"].clone()),
                permission_scopes: fixture["request.header.permission-scopes"]
                    .split(' ')
                    .map(str::to_owned)
                    .collect(),
            },
            transport,
        )
    }

    fn response(fixture: &BTreeMap<String, String>, prefix: &str) -> IRouteResponse {
        IRouteResponse {
            status: fixture[&format!("{prefix}.status")].parse().expect("status"),
            body: decode(fixture, &format!("{prefix}.body.base64")),
        }
    }

    fn fixture() -> BTreeMap<String, String> {
        let path = format!(
            "{}/../conformance/v1.properties",
            env!("CARGO_MANIFEST_DIR")
        );
        fs::read_to_string(path)
            .expect("shared fixture")
            .lines()
            .filter_map(|line| line.split_once('='))
            .map(|(key, value)| (key.to_owned(), value.to_owned()))
            .collect()
    }

    fn decode(fixture: &BTreeMap<String, String>, key: &str) -> String {
        String::from_utf8(decode_base64(fixture.get(key).expect("fixture key")))
            .expect("UTF-8 fixture")
    }

    fn decode_base64(value: &str) -> Vec<u8> {
        let mut output = Vec::new();
        let mut chunk = [0_u8; 4];
        let mut length = 0;
        for byte in value.bytes().filter(|byte| *byte != b'=') {
            chunk[length] = match byte {
                b'A'..=b'Z' => byte - b'A',
                b'a'..=b'z' => byte - b'a' + 26,
                b'0'..=b'9' => byte - b'0' + 52,
                b'+' => 62,
                b'/' => 63,
                _ => continue,
            };
            length += 1;
            if length == 4 {
                output.extend_from_slice(&[
                    (chunk[0] << 2) | (chunk[1] >> 4),
                    (chunk[1] << 4) | (chunk[2] >> 2),
                    (chunk[2] << 6) | chunk[3],
                ]);
                length = 0;
            }
        }
        if length == 2 {
            output.push((chunk[0] << 2) | (chunk[1] >> 4));
        } else if length == 3 {
            output.extend_from_slice(&[
                (chunk[0] << 2) | (chunk[1] >> 4),
                (chunk[1] << 4) | (chunk[2] >> 2),
            ]);
        }
        output
    }
}
