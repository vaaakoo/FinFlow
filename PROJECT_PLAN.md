# FinFlow development roadmap

## 1. Foundation, identity, and gateway

- **Objective** — Preserve the executable v0.1 baseline and add a secured edge.
- **Tasks** — Centralize build and NuGet settings, establish persistent repo instructions, configure Keycloak realm and roles, add YARP, enforce JWT at the ledger, limit requests, and add health and correlation. Keep the existing local demo available as a separate mode.
- **Dependencies** — Existing ledger and worker, .NET 10, Docker Compose, and verified package licenses.
- **Deliverables** — Authenticated Compose mode, reproducible realm import, gateway, and build conventions.
- **Acceptance** — The existing suite passes. A missing or invalid token is rejected at both HTTP boundaries, staff can reach the ledger through the gateway, and customers cannot use staff routes. The secured stack publishes no direct ledger port.
- **Repository changes** — Gateway and shared authentication projects, Compose overlay, realm import, build props, and project documentation.

## 2. Core banking

- **Objective** — Introduce independent customer and transfer services while preserving ledger invariants.
- **Tasks** — Implement profile registration, status, and simulated KYC. Associate accounts with identity subjects. Add immutable posting, statements, and reconciliation. Split transfer lifecycle into its own database and use an idempotent gRPC posting contract. Resolve uncertain outcomes by retrying the same operation ID. Use separate commands and paginated Dapper queries where justified.
- **Dependencies** — Stage 1 identity, gateway, and PostgreSQL conventions.
- **Deliverables** — Customer and Transfer APIs, migrations, Ledger posting contract, ownership policies, and gateway routes.
- **Acceptance** — An authenticated customer can submit and track their own transfer. Cross-customer access fails. Timeouts and concurrent retries preserve one balanced posting.
- **Repository changes** — Customer and Transfer service projects, Ledger migrations and gRPC contracts, authorization integration tests, and transfer diagrams.

## 3. Distributed workflows

- **Objective** — Add simulated payments, recoverable event delivery, notification features, and audit.
- **Tasks** — Introduce versioned RabbitMQ exchanges, per-service queues, correlation and causation, transactional outbox and inbox, bounded consumer retry and dead letters. Publish customer and transfer lifecycle events. Implement a Payment Saga, simulated provider outcomes, Ledger compensation, refunds, and a limited event-sourced payment lifecycle. Use Polly backoff and circuit breaking. Add Valkey cache-aside for customer reads, Mailpit email, SignalR, notification history and read state, and redacted append-only audit records.
- **Dependencies** — Stage 2 ownership and idempotent ledger posting.
- **Deliverables** — Payment and Audit services, expanded Notification service, durable event topology, and infrastructure profiles.
- **Acceptance** — Provider scenarios, duplicate deliveries, and outages converge without duplicate financial effects. Notifications and audit records persist once per event. Cache outages preserve correctness.
- **Repository changes** — Payment, Audit, Notification, messaging contracts, Valkey and Mailpit configuration, Saga diagram, and integration tests.

## 4. Customer and administration portals

- **Objective** — Deliver usable Angular interfaces for both audiences.
- **Tasks** — Build responsive dashboards, PKCE sign-in and logout, role-based navigation, customer profile, balances, statements, transfers, payment history, and realtime notifications. Add staff views for KYC, accounts, operations, reconciliation, audit, filtering, sorting, and pagination.
- **Dependencies** — Stages 1–3 API contracts and secured demo users.
- **Deliverables** — Angular and TypeScript frontend, gateway integration, and screenshots.
- **Acceptance** — Both portals complete their main workflows, enforce role-based access, and handle loading, empty, and error states.
- **Repository changes** — Frontend, UI tests, screenshots, and demo instructions.

## 5. Observability and performance

- **Objective** — Make the distributed workflows diagnosable and measure performance.
- **Tasks** — Add Serilog and OpenTelemetry across HTTP, gRPC, database, and broker boundaries. Configure Prometheus, Grafana, Loki, and Tempo. Measure selected workloads with BenchmarkDotNet and k6. Tune based on recorded evidence and review readiness and liveness.
- **Dependencies** — Running workflows and baseline tests from Stages 1–4.
- **Deliverables** — Telemetry configuration, dashboards, benchmark harnesses, and measured results with environment details.
- **Acceptance** — Trace a transfer across services, view operational metrics and logs, and reproduce documented measurements without exposing secrets.
- **Repository changes** — Service defaults, observability configurations, performance projects, and benchmark reports.

## 6. Deployment and release preparation

- **Objective** — Validate repeatable deployment and prepare the repository for a portfolio release.
- **Tasks** — Complete Aspire orchestration, Kubernetes manifests and local kind or K3s validation, Woodpecker checks, full integration, dependency and license review, architectural decisions, and release documentation.
- **Dependencies** — Stages 1–5 and an available local Kubernetes runtime.
- **Deliverables** — Aspire AppHost, Kubernetes configuration, CI pipeline, and release checklist.
- **Acceptance** — All mandatory workflows pass, clean local startup works, deployment and performance evidence are recorded, and any external setup is explicitly documented.
- **Repository changes** — AppHost, infrastructure manifests, CI, architecture docs, README, and final project status.

