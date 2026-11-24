import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../../', import.meta.url));
const namespace = 'finflow';
const includeTelemetry = process.argv.includes('--observability');
const compose = ['compose', '--env-file', '.env.example', '-p', 'finflow-local',
    '-f', 'compose.yaml', '-f', 'compose.secure.yaml'];
if (includeTelemetry) compose.push('-f', 'compose.observability.yaml');

const secrets = {
    POSTGRES_PASSWORD: ['FINFLOW_POSTGRES_PASSWORD', 'demo-postgres'],
    RABBITMQ_DEFAULT_PASS: ['FINFLOW_RABBITMQ_PASSWORD', 'demo-rabbitmq'],
    FINFLOW_DEMO_PASSWORD: ['FINFLOW_DEMO_PASSWORD', 'finflow-local-demo'],
    FINFLOW_TRANSFER_CLIENT_SECRET: ['FINFLOW_TRANSFER_CLIENT_SECRET', 'finflow-local-transfer-client'],
    FINFLOW_PAYMENT_CLIENT_SECRET: ['FINFLOW_PAYMENT_CLIENT_SECRET', 'finflow-local-payment-client']
};
const environment = { ...process.env, FINFLOW_IDENTITY_PORT: '8082' };
const secretData = {};
for (const [key, [variable, demoValue]] of Object.entries(secrets)) {
    environment[variable] = 'FINFLOW_KUBERNETES_' + key;
    secretData[key] = process.env[variable] || demoValue;
}

const config = JSON.parse(execFileSync('docker', [...compose, 'config', '--format', 'json'], {
    cwd: root, env: environment, encoding: 'utf8'
}));
const resources = [{ apiVersion: 'v1', kind: 'Namespace', metadata: { name: namespace } }];
const ports = {
    postgres: { postgres: 5432 }, rabbitmq: { amqp: 5672 }, valkey: { redis: 6379 },
    mailpit: { http: 8025, smtp: 1025 }, ledger: { http: 8080, grpc: 8081 },
    collector: { otlp: 4317, metrics: 8889 }, prometheus: { http: 9090 }, loki: { http: 3100 },
    tempo: { http: 3200, otlp: 4317 }, grafana: { http: 3000 }
};
const persistent = {
    postgres: ['/var/lib/postgresql/data', '2Gi'],
    rabbitmq: ['/var/lib/rabbitmq', '1Gi']
};
const applicationNames = new Set(['ledger', 'gateway', 'customers', 'transfers', 'payments', 'notifications', 'audit']);
const files = {
    postgres: [['docker/init-databases.sql', '/docker-entrypoint-initdb.d/10-notifications.sql']],
    keycloak: [['infrastructure/keycloak/finflow-realm.json', '/opt/keycloak/data/import/finflow-realm.json']],
    collector: [['infrastructure/observability/collector.yaml', '/etc/otelcol/config.yaml']],
    prometheus: [['infrastructure/observability/prometheus.yaml', '/etc/prometheus/prometheus.yml']],
    loki: [['infrastructure/observability/loki.yaml', '/etc/loki/finflow.yaml']],
    tempo: [['infrastructure/observability/tempo.yaml', '/etc/tempo/finflow.yaml']],
    grafana: [
        ['infrastructure/observability/grafana/datasources.yaml', '/etc/grafana/provisioning/datasources/finflow.yaml'],
        ['infrastructure/observability/grafana/dashboards.yaml', '/etc/grafana/provisioning/dashboards/finflow.yaml'],
        ['infrastructure/observability/grafana/overview.json', '/var/lib/grafana/dashboards/overview.json']
    ]
};
function resource(apiVersion, kind, name, properties) {
    resources.push({ apiVersion, kind, metadata: { name, namespace }, ...properties });
}
resource('v1', 'Secret', 'finflow-environment', { type: 'Opaque', stringData: secretData });

for (const [name, service] of Object.entries(config.services)) {
    const labels = { app: name };
    const servicePorts = Object.entries(ports[name] ?? { http: 8080 })
        .map(([label, port]) => ({ name: label, port, targetPort: port }));
    const env = [];
    const configured = { ...service.environment };
    if (name === 'postgres') configured.PGDATA = '/var/lib/postgresql/data/pgdata';
    for (const key of Object.keys(secrets)) {
        const marker = 'FINFLOW_KUBERNETES_' + key;
        if (Object.values(configured).some(value => String(value).includes(marker))) {
            env.push({ name: key, valueFrom: { secretKeyRef: { name: 'finflow-environment', key } } });
        }
    }
    for (const [key, value] of Object.entries(configured)) {
        if (env.some(item => item.name === key)) continue;
        env.push({ name: key, value: String(value ?? '').replace(/FINFLOW_KUBERNETES_(w+)/g, (_, secret) => '$(' + secret + ')') });
    }

    const volumes = [];
    const volumeMounts = [];
    const configFiles = files[name] ?? [];
    if (configFiles.length) {
        const data = {};
        for (const [index, [source, target]] of configFiles.entries()) {
            const key = 'file-' + index;
            data[key] = readFileSync(new URL(source, new URL('../../', import.meta.url)), 'utf8');
            volumeMounts.push({ name: 'configuration', mountPath: target, subPath: key, readOnly: true });
        }
        resource('v1', 'ConfigMap', name + '-files', { data });
        volumes.push({ name: 'configuration', configMap: { name: name + '-files' } });
    }
    if (persistent[name]) {
        const [path, storage] = persistent[name];
        resource('v1', 'PersistentVolumeClaim', name + '-data', {
            spec: { accessModes: ['ReadWriteOnce'], resources: { requests: { storage } } }
        });
        volumes.push({ name: 'data', persistentVolumeClaim: { claimName: name + '-data' } });
        volumeMounts.push({ name: 'data', mountPath: path });
    }

    const container = {
        name,
        image: service.build ? 'finflow/' + name + ':local' : service.image,
        imagePullPolicy: 'IfNotPresent',
        ports: servicePorts.map(port => ({ name: port.name, containerPort: port.port })),
        env, volumeMounts,
        resources: { requests: { cpu: '50m', memory: '96Mi' }, limits: { memory: name === 'keycloak' ? '1Gi' : '768Mi' } }
    };
    if (service.command?.length) container.args = service.command;
    const health = applicationNames.has(name)
        ? { httpGet: { path: '/health/live', port: 8080 } }
        : { tcpSocket: { port: servicePorts[0].port } };
    container.startupProbe = { ...health, periodSeconds: 2, failureThreshold: 90 };
    const ready = applicationNames.has(name) ? { httpGet: { path: '/health/ready', port: 8080 } } : health;
    container.readinessProbe = { ...ready, periodSeconds: 3 };
    container.livenessProbe = { ...health, periodSeconds: 15, failureThreshold: 6 };
    const pod = { containers: [container], volumes };
    if (name === 'rabbitmq') pod.hostname = 'rabbitmq';
    if (applicationNames.has(name) && name !== 'gateway') {
        pod.initContainers = [{
            name: 'wait-for-postgres', image: 'postgres:17-alpine',
            command: ['sh', '-ec', 'until pg_isready -h postgres -U finflow; do sleep 2; done']
        }];
    }

    resource('apps/v1', 'Deployment', name, {
        spec: {
            replicas: 1,
            ...(persistent[name] ? { strategy: { type: 'Recreate' } } : {}),
            selector: { matchLabels: labels },
            template: { metadata: { labels }, spec: pod }
        }
    });
    resource('v1', 'Service', name, { spec: { selector: labels, ports: servicePorts } });
}
process.stdout.write(JSON.stringify({ apiVersion: 'v1', kind: 'List', items: resources }) + '\n');
