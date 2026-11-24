# Local deployment

On a resource-constrained laptop, treat Compose, Aspire, kind, and Testcontainers as separate verification sessions. Stop the current stack and check `docker ps` before switching. Run the frontend build and dependency audits with Docker stopped. The [low-resource handoff](DEVELOPMENT_HANDOFF.md) starts with just Ledger and PostgreSQL. Use the full stack only after confirming the machine can handle it and obtaining approval.

## Aspire

With .NET 10 and Docker running, start the backend and its development dependencies:

```sh
dotnet run --project src/FinFlow.AppHost
```

Open the dashboard address printed by Aspire. The gateway uses loopback port 8081. Keycloak uses 8082 and Mailpit uses 8025. The AppHost sets the service URLs and supplies the synthetic credentials. The Angular portal can run separately with `npm ci --prefix frontend` and `npm start --prefix frontend`. Stop the similarly configured Compose stack first if it is already using these ports.

## Kubernetes on kind

Run from the repository root with Docker Engine, Node.js, kubectl, and kind available. Use a dedicated local context. The renderer reads the Compose service definitions and emits a Kubernetes List with Deployments, ClusterIP Services, ConfigMaps, a synthetic Secret, and PVCs for PostgreSQL and RabbitMQ.

The following PowerShell example builds and loads local images.

```powershell
kind create cluster --name finflow-local
docker compose -p finflow-local -f compose.yaml -f compose.secure.yaml build

$images = foreach ($name in 'gateway','customers','ledger','transfers','payments','notifications','audit','portal') {
    docker tag "finflow-local-${name}:latest" "finflow/${name}:local"
    "finflow/${name}:local"
}
kind load docker-image --name finflow-local @images
node infrastructure/kubernetes/render.mjs | kubectl --context kind-finflow-local apply -f -
kubectl --context kind-finflow-local -n finflow wait --for=condition=Available deployment --all --timeout=240s
```

Run these in separate terminals to access browser sign-in and the portal. Gateway traffic from the portal is forwarded inside the cluster.

```sh
kubectl --context kind-finflow-local -n finflow port-forward svc/keycloak 8082:8080
kubectl --context kind-finflow-local -n finflow port-forward svc/portal 4200:8080
```

Open `http://localhost:4200`. To include observability in the manifests, add `--observability` to the renderer command. The render uses synthetic defaults unless environment variables override them. Pass output directly to kubectl when using overrides, since the generated Secret contains their values.

## Woodpecker

The [workflow](../.woodpecker.yaml) restores and audits NuGet dependencies, builds and tests .NET, audits and builds Angular, renders manifests, and builds the local images. Run it on a trusted Linux Docker agent with the socket volume allowed. The workflow is restricted to push and manual events because access to the agent's Docker socket conveys host-level privileges. A Woodpecker server and repository connection are required to execute the workflow. Registry publication and external deployment are outside this workflow.

## Release checks

- Review current dependency and container advisories before publishing an artifact.
- Use the [secured smoke script](../tests/smoke-secure.mjs) for authenticated business flows and the [browser smoke script](../frontend/smoke-browser.mjs) for role-specific navigation.
- Confirm the local gateway, portal, and database-backed readiness endpoints before recording deployment as verified.
- Supply deployment-specific TLS, secrets, and storage before any environment shared with other users.

### Low resource commands

With Docker stopped, build the frontend and audit current dependencies:

```powershell
npm ci --prefix frontend
npm audit --prefix frontend --omit=dev --audit-level=high
npm run build --prefix frontend
dotnet restore -p:NuGetAuditMode=all -warnaserror:NU1901,NU1902,NU1903,NU1904 -m:1
dotnet build -c Release --no-restore -m:1
```

Start the Docker engine by itself for the first focused test. Check `docker ps` after starting it. A previous kind node can restart with Docker, so stop it before running tests if it appears. Stop Docker and confirm cleanup before restarting it for the second test. Stop it again after the second test.

```powershell
dotnet test -c Release --no-build --filter 'FullyQualifiedName~Audit_preserves_malformed_deliveries_and_processes_valid_events'
```

```powershell
dotnet test -c Release --no-build --filter 'FullyQualifiedName~Broker_outage_preserves_transfer_and_recovery_handles_a_real_redelivery'
```

## Validation limits

The earlier Aspire check reached its dashboard. Gateway readiness and the secured workflow under Aspire remain open. The earlier kind check started the control plane, followed by a Docker interruption before the workloads could be validated. The Woodpecker workflow requires a connected trusted runner. A dedicated Valkey outage check and a demonstration of the Polly circuit breaker opening also remain open. See the [implementation guide](IMPLEMENTATION_GUIDE.md) for the code paths already in place.
