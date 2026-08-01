use iroute_sdk::{ClientOptions, IRouteClient};
use std::env;
use std::time::{SystemTime, UNIX_EPOCH};

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let client = IRouteClient::new(
        env::var("IROUTE_URL").unwrap_or_else(|_| "http://localhost:8080".into()),
        ClientOptions {
            token: env::var("IROUTE_TOKEN").ok(),
            tenant_id: Some(env::var("IROUTE_TENANT").unwrap_or_else(|_| "demo".into())),
            actor_id: Some(env::var("IROUTE_ACTOR").unwrap_or_else(|_| "sdk-example".into())),
            permission_scopes: Vec::new(),
        },
    );
    let suffix = SystemTime::now().duration_since(UNIX_EPOCH)?.as_nanos();
    let request = format!(
        r#"{{"taskType":"email.draft","input":{{"purpose":"Confirm the SDK quick start."}},"idempotencyKey":"rust-example-{suffix}"}}"#
    );
    println!("{}", client.execute_json(&request, Some(&format!("rust-example-{suffix}")))?);
    Ok(())
}
