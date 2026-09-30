# ADR-002: Modular monolith first

Status: Accepted

The runtime is a modular .NET monolith with one executable and one composition
root. API, worker, migration, and client behavior are process modes of that
executable; API and worker processes may still scale independently in deployment.
Services will be split into separate deployables only when measured scaling,
isolation, ownership, or availability needs exceed the distributed-system cost.
