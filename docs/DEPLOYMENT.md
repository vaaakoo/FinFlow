# Local deployment

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
