# FinFlow project status

## Stages 1 and 2 — Foundation and core banking

The v0.1 demo already supports seeded GEL accounts, atomic and idempotent transfers, signed ledger entries, and confirmed RabbitMQ delivery with a deduplicating notification worker. The demo retains its two service-owned databases and original Compose entry point.

Stage 1 added central NuGet and compiler settings, a Keycloak realm with four synthetic roles, and secured Compose mode. YARP authenticates and rate-limits requests. Each secured HTTP API validates bearer tokens. The ledger, customer, and transfer APIs have no published host ports in this mode.

Stage 2 added Customer and Transfer APIs with separate databases, profile registration, simulated KYC, staff account provisioning, and ownership checks on statements and transfers. Ledger posting is immutable and constrained at commit. A transfer worker submits durable orders to the Ledger through authenticated gRPC and retries the same operation ID. The ledger provides paginated statements and reconciliation.

## Stage 3 — Distributed workflows

Transfer events publish to the durable RabbitMQ topic exchange using the versioned route `ledger.transfer-completed.v1`. The original notification queue is bound to this route so pending and existing messages remain consumable. New events include correlation and causation identifiers.

The existing services were reviewed for readability. Workers and EF Core contexts use conventional dependency-injection constructors, ledger startup maps named endpoint handlers, and common endpoint branches use explicit control flow. The permanent standards and final implementation-guide requirement are in `AGENTS.md`.

The notification worker preserves malformed deliveries in a confirmed dead-letter queue. The new Audit API has a separate database and consumes transfer-completed events using its own durable subscription. It stores append-only, deduplicated records with a minimal payload. Auditors and administrators can query records through the secured gateway.

The Payments API owns a durable Saga and a versioned state history. Ledger posts charges into a protected settlement account and reverses them with separate idempotent postings. Polly handles brief RPC failures while the persisted schedule covers longer outages. Customer, transfer, and payment changes emit lifecycle events from their own outboxes into an audited quorum queue with a bounded redelivery policy. The secured stack provides Valkey cache-aside for customer profiles and owned notification history, read state, SignalR delivery, and synthetic email via Mailpit.

### Validation

- Release build passed with zero warnings and errors. All eight PostgreSQL and RabbitMQ integration tests passed after the Stage 3 messaging change, including routing, correlation, broker recovery, and deduplication.
- A fresh secured Compose project passed PKCE sign-in, 401/403 role checks, customer registration and KYC, account ownership, a customer transfer, concurrent idempotent submits, foreign-account rejection, and transfer history. A separate direct ledger probe returned 401 without a token.
- The secured smoke test stopped the Ledger, submitted an order, observed it remain pending, restarted the Ledger, and verified the order completed with one debit.
- After the readability refactor, all eight integration tests passed. The focused RabbitMQ test also passed with the malformed-delivery dead-letter assertion. Secured Compose smoke checks passed for transfer audit fan-out, auditor access, rejection of non-audit roles, and the minimal payload. PostgreSQL rejected an update to a stored audit row.
- NuGet reported no vulnerable direct or transitive packages from the configured sources at the time of the check.

### Walkthrough and audit delivery

- [Current implementation walkthrough](docs/CURRENT_IMPLEMENTATION.md) records the source layout and transfer flow before the audit delivery change below.
- Audit now preserves malformed transfer events in `finflow.audit.dead-letter`. It confirms the copy before acknowledging the source delivery. The focused Release delivery suite passed both tests, including a malformed audit event followed by a valid event.
- The Stage 3 Release suite passed 9 of 9 tests. Secured Compose smoke verified simulated settlement, concurrent submissions and refunds, uncertain outcomes, compensating a declined charge, lifecycle audit, live notification delivery, and marking notifications read. Mailpit captured four synthetic messages.

## Stages 4–6 — Integration and release validation

- Stage 4 adds the Angular customer and staff portals with Keycloak PKCE, role-specific navigation, account administration, transfers and payments, operational history, audit, and realtime notifications. The original production build and browser smoke checks passed. Repeat the frontend build and audit after the Angular security patch.
- Stage 5 adds OpenTelemetry with Serilog and an optional Collector, Prometheus, Grafana, Loki, and Tempo Compose overlay. Grafana queries verified a transfer trace across Gateway, Transfers, Ledger, Audit, and Notifications, as well as Loki streams and the ledger posting metric. BenchmarkDotNet and k6 measurements are recorded in [results.md](docs/benchmarks/results.md).
- Stage 6 adds an Aspire AppHost, a local Kubernetes manifest renderer, and a Woodpecker pipeline. Aspire built and started its dashboard. The kind cluster started; workload validation stopped when the local engine stopped its containers. The final Release build was clean, but two Docker-backed tests failed during that interruption. Woodpecker has no connected runner here.
- Verification was paused after laptop resource pressure. No verification containers are running. Resume by running one stack at a time. Remaining checks include a serial Release test run, patched Angular build and audit, Valkey outage, Aspire gateway readiness, Kubernetes workload readiness and smoke checks, and writing the final implementation guide after validation.

