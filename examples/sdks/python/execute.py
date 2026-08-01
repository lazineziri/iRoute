import os
import uuid

from iroute import IRouteClient

client = IRouteClient(
    os.getenv("IROUTE_URL", "http://localhost:8080"),
    token=os.getenv("IROUTE_TOKEN"),
    tenant_id=os.getenv("IROUTE_TENANT", "demo"),
    actor_id=os.getenv("IROUTE_ACTOR", "sdk-example"),
)
result = client.execute({
    "taskType": "email.draft",
    "input": {"purpose": "Confirm the SDK quick start."},
    "idempotencyKey": f"python-example-{uuid.uuid4()}",
})
print(result)
