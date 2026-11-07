import { useEffect, useState } from "react";
import { apiClient } from "../api/client";
type View = { id: string; name: string; query: string };
export function SavedLedgerViews({ token, onApply }: { token: string; onApply: (query: URLSearchParams) => void }) {
  const [views, setViews] = useState<View[]>([]);
  const [name, setName] = useState("");
  const [selected, setSelected] = useState("");
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const load = () => apiClient.bookkeeping<View[]>(token, "views").then(setViews);
  useEffect(() => { let active = true; void apiClient.bookkeeping<View[]>(token, "views").then(items => { if (active) setViews(items); }).catch(e => { if (active) setError(e instanceof Error ? e.message : "Unable to load saved filters."); }); return () => { active = false; }; }, [token]);
  const run = async (action: () => Promise<void>) => { setBusy(true); setError(""); try { await action(); await load(); } catch (e) { setError(e instanceof Error ? e.message : "Unable to save filters."); } finally { setBusy(false); } };
  return <section className="transaction-filter-section" aria-label="Named saved filters">
    <strong>Named saved filters</strong>
    {error && <p role="alert">{error}</p>}
    <label>Saved filter<select value={selected} onChange={event => { setSelected(event.target.value); const view = views.find(item => item.id === event.target.value); if (view) { onApply(new URLSearchParams(view.query)); setName(view.name); } }}><option value="">Choose saved filter</option>{views.map(view => <option key={view.id} value={view.id}>{view.name}</option>)}</select></label>
    <button type="button" disabled={busy || !selected} onClick={() => { const view = views.find(item => item.id === selected); if (view) onApply(new URLSearchParams(view.query)); }}>Apply saved filter</button>
    <label>Filter name<input maxLength={120} value={name} onChange={event => setName(event.target.value)} /></label>
    <button type="button" disabled={busy || !name.trim()} onClick={() => void run(async () => { const view = await apiClient.bookkeeping<View>(token, "views", "POST", { name, query: window.location.search }); setSelected(view.id); })}>Save named filter</button>
    <button type="button" disabled={busy || !selected} onClick={() => void run(async () => { await apiClient.bookkeeping(token, `views/${selected}`, "DELETE"); setSelected(""); setName(""); })}>Delete saved filter</button>
    <small>Saved to your account and included in backups. Saving an existing name replaces its filters.</small>
  </section>;
}
