# US and Canada bank connections (Plaid)

Ledgerra can link deposit and credit-card accounts through Plaid Link, retrieve
available posted transactions, and send them to the existing import review screen.
The first connector supports the United States and Canada. It is independent of
AI settings: no OpenAI key is needed.

## Set up from the browser

1. Update Ledgerra to v0.16.0 or later, preserving PostgreSQL and the data-protection
   keyring volume. Use HTTPS for your Ledgerra installation, especially with real
   financial data and OAuth banks.
2. Create your own [Plaid developer application](https://dashboard.plaid.com/).
   Sandbox uses fake banks and test data; it does **not** connect a real bank.
   Real institutions require Plaid Production access and the Transactions product.
   Availability, approval, history limits and pricing depend on Plaid and the bank.
   Ledgerra does not supply a shared Plaid client or production credentials.
3. In **Imports → Connect US and Canadian banks → Plaid provider settings**, enter
   the client ID and secret in the form. Select Sandbox or Production and US or CA.
   Secrets are submitted to the backend, encrypted, and never returned in settings
   responses. Leave the secret blank on later settings edits to keep it.
4. For OAuth institutions, register your public HTTPS Ledgerra URL ending in
   `/imports` in the Plaid dashboard's allowed redirect URIs, then enter the exact
   same URL in Ledgerra. Example: `https://finance.example.com/imports`. Complete
   the redirect in the same browser tab so its short-lived Link session can resume.
5. Select **Connect bank with Plaid**. Sign in and consent in Plaid Link; Ledgerra
   has no bank username/password fields. Sandbox credentials/institutions are
   supplied by Plaid's test interface, not your real bank login.
6. Map each connected source account to an owned Ledgerra account of the same
   currency. Create destination accounts in Accounts first. Investment and loan
   accounts are not supported by this Transactions connector. Each destination
   can be mapped to only one connected source account; unmap it before remapping.
7. Choose **Sync available transactions**, then **Preview bank transactions**.
   Sync downloads to a separate queue; it creates no ledger transactions. Select
   the drafts to accept, review type/category/date/payee, and choose **Save selected
   drafts** in the regular import review screen. Nothing is preselected.
8. Use **Reconnect bank** if authentication expires. Use **Disconnect bank** to
   revoke the Plaid item and remove its local download queue. Already imported
   ledger entries remain.

Keep bank-provider credentials out of chat, screenshots, source control and support
logs. Use the application's password field and preserve your NAS keyring backup.
Provider configuration belongs to the signed-in Ledgerra user.

## Accounting and sync behavior

- Plaid's positive amount is money leaving the account, so it becomes Expense for
  both bank and credit-card spending. Negative amounts are money received; obvious
  refund wording is classified as Refund, while other credits default to Income
  with a review warning. Review internal transfers and loan proceeds explicitly;
  ordinary credit amounts are not universally income. Existing import rules and
  conservative transfer recognition run through the usual import pipeline.
- Pending transactions are not posted. Their final posted replacements are reviewed
  when available. Zero values and incompatible/unknown currencies are excluded.
  Values with more than two decimal places stop sync rather than silently rounding
  transaction amounts. Unsupported descriptions are bounded to existing memo/payee
  lengths (400/120 characters).
- Bank-reported balances are shown separately, with credit-card balances labelled
  as amounts owed. They never overwrite account opening balances or ledger totals.
- Sync uses the provider's cursor and persists it only after every page succeeds.
  A failed or mutated pagination run leaves the original cursor and queue intact;
  retry Sync. Manual sync retrieves information already available from Plaid; it
  does not force an immediate refresh at the bank. There are no background polling
  jobs, paid refresh calls, or webhook subscriptions in this version.
- Each queued transaction has a stable provider identity. Reimporting it is blocked
  by the ledger's imported-identity constraint, including after ordinary edits and
  same-user backup/restore. Existing CSV/OFX entries have different source IDs, so
  heuristic duplicate warnings still need review. A newly linked Plaid item may
  have new account/transaction IDs; review duplicates when reconnecting by creating
  a new item. Prefer **Reconnect bank** for an existing item.
- If Plaid changes or removes an already imported transaction, preview warns about
  it. Ledgerra does not silently rewrite amounts, delete history, bypass reconciliation
  locks or overwrite your manual edits. Review the existing entry yourself.
- At most 10 linked items per user, 25 pages of 500 changes per sync and 2,000 eligible
  drafts per preview are supported. Map/review one source account at a time for
  larger initial histories. Unmapped source entries stay in the queue.

## Backup, reset and operation

Portable Ledgerra JSON backups include imported ledger transactions and their bank
identities, but exclude provider credentials, access tokens, cursors, source-account
mappings and unimported queue entries. On restore on the same instance, existing
connections stay linked but mappings are cleared for explicit review. On a new
instance, configure Plaid and reconnect. Preserve PostgreSQL **and** ASP.NET data-
protection keys for a full server restore; a database alone cannot decrypt tokens.

A normal update using the same database/keyring keeps connections intact. Changing
the Plaid client/environment is blocked until existing items are disconnected.
User-data reset erases local bank configuration and queues; disconnect first to
revoke consent remotely, or revoke it through Plaid/your bank afterwards.

The backend sends bank API calls only to `sandbox.plaid.com` or
`production.plaid.com`, with redirect following disabled. Browsers load official
Plaid Link from `cdn.plaid.com` and may visit bank OAuth domains during consent.
Allow those destinations in NAS/browser outbound policies as needed. No externally
reachable Ledgerra webhook endpoint is required.

Implementation reference: [Plaid's official OpenAPI specification](https://github.com/plaid/plaid-openapi/blob/master/2020-09-14.yml),
API version `2020-09-14`. Tests use simulated provider responses and the Link UI;
live Production bank authorization requires the owner's credentials and consent
and has not been certified in this development environment.

### Synology keyring persistence

The repository's `docker-compose.release.yml` already mounts the keyring. If you
use a custom Container Manager project, check that its backend has a persistent
keyring mount before configuring bank credentials. An example fragment is below;
choose a folder under your existing project directory in File Station and retain
all existing database/network/other environment settings:

```yaml
backend:
  environment:
    DataProtection__KeysPath: /var/lib/ledgerra/keyring
  volumes:
    - /volume1/docker/ledgerra/keyring:/var/lib/ledgerra/keyring
```

If keys already exist only inside a container, back them up before changing its
mount or recreating that container. Losing old keys makes previously encrypted
credentials/tokens unreadable; imported ledger transactions remain in PostgreSQL.
