# Contoso Enrollments

A small .NET 10 API owning student-course associations in its own SQL database. `contracts/enrollments.openapi.yaml` is the published v1 interface. `GET /health/live` and `/health/ready` are anonymous; readiness checks the enrollment table, not upstream availability. Startup never creates, migrates or seeds a database.

## Identity and configuration

| Environment variable | Purpose |
| --- | --- |
| `ConnectionStrings__Enrollments` | SQL connection for this service's database. Use Entra authentication / managed identity in Azure. |
| `AzureAd__TenantId` | Tenant used for v2.0 JWT authority. |
| `AzureAd__Audience` | This API's resource audience. |
| `Students__BaseUrl`, `Academics__BaseUrl` | HTTPS backend origins ending in `/`. |
| `Students__Scope`, `Academics__Scope` | Each target resource's `api://<applicationId>/.default`. |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | Required outside Development/Testing; authentication uses DefaultAzureCredential. |
| `AZURE_CLIENT_ID` | Optional user-assigned managed identity client ID. |

Delegated reads require exact `enrollments.read` and Reader or Registrar; writes require exact `enrollments.write` and Registrar. Tokens with `idtyp=app` are not authorized, including if they carry delegated-looking claims. Signature, issuer, audience and lifetime are validated. Health endpoints expose no details.

On creation, a managed identity obtains separate app-only tokens for Students and Academics. Platform must grant `Students.Lookup` and `Academics.Lookup` to this workload identity, configure the targets to emit `idtyp=app`, and configure their API resource audiences; URLs or scopes alone are not permissions. `GET /api/v1/students/{id}` is based on the [pinned Students contract](https://github.com/msft-contoso-university/contoso-students/blob/0f9043cb862e5d20644efb32dc690baa16a100a7/contracts/students.openapi.yaml); Academics uses `GET /api/v1/courses/{id}` from the [pinned Academics contract](https://github.com/msft-contoso-university/contoso-academics/blob/2eebfe00836c54564bd6519667094f4f71e48dc2/contracts/academics.openapi.yaml). Missing references are 400, while authentication failures, malformed responses, timeouts and unavailable backends are 503. Existing associations may outlive deletion of a remote record. Validation is not a distributed transaction or a remote foreign key.

The platform owns its whole-stack azd/Bicep, dev/staging environments, network access, identity registration, app-role assignment, SQL principal permissions, App Insights and deployment. The runtime principal needs SQL DML only; a separate privileged principal runs migrations. App Insights local authentication is disabled by the platform and the runtime identity needs **Monitoring Metrics Publisher** on that resource. Do not combine SDK telemetry with platform auto-instrumentation. No actual Azure ingestion or deployment has been verified here.

## Database and release

Use `dotnet tool restore`, then `dotnet ef database update --project src/Contoso.Enrollments.Api` for a controlled bootstrap, or run the published executable as `dotnet Contoso.Enrollments.Api.dll --migrate` using a privileged `ConnectionStrings__Enrollments`. `sql/migrations.sql` is an idempotent equivalent generated from the committed EF migration; review and apply it through the platform's controlled release process. The API process never applies it. No demo enrollment is seeded: upstream synthetic IDs have not been verified. `--seed-demo` is intentionally unsupported.

Restore with `dotnet restore --locked-mode`, then `dotnet test` and `dotnet publish src/Contoso.Enrollments.Api -c Release`. `Dockerfile` builds a non-root framework-dependent container, with pinned official multi-platform image digests. CI builds a SHA-labelled candidate image and migration artifact; the platform must publish/promote that exact candidate rather than rebuilding between environments. Configure Container Apps liveness `/health/live`, readiness `/health/ready`, port 8080. Docker and Azure SQL are not required for local SQLite tests; CI runs a disposable SQL Server 2022 container with random test database names and checks migration, uniqueness and concurrency. The test SQL target rejects non-loopback hosts. Local SQLite is not evidence of Azure SQL behavior.

## Legacy semantics and v1 differences

The original MIT-licensed source at commit `0716ee7262eff2c268ebb72097ceb93e0fc83536` represented the association as `StudentCourse` with student and course IDs and returned courses nested in Students. It had no grade field in that model. V1 owns a unique pair with a separate ID and an optional grade restricted to A/B/C/D/F; it exposes explicit association CRUD and bounded filtered pages rather than nested student records. It deliberately has no cross-service SQL foreign keys, no remote cascade, and no fabricated enrollment seeds. See `LICENSE` for the original copyright and MIT terms.
