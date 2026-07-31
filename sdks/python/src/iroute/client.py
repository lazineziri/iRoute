import json
from urllib.request import Request, urlopen


class IRouteClient:
    def __init__(self, base_url: str, token: str | None = None) -> None:
        self._base_url = base_url.rstrip("/")
        self._token = token

    def execute(self, task_type: str, input_data: object) -> dict[str, object]:
        headers = {"Content-Type": "application/json"}
        if self._token:
            headers["Authorization"] = f"Bearer {self._token}"
        request = Request(
            f"{self._base_url}/v1/executions",
            data=json.dumps({"taskType": task_type, "input": input_data}).encode(),
            headers=headers,
            method="POST",
        )
        with urlopen(request, timeout=30) as response:
            return json.load(response)
