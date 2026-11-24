# FinFlow

FinFlow is an MIT-licensed .NET 10 portfolio project for simulated GEL banking. The secured mode includes customer and staff portals, transfers, payment settlement and refunds, notifications, and auditing. The original transfer demo remains available on loopback.

See the [implementation guide](docs/IMPLEMENTATION_GUIDE.md), [current architecture](docs/architecture/current.md), [six-stage roadmap](PROJECT_PLAN.md), and [project status](PROJECT_STATUS.md).

## Architecture

The secured local stack uses one PostgreSQL instance with a separate database for each business service. The gateway routes HTTP and SignalR traffic. Transfers and Payments call private Ledger gRPC endpoints with service-account tokens. RabbitMQ delivers committed events to the notification and audit consumers.

```mermaid
flowchart LR
    Browser[Browser] --> Portal[Angular portal]
    Browser -->|OIDC with PKCE| Identity[Keycloak]
    Portal -->|HTTP and SignalR| Gateway[YARP gateway]
    Gateway --> Customers[Customers API]
    Gateway --> Transfers[Transfers API]
    Gateway --> Payments[Payments API]
    Gateway --> Ledger[Ledger API]
    Gateway --> Notifications[Notifications Worker]
    Gateway --> Audit[Audit API]
    Transfers -->|gRPC posting| Ledger
    Payments -->|gRPC charge and refund| Ledger
    Customers --> Cache[(Valkey profile cache)]
    Customers -->|Lifecycle outbox| Broker[RabbitMQ]
    Transfers -->|Lifecycle outbox| Broker
    Payments -->|Lifecycle outbox| Broker
    Ledger -->|Posting outbox| Broker
    Broker --> Notifications
    Broker --> Audit
    Notifications -->|Synthetic email| Mailpit
    Customers --> Postgres[(PostgreSQL)]
    Transfers --> Postgres
    Payments --> Postgres
    Ledger --> Postgres
    Notifications --> Postgres
    Audit --> Postgres
```

| Application | PostgreSQL database | Responsibilities |
| --- | --- | --- |
| `FinFlow.Gateway` | — | External HTTP and SignalR entry point, route authorization, rate limits, and correlation IDs |
| `FinFlow.Customers.Api` | `finflow_customers` | Customer profiles, simulated KYC, staff customer management, and cache-aside reads |
| `FinFlow.Ledger.Api` | `finflow_ledger` | Account ownership, balances, immutable double-entry postings, statements, and reconciliation |
| `FinFlow.Transfers.Api` | `finflow_transfers` | Customer transfer orders, asynchronous posting, retries, and transfer history |
| `FinFlow.Payments.Api` | `finflow_payments` | Simulated settlement, refunds, compensation, and versioned payment events |
| `FinFlow.Notifications.Worker` | `finflow_notifications` | Deduplicated notifications, read state, SignalR updates, and synthetic email |
| `FinFlow.Audit.Api` | `finflow_audit` | Append-only transfer and workflow audit records, with access restricted by role |
| `frontend` | — | Angular views for customers, staff, and auditors |

Shared projects provide JWT configuration, gRPC contracts, RabbitMQ topology and publishing, and telemetry. [The solution](FinFlow.slnx) also includes integration tests, benchmarks, and an Aspire AppHost.

## Technology stack

| Area | Tools |
| --- | --- |
| API and data | .NET 10, ASP.NET Core, PostgreSQL, EF Core, Dapper, gRPC |
| Messaging and resilience | RabbitMQ, Valkey, Polly, SignalR, Mailpit |
| Identity and UI | Keycloak, OIDC with PKCE, JWT, YARP, Angular, TypeScript |
| Observability | Serilog, OpenTelemetry Collector, Prometheus, Grafana, Loki, Tempo |
| Delivery and testing | Docker Compose, Aspire, Kubernetes on kind, Woodpecker CI, xUnit, Testcontainers, BenchmarkDotNet, k6 |

## Implemented patterns

- **Double-entry ledger and idempotency.** PostgreSQL locks the scoped request key and both accounts. Balances, signed entries, and the transfer outbox event commit together.
- **Transactional outbox and consumer deduplication.** Publishers wait for RabbitMQ confirmation. Consumers persist results before acknowledgment and deduplicate by event ID. Malformed notification and audit deliveries can be preserved in dead-letter queues.
- **Durable Saga and payment event sourcing.** Ordered replay reconstructs the request, status, posting IDs, and retry timing. The worker and refund handler use replayed state. The mutable row supports scheduling and list queries. Version checks guard appends, and Ledger uses separate stable posting keys for charges and reversals.
- **Resilience and read models.** Polly retries selected transient Ledger errors and provides a circuit breaker. Persisted scheduling covers longer interruptions. Valkey caches customer profile reads. Dapper serves ledger statements and reconciliation.

## Screenshots

| Customer portal | Staff operations |
| --- | --- |
| ![FinFlow customer account balances](docs/screenshots/customer.png) | ![FinFlow staff reconciliation and account operations](docs/screenshots/operations.png) |

## Verification status

| Scope | Evidence |
| --- | --- |
| Ledger and messaging | The earlier integration suite passed 9 tests. Both delivery tests that failed during a later interrupted run passed when rerun individually. |
| Secured demo | Earlier smoke runs covered sign-in, role and ownership checks, transfers, payment settlement and compensation, refunds, audit, SignalR, and Mailpit. |
| Payment event sourcing | Four focused Release tests passed without infrastructure. EF generated the upgrade SQL and found no pending model changes. |
| Frontend and telemetry | The patched Angular build passed and its production npm audit reported zero vulnerabilities. Browser smoke passed on the earlier bundle. A transfer trace was verified across five services, with Loki logs and a Prometheus ledger metric. |

**Deferred validation.** Apply the payment stream backfill to PostgreSQL and rerun the secured payment and refund flows. Browser smoke on the patched bundle, a dedicated Valkey outage and Polly breaker check, Aspire gateway readiness, Kubernetes workloads, and Woodpecker execution remain open. Aspire reached its dashboard and kind started its control plane. See [project status](PROJECT_STATUS.md) for details.

## Start the demo

For a laptop session focused on balances and transfers, start only PostgreSQL and the Ledger API:

```sh
docker compose up -d --build ledger
```

Events remain in the outbox. Start RabbitMQ and the notification worker only when you want to see delivery:

```sh
docker compose up -d rabbitmq notifications
```

Use `docker compose stop` when you finish. Run one local stack at a time. The secured portal, observability overlay, Aspire, and kind are separate development options.

With Docker Engine running in Linux-container mode and the Docker Compose plugin available, run this from the repository root.

```sh
docker compose up --build
```

Compose initializes two databases in one PostgreSQL instance. Each application applies its own EF Core migrations at startup. PostgreSQL and RabbitMQ use named volumes. The values in `.env.example` match the built-in local demo defaults. To override the port or synthetic passwords, copy it to `.env` and edit it before the first startup. Use URL-safe characters for the RabbitMQ password and letters, digits, or hyphens for the PostgreSQL password when using the provided Compose file.

To run either application independently with the .NET 10 SDK, use the corresponding command below and supply the environment variables for that process. Point them to a reachable PostgreSQL database and RabbitMQ endpoint.

| Command | Required environment variables |
| --- | --- |
| `dotnet run --project src/FinFlow.Ledger.Api --no-launch-profile -- --urls http://127.0.0.1:8080` | `ConnectionStrings__Ledger`, `RabbitMq__Uri` |
| `dotnet run --project src/FinFlow.Notifications.Worker` | `ConnectionStrings__Notifications`, `RabbitMq__Uri` |

## Transfer money

Run these commands in a second terminal using Bash or Git Bash. The first call waits for the API if the containers are still starting.

```sh
base=http://127.0.0.1:8080
curl -fsS --retry 12 --retry-connrefused --retry-delay 1 "$base/accounts"
echo

transfer='{"sourceAccountId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","destinationAccountId":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb","amountMinor":1234}'
curl -fsSi "$base/transfers" \
  -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: demo-transfer-01' \
  --data "$transfer"
echo

curl -fsS "$base/accounts"
echo
```

On a fresh database, Alice and Bob start at **100000** and **50000** tetri. After transferring **1234** tetri (12.34 GEL), their balances are **98766** and **51234**. The POST returns `201 Created` and a `Location` header for `GET /transfers/{id}`. Repeat the same POST to see the original receipt without changing balances. Using the same key with a different payload returns `409 Conflict`. Insufficient funds also returns `409`. Invalid amounts, same-account transfers, and missing or invalid keys return `400`. Unknown accounts return `404`.

Query the worker database after a few seconds and match `TransferId` to the receipt.

```sh
docker compose exec -T postgres psql -U finflow -d finflow_notifications <<'SQL'
SELECT n."TransferId", n."RecipientAccountId", n."AmountMinor", n."Currency", p."ProcessedAt"
FROM "Notifications" n
JOIN "ProcessedEvents" p ON p."Id" = n."EventId"
ORDER BY p."ProcessedAt";
SQL
```

## Reproduce RabbitMQ recovery

Keep the ledger and worker running. In the same Bash session, stop RabbitMQ, submit the same transfer body with a new key, and inspect the outbox.

```sh
docker compose stop rabbitmq
curl -fsSi "$base/transfers" \
  -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: demo-broker-recovery-01' \
  --data "$transfer"
echo

docker compose exec -T postgres psql -U finflow -d finflow_ledger <<'SQL'
SELECT "Id", "Payload"::jsonb ->> 'TransferId' AS transfer_id, "PublishedAt"
FROM "Outbox"
ORDER BY "CreatedAt";
SQL
```

The POST returns `201` while the broker is down. The matching outbox row has a blank `PublishedAt` and the worker query has no matching notification. Bring the broker back with `docker compose start rabbitmq`. Repeat both SQL queries after RabbitMQ starts. The row now has a publication timestamp and the worker database has one matching notification. Use a fresh key when repeating the outage demo.

Run `docker compose down` to stop the stack and keep the saved demo data. Run `docker compose down --volumes` only when you want to delete the local demo databases and broker data and start from the seeded balances again.

## Run the tests

With the .NET 10 SDK and Docker Engine running, run:

```sh
dotnet test
```

The xUnit suite provisions real PostgreSQL and RabbitMQ containers with Testcontainers. It checks balance and ledger changes, rejected transfers, concurrent debits, concurrent idempotent retries, conflicting payloads, broker recovery, and a RabbitMQ redelivery after the notification transaction has committed.

## Run the secured local mode

```sh
docker compose -p finflow-secure -f compose.yaml -f compose.secure.yaml up --build
```

Open [the Angular portal](http://localhost:4200). Sign in as `customer` to register a profile. Sign in as `operator` to verify that profile, open a GEL account for its subject, and fund it from a seeded demo account. The customer can then submit transfers and simulated payments, request refunds, read statements, and receive notifications. Sign in as `auditor` to view workflow events. Captured synthetic email is available in [Mailpit](http://localhost:8025).

YARP listens on loopback port 8081. Keycloak listens at `http://localhost:8082` and imports the `finflow` realm. The secured configuration uses a public `finflow-portal` client and Authorization Code with S256 PKCE. Synthetic usernames are `customer`, `admin`, `operator`, and `auditor`, with the local demo password `finflow-local-demo`. You can override these ports and the shared synthetic password using `.env`.

Administrator and BankOperator roles can access the original ledger routes. Customers can view accounts that staff provisioned for their identity, read their statements, and submit transfers from those accounts. The ledger validates JWTs independently and has no published host port in this mode. The base Compose demo commands above use a separate project and remain available.

| Route | Role and behavior |
| --- | --- |
| `POST /customers/me` | Customer registers a synthetic profile with `{"displayName":"Demo Customer"}`. |
| `GET /customers/me`, `PUT /customers/me` | Customer reads or updates the profile linked to their identity. |
| `GET /staff/customers?size=25` | Staff list profiles. Use the returned `next` value as the `after` query parameter. |
| `GET /staff/customers/{subject}`, `PUT /staff/customers/{subject}/status` | Staff read or update a profile. The status body contains `kyc` and `active`. |
| `POST /staff/ledger/accounts` | Staff provision a zero-balance GEL account with `name` and the customer's `ownerSubject` from their profile. |
| `GET /customers/me/accounts`, `GET /customers/me/accounts/{id}/statement` | Customers list their accounts and read a statement for an account they own. |
| `POST /customers/me/transfers` | Customers submit a transfer with the fields from the v0.1 demo and an `Idempotency-Key`. Returns `202` and a status URL. |
| `GET /customers/me/transfers/{id}`, `GET /customers/me/transfers?size=25` | Customers read the outcome or browse their transfer history. Use the returned `next` value as `before`. |
| `POST /customers/me/payments`, `GET /customers/me/payments/{id}` | Customers submit a simulated payment with `accountId`, `amountMinor`, `scenario`, and an `Idempotency-Key` header, then poll its status. |
| `POST /customers/me/payments/{id}/refund` | Customers request a refund for an owned settled payment. |
| `GET /customers/me/notifications`, `PUT /customers/me/notifications/{id}/read` | Customers read notification history and mark items as read. |
| `GET /accounts/{id}/statement?size=25` | Staff read a signed ledger statement. Pass the returned `beforeId` and `beforeTime` cursor values to fetch the next page. |
| `GET /staff/ledger/reconciliation` | Staff check the transfer and invalid-posting counts. |
| `GET /staff/audit/transfers?transferId={id}` | Auditors and administrators read transfer completion audit entries. Use `size` and the returned `next` value as `before` for pagination. |
| `GET /staff/audit/workflows?entityId={id}` | Auditors and administrators read customer, transfer, and payment lifecycle events. |

The Transfer API persists orders in `finflow_transfers`. Its worker retries the same order ID when the ledger call fails or its outcome is uncertain. The internal gRPC endpoint requires a Keycloak service-account token. The ledger rechecks source ownership and posts atomically. New accounts start at zero. Staff can fund a demo account using the original transfer endpoint.

With Node.js 22 or newer, verify sign-in, authorization, profile registration, simulated KYC, account ownership, and transfers against the running secured stack. The smoke check creates and funds a synthetic account.

The Audit API consumes transfer-completed events from its own durable RabbitMQ queue and stores event IDs, transfer IDs, timestamps, and correlation identifiers in `finflow_audit`. The broker also retains malformed notification deliveries in `finflow.notifications.dead-letter` for local inspection.

```sh
node --env-file-if-exists=.env tests/smoke-secure.mjs
```

To include a temporary ledger outage in the smoke check, set `FINFLOW_SMOKE_COMPOSE_PROJECT=finflow-secure` in your shell before running it against that local Compose project. The script stops and restarts the ledger and checks that a pending transfer posts once.

Keycloak imports the realm on first startup. If a local stack predates the transfer or payment service account, use a fresh Compose project to load the updated realm. Stop the secured stack when finished:

```sh
docker compose -p finflow-secure -f compose.yaml -f compose.secure.yaml stop
```

## Payment event sourcing

Payment requests, Saga transitions, and retry schedules are recorded in a versioned payment event stream. The worker and refund handler replay the stream before making decisions. The payment row supports scheduling and list queries, and Ledger uses stable posting keys for charges and reversals. The focused replay tests run without Docker:

```sh
dotnet test tests/FinFlow.Tests/FinFlow.Tests.csproj -c Release --filter FullyQualifiedName~PaymentStreamTests
```

Live PostgreSQL migration and secured payment smoke checks for this update are pending. See the [implementation guide](docs/IMPLEMENTATION_GUIDE.md) for the event and projection write path.

## Observability, performance, and deployment

```sh
docker compose -p finflow-secure -f compose.yaml -f compose.secure.yaml -f compose.observability.yaml up --build
```

Open [Grafana](http://localhost:3000) and select **FinFlow workflows**. Use Explore with Tempo for request traces or Loki for structured logs. See [measured performance](docs/benchmarks/results.md) and [local deployment instructions](docs/DEPLOYMENT.md) for Aspire, Kubernetes, and Woodpecker.

