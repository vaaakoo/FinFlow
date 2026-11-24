# FinFlow Development and Testing Handoff

FinFlow is a local simulation of GEL banking. The core ledger, secured customer and staff workflows, simulated payments, notifications, audit, and telemetry have been exercised. Final release validation paused when the laptop became overloaded. The repository contains deployment tooling whose end-to-end checks are still open.

## When the work happened

I built and tested the Stage 3–6 changes during the long implementation job that was already running when you sent “laptop cant work here is lot of service run.” I overlapped the secured Compose stack, Aspire, a local kind cluster, and Docker-backed tests. That was too much to run together on this laptop.

After your message, I stopped the running verification jobs, checked that no Docker containers were running, updated the status and low-resource instructions, and regenerated the npm lockfile without installing dependencies. The subsequent status PDF was document work. No FinFlow stack was started for that PDF or this handoff.

## What the change count includes

Before adding this handoff, Git reported the following against local commit `4c6fed4` (`Add Audit API for transfer event tracking`):

- **50 tracked files changed**, with **944 insertions and 92 deletions**.
- **69 untracked files**, including **19 EF Core migration files** and an **8,080-line Angular package lockfile**. The untracked group also includes the Payments API, Angular portal, deployment configuration, benchmarks, screenshots, and documentation.
- The staged diff was empty. The work is still in the local working tree.

An editor’s count depends on how it treats untracked and generated files. Its “3,100 changes” figure cannot be reconstructed from the message alone. For a review of handwritten changes, start with `git diff --stat`, then inspect individual source files. Review the lockfile and EF migration snapshots separately.

## How the project works

| Area | What FinFlow does | Where to look |
| --- | --- | --- |
| Sign-in and access | Keycloak issues tokens. YARP routes requests. Each secured API checks the token and applies ownership or role rules. | [FinFlowIdentity.cs](../src/FinFlow.Authentication/FinFlowIdentity.cs), [gateway routes](../src/FinFlow.Gateway/appsettings.json), [Keycloak realm](../infrastructure/keycloak/finflow-realm.json) |
| Profiles and accounts | Customers register synthetic profiles. Staff handle simulated KYC, open accounts, and fund them from seeded demo balances. Valkey caches profile reads. | [CustomerEndpoints.cs](../src/FinFlow.Customers.Api/CustomerEndpoints.cs), [CustomerProfileCache.cs](../src/FinFlow.Customers.Api/CustomerProfileCache.cs), [Accounts.cs](../src/FinFlow.Ledger.Api/Accounts.cs) |
| Transfers | The Transfers API saves a pending order and returns HTTP 202. The worker sends the same order ID to Ledger over gRPC on retry. Ledger checks ownership and funds, locks the account rows, and commits the debit, credit, entries, and event in one transaction. The base demo calls Ledger directly and returns HTTP 201. | [TransferEndpoints.cs](../src/FinFlow.Transfers.Api/TransferEndpoints.cs), [PostingWorker.cs](../src/FinFlow.Transfers.Api/PostingWorker.cs), [Transfers.cs](../src/FinFlow.Ledger.Api/Transfers.cs) |
| Simulated payments | The Payments worker advances a stored workflow. Ledger charges a protected settlement account and records a separate reversal for a refund or declined settlement. An uncertain call can be retried with the same payment ID. | [PaymentWorker.cs](../src/FinFlow.Payments.Api/PaymentWorker.cs), [SimulatedProvider.cs](../src/FinFlow.Payments.Api/SimulatedProvider.cs), [LedgerPostingService.cs](../src/FinFlow.Ledger.Api/LedgerPostingService.cs) |
| Events and notifications | Publishers wait for RabbitMQ confirmation. Notification and audit consumers persist records and deduplicate by event ID. The secured portal exposes owned notification history and live updates. SMTP messages go to local Mailpit. | [OutboxPublisher.cs](../src/FinFlow.Ledger.Api/OutboxPublisher.cs), [NotificationConsumer.cs](../src/FinFlow.Notifications.Worker/NotificationConsumer.cs), [AuditConsumer.cs](../src/FinFlow.Audit.Api/AuditConsumer.cs) |
| Portal and operations | Angular provides customer, staff, and audit views. The optional observability stack connects OpenTelemetry, Grafana, Prometheus, Loki, and Tempo. | [Portal component](../frontend/src/portal.component.ts), [telemetry registration](../src/FinFlow.Observability/FinFlowTelemetry.cs), [observability Compose overlay](../compose.observability.yaml) |

The [source walkthrough](CURRENT_IMPLEMENTATION.md) has diagrams and explains the original transfer path in more detail. It is a snapshot from before Payments and the later notification and deployment work.

## What was actually tested

| Check | Result obtained |
| --- | --- |
| .NET Release build | The last recorded build passed with zero warnings and errors. |
| xUnit with PostgreSQL and RabbitMQ | **9 of 9 passed** at the Stage 3 checkpoint. Coverage included balance and posting checks, immutable entries, concurrent debits, idempotent retries, broker outage recovery, message redelivery, and malformed audit messages. |
| Later Release test run | **7 passed and 2 failed** during the interrupted Docker run. The two failures were Docker-backed delivery tests. A clean serial rerun remains open. |
| Secured Compose smoke | Passed Keycloak sign-in and role checks, profile and KYC, account ownership, concurrent transfer requests, payment settlement, uncertain outcome recovery, declined-payment compensation, concurrent refunds, lifecycle audit, SignalR, and notification read state. Mailpit captured four messages in the recorded run. |
| Angular browser smoke | Passed customer, staff, and mobile audit navigation and customer logout. This run predates the Angular security patch. The patched dependency lockfile is in place. Its build and audit remain open. |
| Observability and benchmarks | Verified a transfer trace across Gateway, Transfers, Ledger, Audit, and Notifications. Verified Loki streams and the ledger metric. k6 recorded 1,404 gateway liveness requests, zero failures, and 5.9 ms p95. [Benchmark details](benchmarks/results.md) |
| Aspire, kind, and Woodpecker | Aspire started its dashboard. kind started its control plane. Aspire gateway readiness and Kubernetes workloads still need validation. Woodpecker has a workflow file but no connected runner here. |

A Valkey outage check and the final implementation guide also remain open. See [PROJECT_STATUS.md](../PROJECT_STATUS.md) for the live checklist.

## What you need

| Check | Required tools |
| --- | --- |
| Basic ledger demo | Docker Engine in Linux-container mode, Docker Compose v2, and PowerShell or an HTTP client. |
| .NET build and integration tests | .NET 10 SDK. The integration tests also need Docker because Testcontainers starts PostgreSQL and RabbitMQ. |
| Secured portal | Docker Compose v2 and a browser. Node.js 22.12+ or 24+ is needed for host-side smoke scripts and frontend builds. |
| Optional deployment and browser automation | See [DEPLOYMENT.md](DEPLOYMENT.md) for Aspire and kind. The browser script uses Playwright with a supported browser. |

Run commands from the repository root. On this laptop, run one stack or test suite at a time. Values below use the default local ports and synthetic credentials.

## Test the ledger with two services

Start PostgreSQL and Ledger only. Give the API a moment to apply migrations. If the first read fails to connect, check `docker compose logs ledger` and retry.

```powershell
docker compose up -d --build ledger
$base = 'http://127.0.0.1:8080'
Invoke-RestMethod "$base/accounts" | Format-Table name, balanceMinor

$headers = @{ 'Idempotency-Key' = "handoff-$([guid]::NewGuid())" }
$body = @{
    sourceAccountId = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'
    destinationAccountId = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb'
    amountMinor = 1234
} | ConvertTo-Json

$first = Invoke-RestMethod "$base/transfers" -Method Post -Headers $headers -ContentType 'application/json' -Body $body
$retry = Invoke-RestMethod "$base/transfers" -Method Post -Headers $headers -ContentType 'application/json' -Body $body
if ($first.id -ne $retry.id) { throw 'Retry returned a different transfer ID.' }
Invoke-RestMethod "$base/transfers/$($first.id)"
Invoke-RestMethod "$base/accounts" | Format-Table name, balanceMinor
```

On fresh data, balances move from **100,000 / 50,000** to **98,766 / 51,234** tetri after two submissions with the same key. On existing data, expect one debit and credit of 1,234 tetri from the balances you started with. The event remains pending while RabbitMQ is off.

To check asynchronous delivery, add just the broker and worker. Run the query again after a few seconds if the matching notification has yet to arrive.

```powershell
docker compose up -d --build rabbitmq notifications
$sql = 'SELECT "TransferId", "AmountMinor", "Currency" FROM "Notifications" ORDER BY "CreatedAt" DESC LIMIT 5;'
$sql | docker compose exec -T postgres psql -U finflow -d finflow_notifications
docker compose stop
```

Match the notification’s `TransferId` to `$first.id`. The worker persists the notification in its own database. `docker compose stop` keeps your data.

## Run automated checks

After the demo stack is stopped, run the backend tests. Use the focused command by itself if you only need to check messaging.

```powershell
dotnet build -c Release
dotnet test -c Release --no-build

# Focused alternative to the full test command
dotnet test -c Release --no-build --filter FullyQualifiedName~DeliveryTests
```

The patched frontend build and audit can run with Docker off.

```powershell
npm ci --prefix frontend
npm audit --prefix frontend --omit=dev --audit-level=high
npm run build --prefix frontend
```

## Test the secured portal when capacity allows

Use the secured stack on its own. Leave the observability overlay, Aspire, and kind off for this check.

```powershell
docker compose -p finflow-secure -f compose.yaml -f compose.secure.yaml up -d --build --wait
node --env-file-if-exists=.env tests/smoke-secure.mjs
```

Open `http://localhost:4200`. Sign in as `customer` to register a profile. Sign in as `operator` to verify the profile, open an account for its subject, and fund it. Then sign back in as the customer to transfer money, simulate a payment, or request a refund. Sign in as `auditor` to review workflow events. The default synthetic password is `finflow-local-demo`. Mailpit is at `http://localhost:8025`.

Stop this stack when finished:

```powershell
docker compose -p finflow-secure -f compose.yaml -f compose.secure.yaml stop
```

Keep the optional telemetry and deployment checks for a separate session. The [README](../README.md) and [deployment guide](DEPLOYMENT.md) contain those commands.
