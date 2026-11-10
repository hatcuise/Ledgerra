import { Link } from "react-router-dom";
import { useSpendingInsights } from "../hooks/useSpendingInsights";
import { formatCurrency } from "../utils/format";
import { SectionCard } from "../ui/SectionCard";
export function SpendingInsightSummary({ month, revision }: { month: string; revision: string }) {
  const { data, loading, error, refresh } = useSpendingInsights(month, revision);
  const alerts = data?.alerts.filter(alert => !alert.dismissed) ?? [];
  return <SectionCard title="Spending insights and alerts" actions={<Link to="/insights">View all insights</Link>}>
    {loading && <p role="status">Checking spending patterns…</p>}
    {error && <p role="alert">{error} <button type="button" onClick={() => void refresh()}>Retry insights</button></p>}
    {data && <>
      {!data.dataComplete && <p>Some exchange rates are missing. Monetary alerts are paused; review Insights for details.</p>}
      {data.dataComplete && alerts.length === 0 && <p>No active alerts for this month. Alert preferences and dismissed items are available in Insights.</p>}
      <div className="insight-summary-list">{alerts.slice(0, 3).map(alert => <article key={alert.key} className={`spending-alert spending-alert--${alert.severity}`}>
        <strong>{alert.title}</strong><p>{formatCurrency(alert.amount, data.currencyCode)} · {alert.description}</p>
      </article>)}</div>
      {data.projectedMonthEnd !== null && <p>At this pace: {formatCurrency(data.projectedMonthEnd, data.currencyCode)} net spending by month end. Estimate based on elapsed calendar days.</p>}
    </>}
  </SectionCard>;
}
