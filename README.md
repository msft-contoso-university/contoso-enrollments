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

CI builds Linux AMD64 once in native OCI format using the hosted runner's Podman,
then exports
`contoso-enrollments-<source-sha>.oci.tar`. It verifies every OCI blob, revision,
nonroot configuration and unchanged image config/root-filesystem identity.
`untrusted-candidate-${github.sha}-${github.run_id}-${github.run_attempt}` contains
that archive, `candidate.json`, `SHA256SUMS` and `sql/migrations.sql`. The manifest's
`image.manifestDigest` is the exported OCI manifest digest; `image.configDigest`
is the original built OCI image ID, not a registry repository digest. The platform
must preserve the OCI digest on import and promote it unchanged. No registry
import, transport, independent approval or Azure release is implied by candidate
creation or its self-reported hashes.

## Legacy semantics and v1 differences

The original MIT-licensed source at commit `0716ee7262eff2c268ebb72097ceb93e0fc83536` represented the association as `StudentCourse` with student and course IDs and returned courses nested in Students. It had no grade field in that model. V1 owns a unique pair with a separate ID and an optional grade restricted to A/B/C/D/F; it exposes explicit association CRUD and bounded filtered pages rather than nested student records. It deliberately has no cross-service SQL foreign keys, no remote cascade, and no fabricated enrollment seeds. See `LICENSE` for the original copyright and MIT terms.

Candidate CI runs read-only CodeQL for C#, GitHub Actions and Python and retains SARIF as an artifact; it does not upload registered code-scanning alerts. Native default setup remains enabled and must be reassessed after source reaches main. Candidate SARIF is not evidence of native alerts.

## Offline affected-consumer compatibility

Run `dotnet test tests\Contoso.Enrollments.Tests --filter TestCategory=ConsumerCompatibility`.
With neither variable set this exercises built-in positive fixtures and negative
missing/renamed `id`, wrong type/value, malformed and oversized response cases through
the production `ReferenceLookup`, fake test-only credential and an injected HTTP handler.
No producer OpenAPI comparison substitutes for consuming the actual response.

For actual producer captures set `CONTOSO_CONSUMER_PRODUCER` to `contoso-students`
or `contoso-academics` and `CONTOSO_CONSUMER_SAMPLES` to an absolute existing directory.
The same command requires at least one `Student` envelope for Students or `Course`
envelope for Academics. Each top-level `.json` file is exactly
`{"schema":"Student","body":{...actual HTTP response JSON...}}` (or `"Course"`).
Schema names are case-sensitive and filenames arbitrary. All required-category
samples are consumed, with a positive integer `body.id` used for the request.
`StudentPage` and Academics' `CoursePage`, `Department`, `DepartmentPage`,
`Instructor`, `InstructorPage` may coexist but are not consumed.

Only the positive integer `id` field is used by this consumer. Other fields may
evolve independently; they are intentionally not validated here. The harness asserts
one app-only GET-by-ID per sample (`/api/v1/students/{id}` or `/api/v1/courses/{id}`),
the existing per-resource `.default` scope, and fake Bearer authentication. It never
enumerates lists, adds roles/scopes, fetches URLs in artifacts or acquires live tokens.
Production response deserialization now requires the identity constructor parameter,
strict JSON number types and a 1 MiB maximum body; incompatible responses use the
existing `LookupResult.Unavailable` path (503 in the enrollment create endpoint).

Partial/empty environment configuration, unknown producer/category, missing required
coverage, malformed JSON, nonobject envelopes/bodies, traversal, symlinks/reparse
points (including directory ancestors), subdirectories and non-JSON files fail.
Limits: 32 files, 1 MiB/file, 4 MiB total, JSON depth 32. Supply a dedicated immutable
artifact directory on a controlled runner: this loader is not an archive extractor
or protection against concurrent filesystem mutation. This repository's build job
also exercises the harness using explicitly synthetic envelopes; those fixtures are
not producer evidence. The Students and Academics producer workflows separately
invoke the pinned Enrollments consumer against their actual captured HTTP bodies.
These are offline payload checks, not live Entra/SQL/Azure or deployment evidence.

The SQL integration test also sends real in-process HTTP requests authenticated with
the test-only signed delegated Registrar JWT against the production routes backed
by its migrated loopback SQL database. After successful POST, GET-by-ID, list and
grade-update assertions, it writes five **actual** response envelopes (three
`Enrollment`, two `EnrollmentPage`, including null and nonnull grades) when
`CONTOSO_PRODUCER_SAMPLES` names an absolute output directory. Upstream lookups remain
offline/fake. CI uploads only after all SQL HTTP tests and the no-skipped TRX gate pass,
as `producer-response-samples-${{ github.sha }}-${{ github.run_attempt }}`. This is
synthetic test data captured from real HTTP responses, not live university data or
a handwritten response fixture. After that job succeeds, CI invokes the
[immutable shared workflow](https://github.com/msft-contoso-university/contoso-engineering/blob/eb98dd06864557ab29afc0567664ad7f20055358/.github/workflows/consumer-checks.yml).
It retrieves this repository's same-run, SHA/attempt-qualified artifact and runs
the frozen Web consumer selected by the independently pinned candidate policy.
The `Required` aggregate requires that check to pass; missing, zero, skipped or
failed test evidence does not pass. The reusable job has only contents/Actions
read permissions and inherits no secrets.

[Run 44149555454](https://github.com/msft-contoso-university/contoso-enrollments/actions/runs/44149555454)
passed the actual Enrollments-to-Web check. These source and policy pins are
unapproved review candidates, not trusted release or live integration evidence.
