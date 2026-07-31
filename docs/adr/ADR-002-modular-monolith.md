# ADR-002: Modular monolith first

Status: Accepted

The first runtime is a modular .NET monolith with separately deployable API and worker hosts. Services will be split only when independent scaling, isolation, ownership, or availability evidence exceeds distributed-system cost.
