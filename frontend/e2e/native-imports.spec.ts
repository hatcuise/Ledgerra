import { expect, test } from '@playwright/test';
const api = process.env.E2E_API_BASE_URL ?? 'http://127.0.0.1:5027';

test('OFX inspect, account mapping, preview, commit and repeat FITID detection need no AI', async ({ page, request }) => {
  const registration = await request.post(`${api}/api/auth/register`, { data: { login: `ofx-${Date.now()}`, password: 'P@ssw0rd123!' } }); expect(registration.ok()).toBeTruthy();
  const auth = await registration.json(); const headers = { Authorization: `Bearer ${auth.accessToken}` };
  const created = await request.post(`${api}/api/accounts`, { headers, data: { name: 'Imported checking', type: 'Checking', currencyCode: 'USD' } }); expect(created.ok()).toBeTruthy(); const account = await created.json();
  await page.addInitScript(payload => localStorage.setItem('ledgerra.auth', JSON.stringify(payload)), auth);
  await page.goto('/imports');
  const ofx = '<OFX><STMTRS><CURDEF>USD<BANKACCTFROM><BANKID>123<ACCTID>123456789</BANKACCTFROM><BANKTRANLIST><STMTTRN><DTPOSTED>20260410<TRNAMT>25<FITID>BROWSER-OFX-1<NAME>Employer<MEMO>Salary</STMTTRN></BANKTRANLIST></STMTRS></OFX>';
  await page.getByLabel('Native statement file').setInputFiles({ name: 'statement.ofx', mimeType: 'application/x-ofx', buffer: Buffer.from(ofx) });
  await page.getByRole('button', { name: 'Inspect native file' }).click();
  await page.getByRole('combobox', { name: /^Map Bank account/ }).selectOption(account.id);
  await page.getByRole('button', { name: 'Preview native transactions' }).click();
  await expect(page.getByLabel('Type for ofx-1')).toHaveValue('Income');
  await expect(page.getByLabel('Description for ofx-1')).toHaveValue('Salary');
  await page.getByRole('button', { name: 'Save selected drafts' }).click();
  await expect(page.getByLabel('Type for ofx-1')).toHaveCount(0);
  await page.getByRole('button', { name: 'Preview native transactions' }).click();
  await expect(page.getByText('This bank FITID was already imported or repeated in the file.')).toBeVisible();
  const all = await request.get(`${api}/api/transactions`, { headers }); expect((await all.json()).length).toBe(1);
});
