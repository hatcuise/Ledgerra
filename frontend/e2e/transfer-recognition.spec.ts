import { expect, test } from '@playwright/test';
const api = process.env.E2E_API_BASE_URL ?? 'http://127.0.0.1:5027';

test('existing bank entries are recognized across a selected date gap and can be safely unlinked', async ({ page, request }) => {
  const registration = await request.post(`${api}/api/auth/register`, { data: { login: `match-${Date.now()}`, password: 'P@ssw0rd123!' } }); expect(registration.ok()).toBeTruthy();
  const auth = await registration.json(); const headers = { Authorization: `Bearer ${auth.accessToken}` };
  const a = await request.post(`${api}/api/accounts`, { headers, data: { name: 'Bank A', type: 'Checking', currencyCode: 'USD' } }); const b = await request.post(`${api}/api/accounts`, { headers, data: { name: 'Bank B', type: 'Savings', currencyCode: 'USD' } });
  expect(a.ok()).toBeTruthy(); expect(b.ok()).toBeTruthy(); const first = await a.json(); const second = await b.json();
  for (const [accountId, type, note, date] of [[first.id, 'Expense', 'Transfer to Bank B', '2026-04-10'], [second.id, 'Income', 'Transfer from Bank A', '2026-04-15']]) {
    const r = await request.post(`${api}/api/transactions`, { headers, data: { accountId, type, note, amount: 25, occurredOnUtc: `${date}T00:00:00Z` } }); expect(r.ok()).toBeTruthy();
  }
  await page.addInitScript(payload => localStorage.setItem('ledgerra.auth', JSON.stringify(payload)), auth);
  await page.goto('/bookkeeping');
  await page.getByLabel('Transfer matching date gap (days)').fill('5');
  await page.getByRole('button', { name: 'Find matching transfers', exact: true }).click();
  await expect(page.getByText('Clear match.', { exact: false })).toBeVisible();
  await page.getByRole('button', { name: 'Automatically link clear matches' }).click();
  await expect(page.getByText('1 clear transfer match(es) linked.', { exact: true })).toBeVisible();
  await page.getByText('Matched transfers (1)', { exact: true }).click();
  await page.getByRole('button', { name: 'Undo match', exact: true }).click();
  await expect(page.getByText('Transfer match undone. Both original transactions were preserved.', { exact: true })).toBeVisible();
  const result = await request.get(`${api}/api/transactions`, { headers }); const rows = await result.json(); expect(rows.length).toBe(2); expect(rows.map((r: { type: string }) => r.type).sort()).toEqual(['Expense', 'Income']);
});
