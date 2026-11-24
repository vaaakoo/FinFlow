# FinFlow project status

## Stages 1 and 2 — Foundation and core banking

The v0.1 demo already supports seeded GEL accounts, atomic and idempotent transfers, signed ledger entries, and confirmed RabbitMQ delivery with a deduplicating notification worker. The demo retains its two service-owned databases and original Compose entry point.

Stage 1 added central NuGet and compiler settings, a Keycloak realm with four synthetic roles, and secured Compose mode. YARP authenticates and rate-limits requests. Each secured HTTP API validates bearer tokens. The ledger, customer, and transfer APIs have no published host ports in this mode.

Stage 2 added Customer and Transfer APIs with separate databases, profile registration, simulated KYC, staff account provisioning, and ownership checks on statements and transfers. Ledger posting is immutable and constrained at commit. A transfer worker submits durable orders to the Ledger through authenticated gRPC and retries the same operation ID. The ledger provides paginated statements and reconciliation.

## Stage 3 — Distributed workflows in progress

Transfer events publish to the durable RabbitMQ topic exchange using the versioned route `ledger.transfer-completed.v1`. The original notification queue is bound to this route so pending and existing messages remain consumable. New events include correlation and causation identifiers.

### Validation

- Release build passed with zero warnings and errors. All eight PostgreSQL and RabbitMQ integration tests passed after the Stage 3 messaging change, including routing, correlation, broker recovery, and deduplication.
- A fresh secured Compose project passed PKCE sign-in, 401/403 role checks, customer registration and KYC, account ownership, a customer transfer, concurrent idempotent submits, foreign-account rejection, and transfer history. A separate direct ledger probe returned 401 without a token.
- The secured smoke test stopped the Ledger, submitted an order, observed it remain pending, restarted the Ledger, and verified the order completed with one debit.
- NuGet reported no vulnerable direct or transitive packages from the configured sources at the time of the check.

### Remaining work

- Stage 3 still needs Customer and Transfer lifecycle events beyond ledger completion, individual service subscriptions and dead-letter handling, Audit storage and queries, the Payment Saga with simulated outcomes and compensating ledger entries, Valkey, Mailpit, and notification history and realtime delivery.
- Stages 4–6 still need both Angular portals, observability, measured performance results, Aspire, local Kubernetes validation, Woodpecker CI, and release preparation. `kubectl` is available and has no current context. See [PROJECT_PLAN.md](PROJECT_PLAN.md).

