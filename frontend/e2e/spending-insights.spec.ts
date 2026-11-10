import { expect, test } from '@playwright/test';
const api = process.env.E2E_API_BASE_URL ?? 'http://127.0.0.1:5027';
async function setup(request: any) {
  const r = await request.post(`${api}/api/auth/register`, { data: { login: `insights-${Date.now()}-${Math.floor(Math.random()*1000)}`, password: 'P@ssw0rd123!' } }); expect(r.ok()).toBeTruthy();
  const auth = await r.json(); const headers = { Authorization: `Bearer ${auth.accessToken}` };
  const post = async (path: string, data: any) => { const response = await request.post(`${api}${path}`, { headers, data }); expect(response.ok(), await response.text()).toBeTruthy(); return response.json(); };
  const account = await post('/api/accounts', { name: 'Mint insights bank', type: 'Checking', currencyCode: 'USD', openingBalance: 10000 });
  const category = await post('/api/categories', { name: 'Mint food', kind: 'Expense' });
  return { auth, headers, account, category, post };
}

test('insights show evidence, persist dismissals and preferences, and survive backup/restore', async ({ page, request }) => {
  const { auth, headers, account, category, post } = await setup(request);
  for (const [date, amount] of [['2026-01-05',10],['2026-01-10',20],['2026-02-05',20],['2026-02-10',25],['2026-03-05',20],['2026-03-10',30]] as const)
    await post('/api/transactions', { accountId: account.id, categoryId: category.id, type: 'Expense', amount, occurredOnUtc: `${date}T12:00:00Z`, note: 'Usual food' });
  const purchase = await post('/api/transactions', { accountId: account.id, categoryId: category.id, type: 'Expense', amount: 240, occurredOnUtc: '2026-04-10T12:00:00Z', note: 'Unusual April food' });
  const budget = await request.put(`${api}/api/budgets/2026/4`, { headers, data: { categoryLimits: [{ categoryId: category.id, plannedAmount: 100, carryOverUnspent: false }] } }); expect(budget.ok()).toBeTruthy();
  await page.addInitScript(payload => localStorage.setItem('ledgerra.auth', JSON.stringify(payload)), auth);
  await page.goto('/insights'); await page.getByLabel('Insights month', { exact: true }).fill('2026-04');
  await expect(page.getByRole('heading', { name: 'Mint food is over budget', exact: true })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Spending is up in Mint food', exact: true })).toBeVisible();
  const unusual = page.locator('.spending-alert').filter({ has: page.getByRole('heading', { name: 'A purchase is larger than usual', exact: true }) });
  await unusual.getByRole('link', { name: 'Review transactions', exact: true }).click();
  await expect(page.getByLabel('Transaction Unusual April food', { exact: true })).toBeVisible();
  await expect(page.getByText('Showing the transaction linked from a spending alert.', { exact: false })).toBeVisible();
  await expect(page.getByLabel('Transaction Usual food', { exact: true })).toHaveCount(0);
  await page.getByRole('link', { name: 'Insights', exact: true }).first().click();
  const over = page.locator('.spending-alert').filter({ has: page.getByRole('heading', { name: 'Mint food is over budget', exact: true }) });
  await over.getByRole('button', { name: 'Dismiss alert', exact: true }).click();
  await expect(page.getByText('Alert dismissed for 2026-04.', { exact: true })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Mint food is over budget', exact: true })).toHaveCount(0);
  await page.reload(); await page.getByLabel('Insights month', { exact: true }).fill('2026-04');
  await page.getByLabel('Show dismissed alerts').check(); await expect(page.getByRole('heading', { name: 'Mint food is over budget', exact: true })).toBeVisible();
  await page.getByLabel('Large purchase threshold', { exact: true }).fill('900'); await page.getByRole('button', { name: 'Save alert preferences' }).click();
  await expect(page.getByText('Alert preferences saved.', { exact: true })).toBeVisible();
  const backupResponse = await request.get(`${api}/api/backup/export`, { headers }); const backup = await backupResponse.json(); expect(backup.version).toBe(7); expect(backup.insightPreferences.largePurchaseThreshold).toBe(900); expect(backup.insightDismissals.length).toBe(1);
  const restore = await request.post(`${api}/api/backup/restore`, { headers, data: backup }); expect(restore.ok()).toBeTruthy();
  await page.reload(); await page.getByLabel('Insights month', { exact: true }).fill('2026-04'); await page.getByLabel('Show dismissed alerts').check();
  await expect(page.getByRole('heading', { name: 'Mint food is over budget', exact: true })).toBeVisible(); await expect(page.getByLabel('Large purchase threshold', { exact: true })).toHaveValue('900');
  await page.getByRole('button', { name: 'Restore alert', exact: true }).click(); await page.getByLabel('Show dismissed alerts').uncheck();
  await expect(page.getByRole('heading', { name: 'Mint food is over budget', exact: true })).toBeVisible();
  const stored = await (await request.get(`${api}/api/transactions/${purchase.id}`, { headers })).json(); expect(stored.amount).toBe(240);
  await page.screenshot({ path: '/tmp/ledgerra-spending-insights-desktop.png', fullPage: true });
});

test('New York month-end alerts link to the right transactions and fit a mobile viewport', async ({ page, request }) => {
  const { auth, headers, account, category, post } = await setup(request);
  await post('/api/transactions', { accountId: account.id, categoryId: category.id, type: 'Expense', amount: 40, occurredOnUtc: '2026-05-01T02:00:00Z', note: 'Late New York purchase' });
  const budget = await request.put(`${api}/api/budgets/2026/4`, { headers, data: { categoryLimits: [{ categoryId: category.id, plannedAmount: 30 }] } }); expect(budget.ok()).toBeTruthy();
  await page.emulateMedia({ reducedMotion: 'reduce' }); await page.setViewportSize({ width: 390, height: 844 });
  await page.addInitScript(payload => localStorage.setItem('ledgerra.auth', JSON.stringify(payload)), auth);
  await page.goto('/insights'); await page.getByLabel('Insights month', { exact: true }).fill('2026-04');
  await expect(page.getByText('2026-04 · USD · America/New_York', { exact: true })).toBeVisible();
  const over = page.locator('.spending-alert').filter({ has: page.getByRole('heading', { name: 'Mint food is over budget', exact: true }) });
  await expect(over).toBeVisible(); await page.screenshot({ path: '/tmp/ledgerra-spending-insights-mobile.png', fullPage: true });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 2)).toBeTruthy();
  expect(await page.locator('.mobile-nav').evaluate(element => element.getBoundingClientRect().height)).toBeLessThan(90);
  await over.getByRole('link', { name: 'Review transactions', exact: true }).click();
  await expect(page.getByLabel('Transaction Late New York purchase', { exact: true })).toBeVisible();
});
