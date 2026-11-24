import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createHash, randomBytes } from 'node:crypto';
import { setTimeout as delay } from 'node:timers/promises';

const identity = 'http://localhost:' + (process.env.FINFLOW_IDENTITY_PORT ?? 8082);
const gateway = 'http://127.0.0.1:' + (process.env.FINFLOW_GATEWAY_PORT ?? 8081);
const password = process.env.FINFLOW_DEMO_PASSWORD ?? 'finflow-local-demo';
const realm = identity + '/realms/finflow';
const redirectUri = 'http://localhost:4200/auth/callback';
const clientId = 'finflow-portal';

async function waitFor(url) {
    for (let attempt = 0; attempt < 60; attempt++) {
        const response = await fetch(url, { signal: AbortSignal.timeout(2000) }).catch(() => null);
        if (response?.ok) return;
        await delay(1000);
    }
    throw new Error('Service did not become ready: ' + url);
}

async function signIn(username) {
    const verifier = randomBytes(32).toString('base64url');
    const challenge = createHash('sha256').update(verifier).digest('base64url');
    const state = randomBytes(16).toString('hex');
    const authorization = new URL(realm + '/protocol/openid-connect/auth');
    authorization.search = new URLSearchParams({
        client_id: clientId, redirect_uri: redirectUri, response_type: 'code',
        scope: 'openid', code_challenge: challenge, code_challenge_method: 'S256', state
    });
    const form = await fetch(authorization);
    assert.equal(form.status, 200);
    const cookies = form.headers.getSetCookie().map(cookie => cookie.split(';', 1)[0]).join('; ');
    const action = (await form.text()).match(/id="kc-form-login"[^>]*action="([^"]+)"/)?.[1];
    assert.ok(action, 'Keycloak did not render a login form.');
    const signInUrl = new URL(action.replaceAll('&amp;', '&'), identity);
    assert.equal(signInUrl.origin, identity);
    const login = await fetch(signInUrl, {
        method: 'POST', redirect: 'manual', headers: { cookie: cookies },
        body: new URLSearchParams({ username, password, credentialId: '' })
    });
    assert.equal(login.status, 302, 'Synthetic user did not complete login.');
    const callback = new URL(login.headers.get('location'));
    assert.equal(callback.origin + callback.pathname, redirectUri);
    assert.equal(callback.searchParams.get('state'), state);
    const code = callback.searchParams.get('code');
    assert.ok(code, 'Keycloak did not return an authorization code.');
    const exchange = await fetch(realm + '/protocol/openid-connect/token', {
        method: 'POST',
        body: new URLSearchParams({
            grant_type: 'authorization_code', client_id: clientId, redirect_uri: redirectUri,
            code, code_verifier: verifier
        })
    });
    assert.equal(exchange.status, 200);
    const { access_token: token } = await exchange.json();
    assert.ok(token);
    return token;
}

await waitFor(realm + '/.well-known/openid-configuration');
await waitFor(gateway + '/health/live');
const accounts = gateway + '/accounts';
assert.equal((await fetch(accounts)).status, 401);
assert.equal((await fetch(accounts, { headers: { authorization: 'Bearer invalid' } })).status, 401);
const customer = await signIn('customer');
assert.equal((await fetch(accounts, { headers: { authorization: 'Bearer ' + customer } })).status, 403);

const operator = await signIn('operator');
const response = await fetch(accounts, { headers: { authorization: 'Bearer ' + operator } });
assert.equal(response.status, 200);
assert.match(response.headers.get('x-correlation-id') ?? '', /^[a-f0-9]{32}$/);
const ledgerAccounts = await response.json();
const seedAccount = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
assert.ok(ledgerAccounts.some(account => account.id === seedAccount));
console.log('PKCE login, gateway 401/403, and an authenticated ledger query passed.');

function request(path, token, method = 'GET', data) {
    const headers = { authorization: 'Bearer ' + token };
    if (data) headers['content-type'] = 'application/json';
    return fetch(gateway + path, { headers, method, body: data && JSON.stringify(data) });
}

const registration = await request('/customers/me', customer, 'POST', { displayName: 'Synthetic Customer' });
assert.ok([201, 409].includes(registration.status), 'Customer registration failed.');
assert.equal((await request('/customers/me', customer, 'POST', { displayName: 'Duplicate' })).status, 409);
assert.equal((await request('/staff/customers', customer)).status, 403);
const update = await request('/customers/me', customer, 'PUT', { displayName: 'Updated Customer' });
assert.equal(update.status, 200);
const ownProfile = await request('/customers/me', customer);
assert.equal(ownProfile.status, 200);
const { subject, displayName } = await ownProfile.json();
assert.equal(displayName, 'Updated Customer');
const staffList = await request('/staff/customers?size=1', operator);
assert.equal(staffList.status, 200);
assert.ok((await staffList.json()).items.length === 1);
const verification = await request('/staff/customers/' + encodeURIComponent(subject) + '/status', operator,
    'PUT', { kyc: 'Verified', active: true });
assert.equal(verification.status, 200);
assert.equal((await (await request('/customers/me', customer)).json()).kyc, 'Verified');
console.log('Customer registration, duplicate rejection, profile update, staff listing, and KYC passed.');

const accountInput = { name: 'Synthetic customer account', ownerSubject: subject };
assert.equal((await request('/staff/ledger/accounts', customer, 'POST', accountInput)).status, 403);
const opened = await request('/staff/ledger/accounts', operator, 'POST', accountInput);
assert.equal(opened.status, 201);
const account = await opened.json();
assert.equal(account.balanceMinor, 0);
const ownedAccounts = await request('/customers/me/accounts', customer);
assert.equal(ownedAccounts.status, 200);
const owned = await ownedAccounts.json();
assert.ok(owned.some(item => item.id === account.id));
assert.ok(!owned.some(item => item.id === seedAccount));
assert.equal((await request('/customers/me/accounts/' + seedAccount + '/statement', customer)).status, 404);
const ownedStatement = await request('/customers/me/accounts/' + account.id + '/statement', customer);
assert.equal(ownedStatement.status, 200);
assert.deepEqual((await ownedStatement.json()).items, []);
console.log('Staff provisioning and customer account-ownership checks passed.');

const funding = await fetch(gateway + '/transfers', {
    method: 'POST',
    headers: { authorization: 'Bearer ' + operator, 'content-type': 'application/json',
        'idempotency-key': 'smoke-fund-' + account.id },
    body: JSON.stringify({ sourceAccountId: seedAccount, destinationAccountId: account.id, amountMinor: 500 })
});
assert.equal(funding.status, 201);

const transferPath = '/customers/me/transfers';
const transferKey = 'smoke-transfer-' + account.id;
const transferBody = { sourceAccountId: account.id, destinationAccountId: seedAccount, amountMinor: 150 };
async function submitTransfer(key, body) {
    return fetch(gateway + transferPath, {
        method: 'POST',
        headers: { authorization: 'Bearer ' + customer, 'content-type': 'application/json', 'idempotency-key': key },
        body: JSON.stringify(body)
    });
}
async function awaitOrder(id) {
    for (let attempt = 0; attempt < 25; attempt++) {
        const result = await request(transferPath + '/' + id, customer);
        assert.equal(result.status, 200);
        const order = await result.json();
        if (order.status !== 'Pending') return order;
        await delay(800);
    }
    throw new Error('Customer transfer did not leave Pending: ' + id);
}

const submitted = await Promise.all([
    submitTransfer(transferKey, transferBody), submitTransfer(transferKey, transferBody)
]);
assert.ok(submitted.every(result => result.status === 202));
const [order, replay] = await Promise.all(submitted.map(result => result.json()));
assert.equal(replay.id, order.id);
assert.equal((await submitTransfer(transferKey, { ...transferBody, amountMinor: 151 })).status, 409);
const completed = await awaitOrder(order.id);
assert.equal(completed.status, 'Completed', completed.rejection ?? undefined);
assert.ok(completed.postingId);

const foreign = await submitTransfer(transferKey + '-foreign', {
    sourceAccountId: seedAccount, destinationAccountId: account.id, amountMinor: 100
});
assert.equal(foreign.status, 202);
assert.equal((await awaitOrder((await foreign.json()).id)).status, 'Rejected');
const history = await request(transferPath + '?size=1', customer);
assert.equal(history.status, 200);
const page = await history.json();
assert.ok(page.next);
assert.equal((await request(transferPath + '?size=1&before=' + page.next, customer)).status, 200);
const balances = await (await request('/customers/me/accounts', customer)).json();
assert.equal(balances.find(item => item.id === account.id).balanceMinor, 350);
console.log('Customer transfer, idempotent retry, foreign-account rejection, and transfer history passed.');

if (process.env.FINFLOW_SMOKE_COMPOSE_PROJECT) {
    const project = process.env.FINFLOW_SMOKE_COMPOSE_PROJECT;
    function changeLedgerState(command) {
        execFileSync('docker', ['compose', '-p', project, '-f', 'compose.yaml', '-f', 'compose.secure.yaml',
            command, 'ledger'], { stdio: 'inherit' });
    }

    let recoveryId;
    changeLedgerState('stop');
    try {
        const accepted = await submitTransfer(transferKey + '-recovery', { ...transferBody, amountMinor: 50 });
        assert.equal(accepted.status, 202);
        recoveryId = (await accepted.json()).id;
        await delay(1800);
        const pending = await request(transferPath + '/' + recoveryId, customer);
        assert.equal((await pending.json()).status, 'Pending');
    } finally {
        changeLedgerState('start');
    }
    assert.equal((await awaitOrder(recoveryId)).status, 'Completed');
    const recovered = await (await request('/customers/me/accounts', customer)).json();
    assert.equal(recovered.find(item => item.id === account.id).balanceMinor, 300);
    console.log('Transfer remained pending during Ledger downtime and posted once after recovery.');
}

