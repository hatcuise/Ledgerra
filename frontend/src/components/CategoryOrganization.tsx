import { useState } from "react";
import { apiClient } from "../api/client";
import type { Category } from "../types";
export function CategoryOrganization({ token, categories, onChanged }: { token: string; categories: Category[]; onChanged: () => Promise<void> }) {
  const [id, setId] = useState(""); const [parent, setParent] = useState(""); const [target, setTarget] = useState("");
  const [busy, setBusy] = useState(false); const [error, setError] = useState(""); const [message, setMessage] = useState("");
  const selected = categories.find(c => c.id === id);
  const candidates = categories.filter(c => c.id !== id && c.kind === selected?.kind);
  const path = (category: Category) => { const names = [category.name]; const seen = new Set([category.id]); let parentId = category.parentCategoryId; while (parentId && !seen.has(parentId)) { seen.add(parentId); const p = categories.find(c => c.id === parentId); if (!p) break; names.unshift(p.name); parentId = p.parentCategoryId; } return names.join(" / "); };
  const run = async (action: () => Promise<void>) => { setBusy(true); setError(""); setMessage(""); try { await action(); await onChanged(); } catch (e) { setError(e instanceof Error ? e.message : "Unable to organize categories."); } finally { setBusy(false); } };
  return <details className="section-card category-organization"><summary>Category hierarchy and merge</summary>
    <p>Parent categories organize your taxonomy. Budgets and reports currently show each category separately. Merging moves transactions, split allocations, rules, schedules, payee defaults, saved filters and transfer history to the target.</p>
    {error && <p role="alert">{error}</p>}{message && <p role="status">{message}</p>}
    <label>Category to organize<select value={id} onChange={e => { const c = categories.find(c => c.id === e.target.value); setId(c?.id ?? ""); setParent(c?.parentCategoryId ?? ""); setTarget(""); }}><option value="">Choose category</option>{categories.map(c => <option key={c.id} value={c.id}>{path(c)}</option>)}</select></label>
    {selected && <div className="form-grid"><label>Parent category<select value={parent} onChange={e => setParent(e.target.value)}><option value="">Top level</option>{candidates.map(c => <option key={c.id} value={c.id}>{path(c)}</option>)}</select></label>
      <button type="button" disabled={busy} onClick={() => void run(async () => { await apiClient.bookkeeping(token, `categories/${id}/parent`, "PUT", { parentId: parent || null }); setMessage("Category hierarchy saved."); })}>Save parent category</button>
      <label>Merge into category<select value={target} onChange={e => setTarget(e.target.value)}><option value="">Choose target</option>{candidates.map(c => <option key={c.id} value={c.id}>{path(c)}</option>)}</select></label>
      <button type="button" disabled={busy || !target} onClick={() => { if (window.confirm("Move every use of this category to the target, then remove this category? Conflicting budget limits must be combined first.")) void run(async () => { await apiClient.bookkeeping(token, `categories/${id}/merge`, "POST", { targetId: target }); setId(""); setTarget(""); setMessage("Categories merged."); }); }}>Merge categories</button>
    </div>}
  </details>;
}
