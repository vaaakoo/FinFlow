import { Component, OnDestroy, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DatePipe, DecimalPipe } from '@angular/common';
import { HubConnection, HubConnectionBuilder } from '@microsoft/signalr';
import { User, UserManager, WebStorageStateStore } from 'oidc-client-ts';

interface Account { id: string; name: string; balanceMinor: number; currency: string; }
interface Profile { subject: string; displayName: string; kyc: string; active: boolean; }
interface Transfer { id: string; amountMinor: number; status: string; postingId?: string; rejection?: string; }
interface Payment { id: string; amountMinor: number; status: string; scenario: string; reason?: string; }
interface Notification {
    eventId: string; transferId: string; amountMinor: number; createdAt: string; readAt?: string;
}
interface StatementEntry { transferId: string; completedAt: string; deltaMinor: number; }
interface WorkflowEvent { eventId: string; kind: string; entityId: string; occurredAt: string; }
interface Page<T, TCursor = string> { items: T[]; next?: TCursor; }
interface StatementCursor { beforeId: string; beforeTime: string; }
interface Reconciliation { transfersChecked: number; invalidPostings: number; }

type Section = 'Accounts' | 'Transfers' | 'Payments' | 'Notifications' | 'Profile' | 'Customers' | 'Operations' | 'Audit';

@Component({
    selector: 'finflow-app',
    imports: [FormsModule, DatePipe, DecimalPipe],
    templateUrl: './portal.component.html'
})
export class PortalComponent implements OnInit, OnDestroy {
    readonly identity = signal<User | null>(null);
    readonly section = signal<Section>('Accounts');
    readonly working = signal(false);
    readonly starting = signal(true);
    readonly error = signal('');
    readonly notice = signal('');
    readonly notifications = signal<Notification[]>([]);
    readonly paymentScenarios = ['success', 'decline', 'delay', 'timeout', 'unavailable', 'duplicate', 'uncertain'];
    accounts: Account[] = [];
    transfers: Transfer[] = [];
    payments: Payment[] = [];
    customers: Profile[] = [];
    audit: WorkflowEvent[] = [];
    reconciliation?: Reconciliation;
    statement?: Page<StatementEntry, StatementCursor>;
    statementAccount = '';
    profileName = '';
    profile?: Profile;
    transferSource = '';
    transferDestination = '';
    transferAmount: number | null = null;
    transferNext?: string;
    paymentAccount = '';
    paymentAmount: number | null = null;
    paymentScenario = 'success';
    paymentPage = 1;
    notificationPage = 1;
    customerNext?: string;
    customerFilter = '';
    accountName = '';
    ownerSubject = '';
    auditFilter = '';
    auditPage = 1;
    staffTransfers: Transfer[] = [];
    staffPayments: Payment[] = [];
    operationKind: 'transfers' | 'payments' = 'transfers';
    operationStatus = '';
    operationsPage = 1;
    fundingSource = '';
    fundingDestination = '';
    fundingAmount: number | null = null;
    private fundingKey = '';
    private transferKey = '';
    private paymentKey = '';
    private manager?: UserManager;
    private hub?: HubConnection;

    get roles(): string[] {
        const payload = this.identity()?.access_token.split('.')[1];
        if (!payload) return [];
        try {
            return (JSON.parse(atob(payload.replaceAll('-', '+').replaceAll('_', '/'))).roles ?? []) as string[];
        } catch {
            return [];
        }
    }

    get customer(): boolean { return this.roles.includes('Customer'); }
    get staff(): boolean { return this.roles.includes('Administrator') || this.roles.includes('BankOperator'); }
    get auditor(): boolean { return this.roles.includes('Auditor') || this.roles.includes('Administrator'); }
    get sections(): Section[] {
        const sections: Section[] = [];
        if (this.customer) sections.push('Accounts', 'Transfers', 'Payments', 'Notifications', 'Profile');
        if (this.staff) sections.push('Customers', 'Operations');
        if (this.auditor) sections.push('Audit');
        return sections;
    }

    get visibleCustomers(): Profile[] {
        const filter = this.customerFilter.trim().toLowerCase();
        return this.customers.filter(profile => profile.displayName.toLowerCase().includes(filter));
    }

    async ngOnInit(): Promise<void> {
        try {
            const config = await fetch('/portal-config.json').then(result => result.json()).catch(() => ({}));
            this.manager = new UserManager({
                authority: 'http://localhost:' + (config.identityPort || 8082) + '/realms/finflow',
                client_id: 'finflow-portal',
                redirect_uri: location.origin + '/auth/callback',
                post_logout_redirect_uri: location.origin + '/',
                response_type: 'code',
                scope: 'openid profile',
                userStore: new WebStorageStateStore({ store: sessionStorage }),
                automaticSilentRenew: true
            });
            this.manager.events.addUserLoaded(user => this.identity.set(user));
            this.manager.events.addUserUnloaded(() => this.identity.set(null));
            const user = location.pathname === '/auth/callback'
                ? await this.manager.signinRedirectCallback()
                : await this.manager.getUser();
            if (location.pathname === '/auth/callback')
                history.replaceState({}, '', '/');

            if (user && !user.expired) {
                this.identity.set(user);
                const initial = this.sections[0];
                if (initial) await this.open(initial);
                if (this.customer) void this.connectNotifications();
            }
        } catch (error) {
            this.error.set(this.errorMessage(error));
        } finally {
            this.starting.set(false);
        }
    }

    ngOnDestroy(): void { void this.hub?.stop(); }
    signIn(): void { void this.run(async () => { await this.manager?.signinRedirect(); }); }
    signOut(): void {
        void this.run(async () => {
            await this.hub?.stop();
            await this.manager?.signoutRedirect();
        });
    }

    async open(section: Section): Promise<void> {
        this.section.set(section);
        this.notice.set('');
        await this.run(async () => {
            switch (section) {
                case 'Accounts': await this.loadAccounts(); break;
                case 'Transfers': await this.loadAccounts(); await this.loadTransfers(); break;
                case 'Payments': await this.loadAccounts(); await this.loadPayments(1); break;
                case 'Notifications': await this.loadNotifications(1); break;
                case 'Profile': await this.loadProfile(); break;
                case 'Customers': await this.loadCustomers(); break;
                case 'Operations':
                    this.accounts = await this.api<Account[]>('/accounts');
                    this.reconciliation = await this.api<Reconciliation>('/staff/ledger/reconciliation');
                    await this.loadOperations(1);
                    break;
                case 'Audit': await this.loadAudit(1); break;
            }
        });
    }

    async showStatement(accountId: string, cursor?: StatementCursor): Promise<void> {
        await this.run(async () => {
            const base = this.customer ? '/customers/me/accounts/' : '/accounts/';
            const parameters = new URLSearchParams({ size: '10' });
            if (cursor) {
                parameters.set('beforeId', cursor.beforeId);
                parameters.set('beforeTime', cursor.beforeTime);
            }
            this.statement = await this.api<Page<StatementEntry, StatementCursor>>(
                base + encodeURIComponent(accountId) + '/statement?' + parameters);
            this.statementAccount = this.accounts.find(account => account.id === accountId)?.name ?? accountId;
            this.selectedStatementAccount = accountId;
        });
    }

    selectedStatementAccount = '';

    async submitTransfer(): Promise<void> {
        await this.run(async () => {
            if (!Number.isSafeInteger(this.transferAmount) || this.transferAmount! < 1)
                throw new Error('Enter a positive whole number of tetri.');
            this.transferKey ||= crypto.randomUUID();
            let transfer: Transfer;
            try {
                transfer = await this.api<Transfer>('/customers/me/transfers', 'POST', {
                    sourceAccountId: this.transferSource, destinationAccountId: this.transferDestination.trim(),
                    amountMinor: this.transferAmount
                }, this.transferKey);
            } catch (error) {
                if (error instanceof ApiError && [400, 409].includes(error.status)) this.transferKey = '';
                throw error;
            }
            this.transferKey = '';
            this.transferAmount = null;
            this.notice.set('Transfer accepted. Refresh history to check its status.');
            await this.loadTransfers();
            if (!this.transfers.some(item => item.id === transfer.id)) this.transfers.unshift(transfer);
        });
    }

    moreTransfers(): void { void this.run(() => this.loadTransfers(this.transferNext)); }

    async submitPayment(): Promise<void> {
        await this.run(async () => {
            if (!Number.isSafeInteger(this.paymentAmount) || this.paymentAmount! < 1)
                throw new Error('Enter a positive whole number of tetri.');
            this.paymentKey ||= crypto.randomUUID();
            try {
                await this.api('/customers/me/payments', 'POST', {
                    accountId: this.paymentAccount, amountMinor: this.paymentAmount, scenario: this.paymentScenario
                }, this.paymentKey);
            } catch (error) {
                if (error instanceof ApiError && [400, 409].includes(error.status)) this.paymentKey = '';
                throw error;
            }
            this.paymentKey = '';
            this.paymentAmount = null;
            this.notice.set('Payment accepted. Refresh history to check its status.');
            await this.loadPayments(1);
        });
    }

    refund(id: string): void {
        void this.run(async () => {
            await this.api('/customers/me/payments/' + encodeURIComponent(id) + '/refund', 'POST');
            this.notice.set('Refund requested.');
            await this.loadPayments(this.paymentPage);
        });
    }

    paymentHistory(page: number): void { void this.run(() => this.loadPayments(page)); }
    notificationHistory(page: number): void { void this.run(() => this.loadNotifications(page)); }

    markRead(id: string): void {
        void this.run(async () => {
            await this.api('/customers/me/notifications/' + encodeURIComponent(id) + '/read', 'PUT');
            await this.loadNotifications(this.notificationPage);
        });
    }

    saveProfile(): void {
        void this.run(async () => {
            this.profile = await this.api<Profile>('/customers/me', this.profile ? 'PUT' : 'POST',
                { displayName: this.profileName.trim() });
            this.notice.set('Profile saved.');
        });
    }

    setCustomerStatus(profile: Profile, kyc: string, active: boolean): void {
        void this.run(async () => {
            const updated = await this.api<Profile>('/staff/customers/' + encodeURIComponent(profile.subject)
                + '/status', 'PUT', { kyc, active });
            this.customers = this.customers.map(item => item.subject === updated.subject ? updated : item);
            this.notice.set('Customer status saved.');
        });
    }

    moreCustomers(): void { void this.run(() => this.loadCustomers(this.customerNext)); }
    auditHistory(page: number): void { void this.run(() => this.loadAudit(page)); }

    openAccount(): void {
        void this.run(async () => {
            const account = await this.api<Account>('/staff/ledger/accounts', 'POST',
                { name: this.accountName.trim(), ownerSubject: this.ownerSubject.trim() });
            this.accounts = [...this.accounts, account];
            this.accountName = '';
            this.notice.set('Account opened.');
        });
    }

    fundAccount(): void {
        void this.run(async () => {
            if (!Number.isSafeInteger(this.fundingAmount) || this.fundingAmount! < 1)
                throw new Error('Enter a positive whole number of tetri.');
            this.fundingKey ||= crypto.randomUUID();
            await this.api('/transfers', 'POST', {
                sourceAccountId: this.fundingSource, destinationAccountId: this.fundingDestination,
                amountMinor: this.fundingAmount
            }, this.fundingKey);
            this.fundingKey = '';
            this.fundingAmount = null;
            this.accounts = await this.api<Account[]>('/accounts');
            this.notice.set('Funding transfer posted.');
        });
    }

    changeOperationKind(): void {
        this.operationStatus = '';
        this.operationsHistory(1);
    }

    operationsHistory(page: number): void { void this.run(() => this.loadOperations(page)); }

    private async loadOperations(page: number): Promise<void> {
        const parameters = new URLSearchParams({ page: String(page) });
        if (this.operationStatus) parameters.set('status', this.operationStatus);
        const path = '/staff/' + this.operationKind + '?' + parameters;
        if (this.operationKind === 'transfers')
            this.staffTransfers = await this.api<Transfer[]>(path);
        else
            this.staffPayments = await this.api<Payment[]>(path);
        this.operationsPage = page;
    }

    private async loadAccounts(): Promise<void> {
        this.accounts = await this.api<Account[]>('/customers/me/accounts');
    }

    private async loadTransfers(cursor?: string): Promise<void> {
        const path = '/customers/me/transfers?size=10' + (cursor ? '&before=' + encodeURIComponent(cursor) : '');
        const page = await this.api<Page<Transfer>>(path);
        this.transfers = cursor ? [...this.transfers, ...page.items] : page.items;
        this.transferNext = page.next;
    }

    private async loadPayments(page: number): Promise<void> {
        this.payments = await this.api<Payment[]>('/customers/me/payments?page=' + page);
        this.paymentPage = page;
    }

    private async loadNotifications(page: number): Promise<void> {
        this.notifications.set(await this.api<Notification[]>('/customers/me/notifications?page=' + page));
        this.notificationPage = page;
    }

    private async loadProfile(): Promise<void> {
        try {
            this.profile = await this.api<Profile>('/customers/me');
            this.profileName = this.profile.displayName;
        } catch (error) {
            if (!(error instanceof ApiError) || error.status !== 404) throw error;
            this.profile = undefined;
            this.profileName = '';
        }
    }

    private async loadCustomers(cursor?: string): Promise<void> {
        const path = '/staff/customers?size=10' + (cursor ? '&after=' + encodeURIComponent(cursor) : '');
        const page = await this.api<Page<Profile>>(path);
        this.customers = cursor ? [...this.customers, ...page.items] : page.items;
        this.customerNext = page.next;
    }

    private async loadAudit(page: number): Promise<void> {
        const parameters = new URLSearchParams({ page: String(page) });
        if (this.auditFilter.trim()) parameters.set('entityId', this.auditFilter.trim());
        this.audit = await this.api<WorkflowEvent[]>('/staff/audit/workflows?' + parameters);
        this.auditPage = page;
    }

    private async connectNotifications(): Promise<void> {
        this.hub = new HubConnectionBuilder()
            .withUrl('/api/customers/me/notifications/live', {
                accessTokenFactory: () => this.identity()?.access_token ?? ''
            })
            .withAutomaticReconnect()
            .build();
        this.hub.on('NotificationReceived', (entry: Notification) => {
            if (this.notificationPage === 1)
                this.notifications.update(rows => [entry, ...rows.filter(item => item.eventId !== entry.eventId)].slice(0, 25));
            this.notice.set('A new transfer notification arrived.');
        });
        try { await this.hub.start(); }
        catch { this.notice.set('Live notifications are unavailable. History is still available.'); }
    }

    private async run(operation: () => Promise<void>): Promise<void> {
        this.working.set(true);
        this.error.set('');
        try { await operation(); }
        catch (error) { this.error.set(this.errorMessage(error)); }
        finally { this.working.set(false); }
    }

    private errorMessage(error: unknown): string {
        return error instanceof Error ? error.message : 'The request could not be completed.';
    }

    private async api<T>(path: string, method = 'GET', body?: object, key?: string): Promise<T> {
        const header = new Headers({ Authorization: 'Bearer ' + (this.identity()?.access_token ?? '') });
        if (body) header.set('Content-Type', 'application/json');
        if (key) header.set('Idempotency-Key', key);
        const response = await fetch('/api' + path, { method, headers: header, body: body && JSON.stringify(body) });
        if (!response.ok) {
            const payload = await response.json().catch(() => ({}));
            throw new ApiError(response.status, payload.detail ?? payload.error ?? 'Request failed (' + response.status + ').');
        }
        return response.status === 204 ? undefined as T : response.json();
    }
}

class ApiError extends Error {
    constructor(readonly status: number, message: string) { super(message); }
}
