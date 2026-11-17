# Enrollment component deployment plan

Status: In preparation (architecture previously approved).

## Scope and ownership
Prepare a .NET 10 enrollment service for Azure Container Apps and an independently owned Azure SQL database. The central contoso-platform repository exclusively owns azd, Bicep, environment provisioning, identity assignments and deployment. This repository will not create or deploy Azure infrastructure.

## Component decisions
Use Entra JWT validation, managed-identity downstream calls to Students and Academics, EF Core SQL Server migrations run separately with a privileged identity, and Azure Monitor OpenTelemetry with managed identity. Runtime receives only DML access; the platform configures probes and permissions. Do not seed unverified upstream references. Dev and staging are provisioned by the platform per environment.

## Verification and release
Implement and test locally, including authorization and relational persistence; CI covers SQL Server and immutable candidate packaging. Local substitutes are not evidence of Azure deployment or telemetry. No Azure resource or default changes are authorized by this component preparation.
