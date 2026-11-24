import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdir } from 'node:fs/promises';
import { chromium } from 'playwright';

const origin = process.env.FINFLOW_PORTAL_URL ?? 'http://localhost:4200';
const password = process.env.FINFLOW_DEMO_PASSWORD ?? 'finflow-local-demo';
const screenshots = process.env.FINFLOW_SCREENSHOTS;
const browser = await chromium.launch(process.env.FINFLOW_BROWSER_CHANNEL
    ? { channel: process.env.FINFLOW_BROWSER_CHANNEL } : {});

async function signIn(context, username) {
    const page = await context.newPage();
    await page.goto(origin);
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();
    await page.locator('#username').fill(username);
    await page.locator('#password').fill(password);
    await page.locator('#kc-login').click();
    await page.getByRole('button', { name: 'Sign out', exact: true }).waitFor();
    return page;
}

try {
    if (screenshots) await mkdir(screenshots, { recursive: true });
    const customerContext = await browser.newContext({ viewport: { width: 1280, height: 850 } });
    const customer = await signIn(customerContext, 'customer');
    await customer.getByRole('heading', { name: 'Accounts', exact: true }).waitFor();
    assert.equal(await customer.getByRole('button', { name: 'Operations', exact: true }).count(), 0);
    if (screenshots) await customer.screenshot({ path: screenshots + '/customer.png' });
    if (process.env.FINFLOW_SMOKE_CACHE_PROJECT) {
        const compose = ['compose', '-p', process.env.FINFLOW_SMOKE_CACHE_PROJECT,
            '-f', 'compose.yaml', '-f', 'compose.secure.yaml'];
        execFileSync('docker', [...compose, 'stop', 'valkey'], { stdio: 'inherit' });
        try {
            await customer.getByRole('button', { name: 'Profile', exact: true }).click();
            await customer.getByRole('heading', { name: 'Edit profile', exact: true }).waitFor();
            assert.equal(await customer.getByRole('alert').count(), 0);
        } finally {
            execFileSync('docker', [...compose, 'start', 'valkey'], { stdio: 'inherit' });
        }
        console.log('Profile remained available during the Valkey outage.');
    }
    await customer.getByRole('button', { name: 'Payments', exact: true }).click();
    await customer.getByRole('heading', { name: 'Payment history' }).waitFor();
    await customer.getByRole('button', { name: 'Notifications', exact: true }).click();
    await customer.getByRole('heading', { name: 'Notifications', exact: true }).waitFor();
    assert.equal(await customer.getByRole('alert').count(), 0);
    await customer.getByRole('button', { name: 'Sign out' }).click();
    await customer.getByRole('button', { name: 'Sign in', exact: true }).waitFor();
    await customerContext.close();

    const staffContext = await browser.newContext({ viewport: { width: 1280, height: 850 } });
    const staff = await signIn(staffContext, 'operator');
    await staff.getByRole('heading', { name: 'Customers', exact: true }).waitFor();
    assert.equal(await staff.getByRole('button', { name: 'Payments', exact: true }).count(), 0);
    await staff.getByRole('button', { name: 'Operations', exact: true }).click();
    await staff.getByRole('heading', { name: 'Reconciliation' }).waitFor();
    await staff.getByRole('heading', { name: 'Operation history' }).waitFor();
    assert.equal(await staff.getByRole('alert').count(), 0);
    if (screenshots) await staff.screenshot({ path: screenshots + '/operations.png', fullPage: true });
    await staffContext.close();

    const auditContext = await browser.newContext({ viewport: { width: 390, height: 844 } });
    const audit = await signIn(auditContext, 'auditor');
    await audit.getByRole('heading', { name: 'Audit', exact: true }).waitFor();
    assert.equal(await audit.getByRole('button', { name: 'Operations', exact: true }).count(), 0);
    assert.equal(await audit.getByRole('alert').count(), 0);
    await auditContext.close();
    console.log('Customer, staff, and mobile audit navigation and customer logout passed.');
} finally {
    await browser.close();
}
