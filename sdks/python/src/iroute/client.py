from __future__ import annotations

import json
from collections.abc import Callable, Iterable, Iterator, Mapping, Sequence
from typing import Any, BinaryIO
from urllib.error import HTTPError
from urllib.parse import quote, urlencode
from urllib.request import Request, urlopen

JsonObject = dict[str, Any]
Opener = Callable[..., BinaryIO]


class _Unset:
    """Distinguishes "no timeout argument given" from an explicit ``None`` (wait forever)."""


_UNSET = _Unset()


class IRouteApiError(RuntimeError):
    def __init__(self, status: int, response_body: str) -> None:
        self.status = status
        self.response_body = response_body
        try:
            problem = json.loads(response_body)
        except json.JSONDecodeError:
            problem = {}
        self.code = problem.get("code")
        self.title = problem.get("title")
        self.detail = problem.get("detail")
        super().__init__(self.detail or self.title or f"iRoute request failed with HTTP {status}")


class IRouteClient:
    def __init__(
        self,
        base_url: str,
        *,
        token: str | None = None,
        tenant_id: str | None = None,
        actor_id: str | None = None,
        permission_scopes: Sequence[str] = (),
        timeout: float = 30,
        stream_timeout: float | None = None,
        opener: Opener = urlopen,
    ) -> None:
        self._base_url = base_url.rstrip("/")
        self._token = token
        self._tenant_id = tenant_id
        self._actor_id = actor_id
        self._permission_scopes = tuple(permission_scopes)
        self._timeout = timeout
        # urlopen applies its timeout to each read, not to the whole call, so the request timeout
        # would abort a live event stream that is merely quiet. The contract allows deadlines up to
        # 15 minutes and defines no heartbeat frame, so streaming waits indefinitely by default.
        self._stream_timeout = stream_timeout
        self._opener = opener

    def execute(
        self,
        request_or_task_type: Mapping[str, Any] | str,
        input_data: object | None = None,
    ) -> JsonObject:
        request = (
            dict(request_or_task_type)
            if isinstance(request_or_task_type, Mapping)
            else {"taskType": request_or_task_type, "input": input_data}
        )
        headers: dict[str, str] = {}
        idempotency_key = request.get("idempotencyKey")
        if isinstance(idempotency_key, str) and idempotency_key:
            headers["Idempotency-Key"] = idempotency_key
        return self._request_json("POST", "/v1/executions", request, headers=headers)

    def get(self, execution_id: str) -> JsonObject | None:
        return self._request_json(
            "GET",
            f"/v1/executions/{quote(execution_id, safe='')}",
            not_found=True,
        )

    def cancel(self, execution_id: str) -> bool:
        response = self._request(
            "POST",
            f"/v1/executions/{quote(execution_id, safe='')}/cancel",
            not_found=True,
        )
        if response is None:
            return False
        response.close()
        return True

    def submit_approval(self, execution_id: str, decision: Mapping[str, Any]) -> JsonObject:
        return self._request_json(
            "POST",
            f"/v1/executions/{quote(execution_id, safe='')}/approvals",
            dict(decision),
        )

    def get_artifact(self, artifact_id: str) -> JsonObject | None:
        return self._request_json(
            "GET",
            f"/v1/artifacts/{quote(artifact_id, safe='')}",
            not_found=True,
        )

    def get_model_gateway_health(self) -> JsonObject:
        return self._request_json("GET", "/health/model-gateway", allowed_statuses={503})

    def get_observability_summary(
        self,
        *,
        from_time: str | None = None,
        to_time: str | None = None,
        task_type: str | None = None,
        policy_version: str | None = None,
    ) -> JsonObject:
        query = urlencode({
            key: value
            for key, value in {
                "from": from_time,
                "to": to_time,
                "taskType": task_type,
                "policyVersion": policy_version,
            }.items()
            if value is not None
        })
        return self._request_json("GET", f"/v1/observability/summary{f'?{query}' if query else ''}")

    def get_execution_timeline(self, execution_id: str) -> JsonObject | None:
        return self._request_json(
            "GET",
            f"/v1/observability/executions/{quote(execution_id, safe='')}",
            not_found=True,
        )

    def stream_events(self, execution_id: str, after_sequence: int = 0) -> Iterator[JsonObject]:
        response = self._request(
            "GET",
            f"/v1/executions/{quote(execution_id, safe='')}/events?after={after_sequence}",
            headers={"Accept": "text/event-stream"},
            timeout=self._stream_timeout,
        )
        if response is None:
            return
        data_lines: list[str] = []
        try:
            for raw_line in response:
                line = raw_line.decode("utf-8").rstrip("\r\n")
                if not line:
                    if data_lines:
                        yield json.loads("\n".join(data_lines))
                        data_lines.clear()
                    continue
                if line.startswith("data:"):
                    data_lines.append(line[5:].lstrip())
            if data_lines:
                yield json.loads("\n".join(data_lines))
        finally:
            response.close()

    def _request_json(
        self,
        method: str,
        path: str,
        body: Mapping[str, Any] | None = None,
        *,
        headers: Mapping[str, str] | None = None,
        not_found: bool = False,
        allowed_statuses: set[int] | None = None,
    ) -> JsonObject | None:
        response = self._request(
            method,
            path,
            body,
            headers=headers,
            not_found=not_found,
            allowed_statuses=allowed_statuses,
        )
        if response is None:
            return None
        try:
            return json.load(response)
        finally:
            response.close()

    def _request(
        self,
        method: str,
        path: str,
        body: Mapping[str, Any] | None = None,
        *,
        headers: Mapping[str, str] | None = None,
        not_found: bool = False,
        allowed_statuses: set[int] | None = None,
        timeout: float | None | _Unset = _UNSET,
    ) -> BinaryIO | None:
        request_headers = self._headers(headers)
        data = None
        if body is not None:
            request_headers["Content-Type"] = "application/json"
            data = json.dumps(body, separators=(",", ":")).encode("utf-8")
        request = Request(
            f"{self._base_url}{path}",
            data=data,
            headers=request_headers,
            method=method,
        )
        try:
            response = self._opener(
                request,
                timeout=self._timeout if isinstance(timeout, _Unset) else timeout,
            )
        except HTTPError as error:
            return self._handle_error(error, not_found, allowed_statuses)
        status = _status(response)
        if 200 <= status < 300 or status in (allowed_statuses or set()):
            return response
        return self._handle_error(response, not_found, allowed_statuses)

    def _handle_error(
        self,
        response: BinaryIO,
        not_found: bool,
        allowed_statuses: set[int] | None,
    ) -> BinaryIO | None:
        status = _status(response)
        if status == 404 and not_found:
            response.close()
            return None
        if status in (allowed_statuses or set()):
            return response
        body = response.read().decode("utf-8", errors="replace")
        response.close()
        raise IRouteApiError(status, body)

    def _headers(self, extra: Mapping[str, str] | None) -> dict[str, str]:
        headers = dict(extra or {})
        if self._token:
            headers["Authorization"] = f"Bearer {self._token}"
        if self._tenant_id:
            headers["X-Tenant-Id"] = self._tenant_id
        if self._actor_id:
            headers["X-Actor-Id"] = self._actor_id
        if self._permission_scopes:
            headers["X-Permission-Scopes"] = " ".join(self._permission_scopes)
        return headers


def _status(response: BinaryIO) -> int:
    status = getattr(response, "status", None)
    if status is not None:
        return int(status)
    return int(response.getcode())
