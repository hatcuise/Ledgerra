# Spending insights and in-app alerts

Open **Insights** in the navigation. Choose a month using **Insights month**;
this works on mobile even when the sidebar is hidden. The dashboard's spending
alert summary uses the same server calculations and preferences.

This feature uses transactions already saved in Ledgerra. CSV/manual imports work;
no AI key, Plaid account, external analytics service or notification subscription
is required. Bank feeds must first be reviewed and imported, as before.

## What is included

- Net spending, income and cash flow, plus category and payee spending breakdowns.
- Same-day spending comparisons across up to three prior months with recorded
  income or spending. At least two recorded prior months are needed for change
  alerts. Missing months are not silently filled with zeroes. A month with activity
  is not a guarantee of complete imported history; keep your ledger current.
- An interactive cumulative spending chart, optional comparison/pace lines and an
  expandable daily data table. No future spending is drawn as actual spending.
- Category budget warnings, overspending alerts and current-month budget-pace
  warnings. Available budget includes valid positive carryover from consecutive
  prior budget periods. Overspending takes precedence over a near-budget/pace alert
  for that category. An explicit zero available budget can still be exceeded.
- Large-purchase alerts and unusually large purchases relative to a category's
  prior median. An unusual-purchase comparison needs five ordinary categorized
  expense purchases in the three prior months. Split parents can trigger a
  whole-purchase large alert, but are not used in ordinary category medians.
  Refunds and income never trigger purchase alerts. This is a review heuristic,
  not fraud detection or a claim that a charge is unauthorized.
- Configurable alert switches/thresholds, dismissals and restoration, saved per
  user and included in JSON backups. No email, SMS or push notifications are sent.

## How calculations work

Expenses increase spending; refunds reduce it; transfers are excluded. Parent
split amounts determine the total while allocation children determine category
spending, so a split is not counted twice. Payee totals use parent transactions.
Entries without an assigned payee are grouped as **Unassigned payee** rather than
inventing a merchant identity from an arbitrary note. Accounts marked **Exclude
from budget** are excluded from Insights.

Calendar boundaries use the browser's IANA time zone and calendar date, shown on
Insights. Transaction review links preserve that time zone; a purchase just after
midnight UTC can correctly belong to the previous local day/month. Date grouping
never rewrites stored timestamps. API clients that omit `timeZone` use UTC.
The current-month selector now follows the browser calendar rather than UTC.

The current month includes entries through today, excluding later local dates.
Past months are measured through month end; future months show no actual spending.
Comparisons use the same day number of each recorded prior month, clamped to that
month's last day. Negative comparison baselines do not generate percentage-increase
alerts; a zero baseline can produce a **new spending** alert after the minimum
absolute increase is reached.

After at least seven calendar days, an incomplete current month with positive net
spending can show a straight-line estimate: net spending / elapsed days × days in
month. It is not a bill forecast, an income forecast or a promise about your future
balance. No projection is shown for closed/future months, nonpositive spending,
missing exchange rates or the first six days. Refunds may reduce a spending line.

Amounts use your profile currency and monthly FX rates. An older usable rate is
identified with a warning. Missing rates exclude the affected amounts, label totals
as known amounts, and pause monetary alerts, comparisons, budget balances and
projections until rates are supplied. Amounts are rounded to two decimal places;
individually converted split allocations can have small rounding differences from
converted parent totals. Other existing reports retain their own date/filter behavior.

## Configure and review alerts

In **Alert preferences**, enable each alert group and set:

| Preference | Default |
|---|---:|
| Budget warning threshold | 80% of available budget |
| Category spending increase | 30% and at least 50 currency units |
| Large purchase | 500 currency units |
| Unusual purchase minimum | 100 currency units |
| Unusual purchase multiplier | 3× category median |

Amounts are denominated in the current profile currency. If you change that
currency, review your thresholds; they are not automatically converted.

**Review transactions** opens the supporting category/date range, or the exact
purchase for a purchase alert. **Show all matching transactions** exits an exact
purchase focus. **Review budget** opens Budgets for the selected global month.
Individual-purchase links do not silently create or edit transactions.

**Dismiss alert** hides that alert for its month and currency. Budget-warning and
over-budget stages have distinct identities: dismissing a warning does not suppress
later overspending. Enable **Show dismissed alerts** and choose **Restore alert**
to undo a dismissal. Only currently applicable alerts are listed; an old warning
that no longer applies can disappear as the ledger changes. Dismissal does not
freeze the underlying amount or block future months' alerts. The list displays
100 applicable alerts at a time; dismiss/review active alerts to see further items.

## Data, backup and updates

The schema adds `InsightPreferences` and `InsightDismissals`; it does not reclassify,
re-date, invert, delete or otherwise migrate financial transactions. User-data reset
removes these settings for that user only. Normal updates preserve them with the
same PostgreSQL volume.

JSON backup format **7** includes alert settings/dismissals. Versions 2–7 restore
in this release; older Ledgerra versions cannot restore a version 7 file. Keep a
database backup before updating and preserve PostgreSQL and bank keyring volumes.
Older JSON archives without the new fields restore default alert preferences.
Bank connection tokens remain excluded from portable JSON archives as in v0.16.0.
