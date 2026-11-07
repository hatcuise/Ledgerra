import { expect, test } from '@playwright/test';
const api = process.env.E2E_API_BASE_URL ?? 'http://127.0.0.1:5027';

test('payees and imported card payments preserve balances and exclude transfers from income', async ({ page, request }) => {
  const registered = await request.post(`${api}/api/auth/register`, { data: { login: `pay-${Date.now()}`, password: 'P@ssw0rd123!' } });
  expect(registered.ok()).toBeTruthy(); const auth = await registered.json(); const headers = { Authorization: `Bearer ${auth.accessToken}` };
  const bankResult = await request.post(`${api}/api/accounts`, { headers, data: { name: 'Bank', type: 'Checking', currencyCode: 'USD', openingBalance: 100 } });
  const cardResult = await request.post(`${api}/api/accounts`, { headers, data: { name: 'Card', type: 'Credit', currencyCode: 'USD', openingBalance: -50 } });
  expect(bankResult.ok()).toBeTruthy(); expect(cardResult.ok()).toBeTruthy(); const bank = await bankResult.json(); const card = await cardResult.json();
  const outgoingResult = await request.post(`${api}/api/transactions`, { headers, data: { accountId: bank.id, type: 'Expense', amount: 25, occurredOnUtc: '2026-04-10T12:00:00Z', note: 'Card payment' } });
  const incomingResult = await request.post(`${api}/api/transactions`, { headers, data: { accountId: card.id, type: 'Income', amount: 25, occurredOnUtc: '2026-04-10T12:00:00Z', note: 'Payment received' } });
  expect(outgoingResult.ok()).toBeTruthy(); expect(incomingResult.ok()).toBeTruthy(); const incoming = await incomingResult.json();
  await page.addInitScript(payload => localStorage.setItem('ledgerra.auth', JSON.stringify(payload)), auth);
  await page.goto('/bookkeeping');
  await page.getByRole('combobox', { name: 'Account', exact: true }).selectOption(bank.id);
  await page.getByLabel('Payee name').fill('Card issuer');
  await page.getByRole('button', { name: 'Save payee', exact: true }).click();
  await expect(page.getByText('Payee saved.', { exact: true })).toBeVisible();
  await page.getByLabel('Select Card payment', { exact: true }).check();
  await page.getByRole('combobox', { name: 'Other account', exact: true }).selectOption(card.id);
  await page.getByRole('combobox', { name: 'Incoming transaction', exact: true }).selectOption(incoming.id);
  await page.getByRole('button', { name: 'Link transfer', exact: true }).click();
  await expect(page.getByText('Transfer linked.', { exact: true })).toBeVisible();
  await expect(page.getByText('TransferOut', { exact: true })).toBeVisible();
  await expect(page.getByText('Opening balance: $100.00 · Current balance: $75.00')).toBeVisible();
  const summary = await request.get(`${api}/api/dashboard/summary?month=2026-04`, { headers });
  expect(summary.ok()).toBeTruthy(); const totals = await summary.json(); expect(totals.income).toBe(0); expect(totals.expenses).toBe(0);
});
