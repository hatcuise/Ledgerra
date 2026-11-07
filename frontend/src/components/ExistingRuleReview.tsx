import { useState } from "react";
import { apiClient } from "../api/client";
import type { Category } from "../types";
import { SectionCard } from "../ui/SectionCard";
type Change = { id: string; note: string | null; amount: number; occurredOnUtc: string; oldCategoryId: string | null; oldType: string; newCategoryId: string; newType: string; ruleName: string; fingerprint: string };
export function ExistingRuleReview({ token, categories, onChanged }: { token: string; categories: Category[]; onChanged: () => Promise<void> }) {
  const [from, setFrom] = useState(""); const [to, setTo] = useState(""); const [rows, setRows] = useState<Change[] | null>(null);
  const [selected, setSelected] = useState<Set<string>>(new Set()); const [busy, setBusy] = useState(false); const [error, setError] = useState(""); const [message, setMessage] = useState("");
  const run = async (action: () => Promise<void>) => { setBusy(true); setError(""); setMessage(""); try { await action(); } catch (e) { setRows(null); setSelected(new Set()); setError(e instanceof Error ? e.message : "Operation failed."); } finally { setBusy(false); } };
  const name = (id: string | null) => categories.find(c => c.id === id)?.name ?? "Uncategorized";
  return <SectionCard title="Apply rules to existing transactions">
    <p>Preview your active import rules against already saved ordinary transactions. Splits, transfers and reconciled entries are excluded. Review type changes carefully: they can change account balances. No changes are made until you select and apply them.</p>
    {error && <p role="alert">{error}</p>}{message && <p role="status">{message}</p>}
    <div className="form-grid"><label>Rule review from<input type="date" value={from} disabled={busy} onChange={e => { setFrom(e.target.value); setRows(null); setSelected(new Set()); }} /></label>
      <label>Rule review to<input type="date" value={to} disabled={busy} onChange={e => { setTo(e.target.value); setRows(null); setSelected(new Set()); }} /></label>
      <button type="button" disabled={busy} onClick={() => void run(async () => { setRows(await apiClient.bookkeeping<Change[]>(token, "rules/preview", "POST", { from: from || null, to: to || null })); setSelected(new Set()); })}>Preview existing transaction rules</button>
    </div>
    {rows && <><p>{rows.length} proposed changes.</p><div className="table-scroll"><table><thead><tr><th>Select</th><th>Date / note / amount</th><th>Current</th><th>Proposed</th><th>Rule</th></tr></thead><tbody>{rows.map(row => <tr key={row.id}>
      <td><input type="checkbox" disabled={busy} aria-label={`Apply rule to ${row.note ?? row.id}`} checked={selected.has(row.id)} onChange={e => setSelected(current => { const next = new Set(current); if (e.target.checked) next.add(row.id); else next.delete(row.id); return next; })} /></td>
      <td>{row.occurredOnUtc.slice(0, 10)} {row.note} {row.amount}</td><td>{row.oldType} · {name(row.oldCategoryId)}</td><td>{row.newType} · {name(row.newCategoryId)}</td><td>{row.ruleName}</td>
    </tr>)}</tbody></table></div><button type="button" disabled={busy || !selected.size} onClick={() => void run(async () => { const result = await apiClient.bookkeeping<{ updated: number }>(token, "rules/apply", "POST", { from: from || null, to: to || null, selected: rows.filter(row => selected.has(row.id)).map(row => ({ id: row.id, fingerprint: row.fingerprint })) }); setRows(null); setSelected(new Set()); setMessage(`${result.updated} transactions updated.`); await onChanged(); })}>Apply selected rule changes</button></>}
  </SectionCard>;
}
