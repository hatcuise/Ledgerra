import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { apiClient } from "../api/client";
import { useMonthSelection } from "../state/MonthContext";
import { useSpendingInsights } from "../hooks/useSpendingInsights";
import { SpendingPaceChart } from "../components/SpendingPaceChart";
import { PageHeader } from "../ui/PageHeader";
import { MetricCard } from "../ui/MetricCard";
import { SectionCard } from "../ui/SectionCard";
import { formatCurrency } from "../utils/format";
import type { InsightPreferences, SpendingAlert } from "../types/insights";

function transactionLink(alert: SpendingAlert, month: string, through: string | null, timeZone: string) {
  if (alert.transactionId) return `/transactions?entry=${alert.transactionId}`;
  const query = new URLSearchParams({ from: `${month}-01`, to: through ?? `${month}-01`, timeZone });
  if (alert.categoryId) query.set("categoryId", alert.categoryId);
  if (alert.transactionId) query.set("entry", alert.transactionId);
  return `/transactions?${query}`;
}
export function InsightsPage() {
  const { selectedMonth, setSelectedMonth } = useMonthSelection();
  const { data, error, loading, refresh, token, today, timeZone } = useSpendingInsights(selectedMonth);
  const [preferences, setPreferences] = useState<InsightPreferences | null>(null);
  const [busy, setBusy] = useState(false); const [actionError, setActionError] = useState(""); const [message, setMessage] = useState("");
  const [dismissed, setDismissed] = useState(false);
  const loadPreferences = () => apiClient.insights<InsightPreferences>(token, "/preferences").then(setPreferences);
  useEffect(() => { let active = true; if (token) void apiClient.insights<InsightPreferences>(token, "/preferences").then(value => { if (active) setPreferences(value); }).catch(e => { if (active) setActionError(e.message); }); return () => { active = false; }; }, [token]);
  const run = async (action: () => Promise<void>) => { setBusy(true); setActionError(""); setMessage(""); try { await action(); await refresh(); } catch (e) { setActionError(e instanceof Error ? e.message : "Unable to update insights."); } finally { setBusy(false); } };
  const money = (value: number | null) => value === null ? "—" : formatCurrency(value, data?.currencyCode ?? "USD");
  const alerts = data?.alerts.filter(alert => alert.dismissed === dismissed) ?? [];
  return <div className="page-stack insights-page">
    <PageHeader eyebrow="Spending insights" title="Know where your money is going" description="Spot spending changes, review large purchases, and catch budget pressure using your recorded transactions." actions={<div className="insight-page-actions"><label>Insights month<input type="month" value={selectedMonth} min="1900-01" max="9998-12" onChange={event => setSelectedMonth(event.target.value)} /></label><button type="button" className="ghost-button" disabled={loading || busy} onClick={() => void refresh()}>Refresh insights</button></div>} />
    {error && <p className="error-banner" role="alert">{error} <button type="button" onClick={() => void refresh()}>Retry insights</button></p>}
    {actionError && <p className="error-banner" role="alert">{actionError}</p>}{message && <p className="success-banner" role="status">{message}</p>}
    {loading && <p role="status">Calculating spending insights…</p>}
    {data && <>
      <div className="insight-context"><strong>{data.month} · {data.currencyCode} · {data.timeZone ?? "UTC"}</strong><span>{data.throughDate ? `Recorded through ${data.throughDate}` : "Future month — no recorded spending included"}</span></div>
      {data.warnings.length > 0 && <div className="insight-coverage" role="status"><strong>{data.dataComplete ? "Exchange-rate notes" : "Incomplete currency coverage"}</strong><ul>{data.warnings.map(warning => <li key={warning}>{warning}</li>)}</ul><Link to="/settings">Review exchange rates</Link></div>}
      <div className="metric-grid">
        <MetricCard label={data.dataComplete ? "Net spending" : "Known net spending"} value={money(data.netSpending)} detail="Expenses minus refunds; transfers excluded" />
        <MetricCard label="Prior-month average" value={money(data.historicalAverage)} detail={data.historicalMonths >= 2 ? `Same-day comparison across ${data.historicalMonths} recorded prior months` : "Needs activity in at least two of the three prior months"} />
        <MetricCard label={data.dataComplete ? "Cash flow" : "Known cash flow"} value={money(data.cashFlow)} tone={data.cashFlow >= 0 ? "positive" : "negative"} detail={`Recorded income ${money(data.income)} minus net spending`} />
        <MetricCard label="Estimated month-end spending" value={money(data.projectedMonthEnd)} detail={data.projectedMonthEnd !== null ? `Straight-line estimate after ${data.elapsedDays} calendar days` : "Available after day 7 of an incomplete current month"} />
      </div>
      <SectionCard title="Your spending pace">
        <p>Compare cumulative net spending with recorded prior months. Only existing ledger data is measured; unimported activity may be missing. Transfers never count as spending and refunds can lower the line.</p>
        <SpendingPaceChart data={data} />
        <details><summary>Daily spending data</summary><div className="table-scroll"><table><thead><tr><th>Day</th><th>Net spending so far</th><th>Prior average</th><th>Estimated pace</th></tr></thead><tbody>{data.daily.map(day => <tr key={day.day}><td>{day.day}</td><td>{money(day.current)}</td><td>{money(day.historicalAverage)}</td><td>{money(day.projected)}</td></tr>)}</tbody></table></div></details>
        <p className="form-helper">The pace estimate assumes spending continues at the same daily rate. It does not know future bills, income or purchases. Historical comparisons use only months with recorded income or spending; missing months are not zero-spending months.</p>
      </SectionCard>
      <SectionCard title="Spending alerts" actions={<label className="insight-dismissed-toggle"><input type="checkbox" checked={dismissed} onChange={e => setDismissed(e.target.checked)} />Show dismissed alerts</label>}>
        <p>{alerts.length} {dismissed ? "dismissed" : "active"} alert{alerts.length === 1 ? "" : "s"}. Dismissing a budget warning does not dismiss a later over-budget alert.</p>
        {alerts.length === 0 && <p>{dismissed ? "No dismissed alerts currently apply to this month." : data.dataComplete ? "No active spending alerts. Check your preferences or keep adding transactions." : "Monetary alerts are paused until the missing exchange rates are supplied."}</p>}
        <div className="spending-alert-list">{alerts.slice(0, 100).map(alert => <article key={alert.key} className={`spending-alert spending-alert--${alert.severity}`}>
          <div><span className="eyebrow">{alert.kind.replace(/([a-z])([A-Z])/g, "$1 $2")}</span><h3>{alert.title}</h3><p>{alert.description}</p></div>
          <dl className="insight-alert-values"><div><dt>Spending / purchase</dt><dd>{money(alert.amount)}</dd></div>{alert.baseline !== null && <div><dt>{alert.kind.startsWith("Budget") ? "Available budget" : alert.kind === "UnusualPurchase" ? "Typical purchase (median)" : alert.kind === "LargePurchase" ? "Your threshold" : "Prior average"}</dt><dd>{money(alert.baseline)}</dd></div>}{alert.projected !== null && <div><dt>Estimated month end</dt><dd>{money(alert.projected)}</dd></div>}</dl>
          {alert.sampleCount > 0 && <p className="form-helper">Based on {alert.sampleCount} {alert.kind === "UnusualPurchase" ? "prior purchases" : "recorded prior months"}.</p>}
          <div className="transaction-form-actions"><Link to={transactionLink(alert, selectedMonth, data.throughDate, data.timeZone ?? timeZone)}>Review transactions</Link>{alert.kind.startsWith("Budget") && <Link to="/budgets">Review budget</Link>}
            <button className="ghost-button compact-button" type="button" disabled={busy} onClick={() => void run(async () => { if (dismissed) await apiClient.insights(token, `/dismiss/${alert.key}`, "DELETE"); else await apiClient.insights(token, "/dismiss", "POST", { key: alert.key, month: selectedMonth, today, timeZone }); setMessage(dismissed ? "Alert restored." : `Alert dismissed for ${selectedMonth}.`); })}>{dismissed ? "Restore alert" : "Dismiss alert"}</button>
          </div>
        </article>)}</div>{alerts.length > 100 && <p>Showing the first 100 alerts. Review or dismiss these to see the remaining alerts.</p>}
      </SectionCard>
      <div className="insight-breakdowns">
        <SectionCard title="Category spending changes"><div className="table-scroll"><table><thead><tr><th>Category</th><th>Net spending</th><th>Prior average</th><th>Change</th><th>Budget remaining</th></tr></thead><tbody>{data.categories.map(category => <tr key={category.categoryId ?? "none"}><td><Link to={`/transactions?from=${selectedMonth}-01&to=${data.throughDate ?? selectedMonth + "-01"}&timeZone=${encodeURIComponent(data.timeZone ?? timeZone)}${category.categoryId ? `&categoryId=${category.categoryId}` : "&view=uncategorized"}`}>{category.name}</Link></td><td>{money(category.spent)}</td><td>{money(category.historicalAverage)}</td><td>{category.percentChange === null ? category.change !== null ? money(category.change) : "—" : `${category.percentChange > 0 ? "+" : ""}${category.percentChange.toFixed(1)}%`}</td><td>{money(category.budgetRemaining)}</td></tr>)}</tbody></table></div>{!data.categories.length && <p>No spending or category budgets in this period.</p>}</SectionCard>
        <SectionCard title="Spending by payee"><div className="bar-list">{data.payees.slice(0, 12).map(payee => <div className="bar-item" key={payee.payeeId ?? "none"}><div><strong>{payee.name}</strong><span>{money(payee.spent)}</span></div><small>{payee.transactions} transaction{payee.transactions === 1 ? "" : "s"}</small><div className="bar-track"><div className="bar-fill" style={{ width: `${Math.min(100, Math.max(0, payee.spent / Math.max(...data.payees.map(p => p.spent), 1) * 100))}%` }} /></div></div>)}</div>{!data.payees.length && <p>No payee spending yet.</p>}<p className="form-helper">Assign payees in transaction details or Bookkeeping for a clearer breakdown. Unassigned entries stay grouped together.</p></SectionCard>
      </div>
    </>}
    <SectionCard title="Alert preferences">
      <p>Preferences are saved to your account and included in backups. Amount thresholds use your profile currency. These alerts appear only inside Ledgerra; no emails or push messages are sent.</p>
      {preferences ? <form className="stack-form" onSubmit={e => { e.preventDefault(); void run(async () => { await apiClient.insights(token, "/preferences", "PUT", preferences); await loadPreferences(); setMessage("Alert preferences saved."); }); }}><fieldset disabled={busy}>
        <div className="insight-preference-toggles">{([ ["budgetAlerts", "Budget warnings and pace"], ["spendingChangeAlerts", "Category spending increases"], ["largePurchaseAlerts", "Large purchases"], ["unusualPurchaseAlerts", "Unusual purchases"] ] as const).map(([key, label]) => <label key={key}><input type="checkbox" checked={preferences[key]} onChange={e => setPreferences(p => p && ({ ...p, [key]: e.target.checked }))} />{label}</label>)}</div>
        <div className="form-grid">{([ ["budgetWarningPercent", "Budget warning at (%)", 1, 100], ["spendingIncreasePercent", "Spending increase at (%)", 1, 1000], ["minimumSpendingIncrease", "Minimum spending increase", 0.01, 999999999], ["largePurchaseThreshold", "Large purchase threshold", 0.01, 999999999], ["unusualPurchaseMinimum", "Unusual purchase minimum", 0.01, 999999999], ["unusualPurchaseMultiplier", "Unusual purchase multiplier", 1.5, 100] ] as const).map(([key, label, min, max]) => <label key={key}>{label}<input required type="number" step="0.01" min={min} max={max} value={preferences[key]} onChange={e => setPreferences(p => p && ({ ...p, [key]: Number(e.target.value) }))} /></label>)}</div>
        <button className="primary-button" type="submit">Save alert preferences</button>
      </fieldset></form> : <button type="button" onClick={() => void loadPreferences().catch(e => setActionError(e.message))}>Load alert preferences</button>}
    </SectionCard>
  </div>;
}
