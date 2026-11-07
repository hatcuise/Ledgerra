# Bookkeeping and bank imports

Ledgerra supports explicit Income, Expense, Refund, and linked Transfer transactions.
Amounts stored on a transaction are positive magnitudes. Its type determines its
balance and reporting effects; a CSV's amount sign is only an import convention.

## Bank import profiles

Upload a CSV in Imports and review the column mapping and bank import settings.
Choose a delimiter (comma, semicolon, tab, or pipe), locale, and date format. An
explicit date format avoids ambiguity between March 7 and July 3. Either map a
single signed amount or select separate nonnegative debit and credit columns.
A row cannot have both a nonzero debit and credit. Zero-value rows are skipped.

Specify the meaning of positive and negative amounts. For a credit card export
with positive purchases and negative refunds, choose Expense for positive amounts
and Refund for negative amounts, leaving Invert amounts off. Inversion reverses
signs before applying these choices. A type column, when selected, overrides the
sign mapping and must contain Income, Expense, or Refund.

Save these settings under a bank profile name and select the profile for later
imports. Profiles are private to the signed-in user and stored in PostgreSQL.
Optional payee, category, reference, and tag columns are preserved through preview
and saving. Category names match existing categories of the appropriate type;
unmatched categories produce a warning and require review rather than creating
new categories automatically.

CSV previews apply categorization rules and flag both existing duplicates and
repeated rows in the same file. Preview and commit do not require an AI provider.
Always review dates, amounts, types, categories, and duplicate warnings before saving.
PDF extraction still uses the separately configured AI provider.

## Credit cards, refunds, and transfers

Record credit card debt as a negative account balance. A purchase is an Expense;
a refund increases the account balance and reduces category spending rather than
increasing income. Budgets, dashboard totals, cash flow, category reports, and
account balances use these meanings consistently. Split children allocate category
spending without affecting account balances a second time.

A card payment moves money from the bank to the card account. Create a Transfer
using the transaction form, or import each statement and use Bookkeeping to link
the outgoing and incoming entries. Matching requires equal amounts, separate
accounts with the same currency, and unreconciled entries without existing split
or transfer groups. Both original entries and dates remain; neither balance changes.
Transfers are excluded from income and spending.

Existing transactions are never reclassified during an upgrade. Use the register's
bulk type/category/payee tools to correct selected ordinary entries after reviewing
them. Splits and transfers must be edited through their transaction workflow.

## Payees, clearing, and reconciliation

Bookkeeping contains an account register with running balances, payees, reference
numbers, tags, and Uncleared/Cleared/Reconciled states. Payees can carry a default
category, and payees/categories can be applied to selected entries together.

To reconcile a statement:

1. Choose an account and select the entries that appear on the statement.
2. Enter its date and ending balance (negative for credit card debt).
3. Finish only when the calculated balance equals the statement balance exactly.

The calculation starts at the account's opening balance and includes all previously
reconciled entries plus the selected entries. Future entries and foreign-account
entries are rejected. Statements must be reconciled in increasing date order.
Reconciled entries cannot be changed, moved, deleted, or relinked, including their
paired transfer/split entries. The account's opening balance and currency are also
protected. Reopen the latest statement to make a reviewed correction, then reconcile
again. Reopening restores its selected entries to Cleared.

## Scheduled transactions

The existing weekly/monthly scheduler supports Income, Expense, Refund, and
Transfer. Scheduled transfers need a different destination account with the same
currency and create linked outgoing/incoming entries together. Generation keeps
its existing serializable transaction and monthly day anchoring.

## Upgrade and backup

The PostgreSQL startup initializer adds bookkeeping tables and a nullable scheduled
transfer destination. It is idempotent; it does not reinterpret or rewrite existing
transactions. Retain your Synology project's PostgreSQL volume and credentials.
Back up the database before upgrading. Update both backend and frontend images.

JSON backups now use version 5 and include bank profiles, payees, entry metadata,
reconciliation history, recognized-transfer undo history, stable bank identities,
recurring schedules, categorization rules, and exchange rates alongside the existing data. Version 2/3/4 backups remain readable. Restore
replaces the signed-in user's data; review and retain a backup before restoring.
A version 5 backup requires the upgraded backend; older versions cannot restore it.

## Verification and scope

Automated checks cover refund accounting in balances, budgets and reports, split
allocation, CSV conventions, repeated imports, ownership, exact reconciliation,
reconciled-entry locks, transfer matching, scheduled payment generation, and backup
round trips. Browser tests in `frontend/e2e/bookkeeping*.spec.ts` exercise saved
profiles, purchases/refunds, saving, reconciliation, payees, and payment matching.

The capabilities above build on Ledgerra's existing budgets, splits, rules,
recurring transactions, opening balances, and reports. Full HomeBank wallet migration, split-allocation migration, investment lot accounting,
and foreign-currency transfer matching remain outside this implementation. The new bookkeeping settings are currently in
English; existing localized transaction labels remain supported.


## Native statements: OFX/QFX, QIF, and HomeBank

The Imports page has a separate native-file panel. Choose UTF-8/BOM Unicode or
Windows-1252, and the QIF month/day or day/month order. Inspect the file, then map
each source account to a distinct existing owned Ledgerra account, or explicitly
skip that source account. OFX and HomeBank currency information must match the
destination. The panel displays source opening balances but never changes the
destination opening balance. Create missing Ledgerra accounts before importing.

Preview transactions before saving. Payees, categories, references, and tags are
carried through supported records. Unknown categories require review and are not
created automatically. Positive credits in a uniquely matched expense category
become Refund; other credits default to Income for review. Source clearing and
reconciliation states are not asserted as Ledgerra-reconciled statements.

OFX bank/credit-card XML and legacy SGML are supported. OFX FITID is hashed with
its source bank/account identity into a stable bank key. Reimporting that identity
is blocked even after descriptions are edited or data is backed up/restored.
Other formats use reviewed heuristic duplicate detection, including entries that
have since been linked as transfers. OFX corrections and investment statements
need separate migration support; they are not applied as replacement/deletion commands.

HomeBank .xhb input is XML, not the HomeBank installer/source .tar.gz archive.
The reader resolves account/currency/payee/category data and GLib Julian dates.
Only ordinary transaction migration is supported. Split rows, reminder/void rows,
and cross-currency HomeBank transfers are excluded with warnings; schedules,
budgets, rules, original reconciliation history, and opening balances are not
migrated by this panel. Review warnings and destination balances before accepting
any partial migration. Values requiring more than two decimal places are rejected
for selected accounts, never silently rounded. DTDs/external entities are disabled.

Limits: 8 MB input, 100 source accounts, and 20,000 supported transactions per file.
QIF investment activity is rejected; QIF opening balance and split rows are not
posted as ordinary entries. QIF two-digit years use 1970–2069. Multi-file batches
and complete HomeBank wallet migration remain separate inventory items.

## Automatic and retrospective transfer recognition

New ordinary transactions and imported batches look for an existing counterpart.
Pairs must have opposite directions, equal amounts, the same currency, different
owned accounts, and dates within three calendar days. Both endpoints must be
mutually unique. Automatic linking additionally requires a nontrivial shared bank
reference, or transfer descriptions that identify both owned accounts by name or
masked account suffix. Generic transfer wording or equal amounts/dates alone only
produce suggestions. Reconciled, split, refund, and already-linked entries are excluded.

Bookkeeping → Recognize transfers scans entries that were added earlier. Choose a
0–7 day gap, review candidates, link a selected pair, or automatically link the
clear matches. No extra money movements are created. Both account balances,
original dates, payees, references, and tags remain intact; income/spending reports
exclude the linked payment. Recognized history offers Undo match, restoring both
original entries and their categories where those categories still exist. Reopen
any reconciled statement before undoing; recognized entries cannot be edited or
deleted through the old transfer editor before unlinking.

The [HomeBank capability audit](audits/homebank-5.10.3-gap-analysis.md) lists the
remaining work and distinguishes reference behavior from engineering recommendations.

## Everyday ledger tools (v0.15.0)

In **Transactions → Add transaction**, choose Expense and **Add split**. Allocate
at least two positive amounts to expense categories. The allocation total must
equal the transaction amount; the unallocated balance is shown before saving.
Editing a split loads its allocations. If loading fails, saving is disabled to
prevent inadvertently replacing it with a single category.

Expand **Payee, reference, tags and payment details** to set metadata in the same
save. Tags are comma separated. **Save and keep details** leaves the entry form
open for repeated entry. **Duplicate** opens an unsaved copy for review, with the
reference and cleared state reset. Imported source identities are never cloned.

**Bookkeeping → Payees and tags** supports editing and merging payees, deleting
unused payees and renaming/combining/removing used tags. A payee merge keeps the
target's default category and updates references without changing balances.

**Categories → Category hierarchy and merge** stores parent relationships and
performs real merges. Merging repairs dependencies, including split allocations
and transfer undo history. It stops on overlapping budget limits so no planned
amount or carry-forward policy is silently discarded. Parent relationships do
not yet aggregate budget/report totals.

**Transactions → Named saved filters** saves multiple views to your account and
backup. Selecting a saved filter restores the account, category, type, date,
amount and note filters. Saving the same name replaces that saved view.

**Settings → Import rules → Additional rule conditions** constrains a text rule
by account and amount; all conditions must match. **Bookkeeping → Apply rules to
existing transactions** previews the active rules by priority, lets you select
changes and rechecks their validity before committing. A type change can affect
balances, so both the current and proposed types are shown. Reconciled entries,
splits and transfers are excluded. Narrow the dates if more than 2,000 eligible
entries would be reviewed.

See [v0.15.0 scope and remaining work](release-notes/v0.15.0.md).
