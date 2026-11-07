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

JSON backups now use version 4 and include bank profiles, payees, entry metadata,
reconciliation history, recurring schedules, categorization rules, and exchange
rates alongside the existing data. Version 2/3 backups remain readable. Restore
replaces the signed-in user's data; review and retain a backup before restoring.
A version 4 backup requires the upgraded backend; older versions cannot restore it.

## Verification and scope

Automated checks cover refund accounting in balances, budgets and reports, split
allocation, CSV conventions, repeated imports, ownership, exact reconciliation,
reconciled-entry locks, transfer matching, scheduled payment generation, and backup
round trips. Browser tests in `frontend/e2e/bookkeeping*.spec.ts` exercise saved
profiles, purchases/refunds, saving, reconciliation, payees, and payment matching.

The capabilities above build on Ledgerra's existing budgets, splits, rules,
recurring transactions, opening balances, and reports. Native HomeBank archives,
QIF/OFX import, investment lot accounting, and foreign-currency transfer matching
are outside this implementation. The new bookkeeping settings are currently in
English; existing localized transaction labels remain supported.
