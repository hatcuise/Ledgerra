import { expect, test } from '@playwright/test';
const api = process.env.E2E_API_BASE_URL ?? 'http://127.0.0.1:5027';

test('Plaid Link browser flow maps an account and reviews a feed before posting', async ({ page, request }) => {
  const registration = await request.post(`${api}/api/auth/register`, { data: { login: `bank-ui-${Date.now()}`, password: 'P@ssw0rd123!' } });
  expect(registration.ok()).toBeTruthy(); const auth = await registration.json(); const headers = { Authorization: `Bearer ${auth.accessToken}` };
  const created = await request.post(`${api}/api/accounts`, { headers, data: { name: 'Connected browser bank', type: 'Checking', currencyCode: 'USD', openingBalance: 100 } });
  expect(created.ok()).toBeTruthy(); const account = await created.json();
  let configured = false; let linked = false; let mapped: string | null = null; let synced = false;
  let config = { clientId: '', country: 'US', environment: 'Sandbox', redirectUri: null as string | null };
  const connectionId = '22222222-2222-4222-8222-222222222222'; const sourceId = '33333333-3333-4333-8333-333333333333';
  // The bank provider is simulated; transaction commit uses the real API/PostgreSQL.
  await page.route('**/api/banks/**', async route => {
    const path = new URL(route.request().url()).pathname.slice('/api/banks/'.length);
    const method = route.request().method(); const data = method === 'GET' ? null : route.request().postDataJSON();
    let body: any = {};
    if (path === 'settings') {
      if (method === 'PUT') { expect(data.secret).toBe('sandbox-ui-secret'); configured = true; config = { clientId: data.clientId, country: data.country, environment: data.environment, redirectUri: data.redirectUri }; }
      body = { ...config, isConfigured: configured };
    } else if (path === 'connections') body = linked ? [{ id: connectionId, name: 'Demo bank', environment: 'Sandbox', lastSyncedAtUtc: synced ? '2026-11-07T12:00:00Z' : null, accounts: [{ id: sourceId, name: 'Demo checking', mask: '1234', type: 'depository', currency: 'USD', reportedBalance: 999, ledgerAccountId: mapped }] }] : [];
    else if (path === 'link-token') body = { linkToken: 'link-sandbox-browser' };
    else if (path === 'exchange') { expect(data.publicToken).toBe('public-sandbox-browser'); linked = true; body = { id: connectionId, name: 'Demo bank' }; }
    else if (path.endsWith('/mapping')) { mapped = data.accountId; body = {}; }
    else if (path.endsWith('/sync')) { synced = true; body = { changes: 1 }; }
    else if (path.endsWith('/preview')) { expect(synced).toBeTruthy(); body = { warnings: ['Pending transactions excluded.'], transactions: [{ sourceId: 'bank-ui-1', accountId: mapped, amount: 12, type: 'Expense', occurredOnUtc: '2026-11-07T12:00:00Z', note: 'Connected bank market', payeeName: 'Demo market', confidence: 1, warnings: [], isSelectedByDefault: false, isLikelyDuplicate: false, externalKey: 'B'.repeat(64) }] }; }
    else throw new Error(`Unexpected bank route: ${path}`);
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
  });
  await page.route('https://cdn.plaid.com/link/v2/stable/link-initialize.js', route => route.fulfill({ status: 200, contentType: 'application/javascript', body: `window.Plaid = {create: function(options) {return {open: function() {options.onSuccess('public-sandbox-browser', {institution:{name:'Demo bank'}});},destroy:function(){}};}};` }));
  await page.addInitScript(payload => localStorage.setItem('ledgerra.auth', JSON.stringify(payload)), auth);
  await page.goto('/imports');
  await page.getByText('Plaid provider settings (required)', { exact: true }).click();
  await page.getByLabel('Plaid client ID').fill('browser-client'); await page.getByLabel('Plaid secret', { exact: true }).fill('sandbox-ui-secret');
  await page.getByRole('combobox', { name: 'Bank country', exact: true }).selectOption('CA');
  await page.getByRole('button', { name: 'Save Plaid settings', exact: true }).click();
  await expect(page.getByLabel('Plaid secret', { exact: true })).toHaveValue('');
  await page.getByRole('button', { name: 'Connect bank with Plaid' }).click();
  await expect(page.getByRole('heading', { name: 'Demo bank · Sandbox' })).toBeVisible();
  await page.getByRole('combobox', { name: 'Map connected Demo checking' }).selectOption(account.id);
  await page.getByRole('button', { name: 'Sync available transactions', exact: true }).click();
  await expect(page.getByText('1 provider changes downloaded. Preview to review posted transactions; no ledger entries were changed.', { exact: true })).toBeVisible();
  const before = await (await request.get(`${api}/api/accounts/${account.id}`, { headers })).json(); expect(before.currentBalance).toBe(100);
  await page.getByRole('button', { name: 'Preview bank transactions', exact: true }).click();
  await expect(page.getByLabel('Type for bank-ui-1')).toHaveValue('Expense');
  await expect(page.getByLabel('Select bank-ui-1', { exact: true })).not.toBeChecked();
  await page.getByLabel('Select bank-ui-1', { exact: true }).check();
  await page.getByRole('button', { name: 'Save selected drafts', exact: true }).click();
  await expect(page.getByLabel('Type for bank-ui-1')).toHaveCount(0);
  const after = await (await request.get(`${api}/api/accounts/${account.id}`, { headers })).json(); expect(after.currentBalance).toBe(88); expect(after.openingBalance).toBe(100);
});
