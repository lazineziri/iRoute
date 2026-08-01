import base64
import io
import json
import sys
import unittest
from pathlib import Path
from urllib.request import Request

sys.path.insert(0, str(Path(__file__).parents[1] / "src"))

from iroute import IRouteApiError, IRouteClient


def load_fixture() -> dict[str, str]:
    path = Path(__file__).parents[2] / "conformance" / "v1.properties"
    return dict(line.split("=", 1) for line in path.read_text().splitlines() if line)


FIXTURE = load_fixture()


def decode(key: str) -> str:
    return base64.b64decode(FIXTURE[key]).decode()


class FakeResponse(io.BytesIO):
    def __init__(self, status: int, body: str) -> None:
        super().__init__(body.encode())
        self.status = status


class CapturingOpener:
    def __init__(self, status: int, body: str) -> None:
        self.status = status
        self.body = body
        self.request: Request | None = None

    def __call__(self, request: Request, *, timeout: float) -> FakeResponse:
        self.request = request
        return FakeResponse(self.status, self.body)


class ConformanceTests(unittest.TestCase):
    def test_request_fixture(self) -> None:
        opener = CapturingOpener(int(FIXTURE["response.status"]), decode("response.body.base64"))
        client = self.create_client(opener)
        request_body = json.loads(decode("request.body.base64"))

        result = client.execute(request_body)

        self.assertIsNotNone(opener.request)
        assert opener.request is not None
        headers = {key.lower(): value for key, value in opener.request.header_items()}
        self.assertEqual(opener.request.method, FIXTURE["request.method"])
        self.assertEqual(opener.request.full_url, "http://sdk-fixture.test" + FIXTURE["request.path"])
        self.assertEqual(headers["authorization"], FIXTURE["request.header.authorization"])
        self.assertEqual(headers["content-type"], FIXTURE["request.header.content-type"])
        self.assertEqual(headers["x-tenant-id"], FIXTURE["request.header.tenant"])
        self.assertEqual(headers["x-actor-id"], FIXTURE["request.header.actor"])
        self.assertEqual(headers["x-permission-scopes"], FIXTURE["request.header.permission-scopes"])
        self.assertEqual(headers["idempotency-key"], FIXTURE["request.header.idempotency-key"])
        self.assertEqual(json.loads(opener.request.data or b"{}"), request_body)
        self.assertEqual(result["executionId"], "019fbc00-0000-7000-8000-000000000014")

    def test_stream_fixture(self) -> None:
        opener = CapturingOpener(int(FIXTURE["stream.status"]), decode("stream.body.base64"))
        client = self.create_client(opener)

        events = list(client.stream_events("019fbc00-0000-7000-8000-000000000014"))

        self.assertEqual(len(events), int(FIXTURE["stream.expected.count"]))
        self.assertEqual([event["type"] for event in events], FIXTURE["stream.expected.types"].split(","))

    def test_error_fixture(self) -> None:
        opener = CapturingOpener(int(FIXTURE["error.status"]), decode("error.body.base64"))
        client = self.create_client(opener)

        with self.assertRaises(IRouteApiError) as raised:
            client.execute(json.loads(decode("request.body.base64")))

        error = raised.exception
        self.assertEqual(error.status, int(FIXTURE["error.status"]))
        self.assertEqual(error.code, FIXTURE["error.expected.code"])
        self.assertEqual(error.title, FIXTURE["error.expected.title"])
        self.assertEqual(error.detail, FIXTURE["error.expected.detail"])
        self.assertEqual(error.response_body, decode("error.body.base64"))

    @staticmethod
    def create_client(opener: CapturingOpener) -> IRouteClient:
        return IRouteClient(
            "http://sdk-fixture.test",
            token="fixture-token",
            tenant_id=FIXTURE["request.header.tenant"],
            actor_id=FIXTURE["request.header.actor"],
            permission_scopes=FIXTURE["request.header.permission-scopes"].split(),
            opener=opener,
        )


if __name__ == "__main__":
    unittest.main()
