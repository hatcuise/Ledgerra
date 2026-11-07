import { useEffect, useState } from "react";
import { apiClient } from "../api/client";
import type { Category } from "../types";
import { SectionCard } from "../ui/SectionCard";
type Payee = { id: string; name: string; defaultCategoryId: string | null };
export function PayeeManager({ token, categories, onChanged }: { token: string; categories: Category[]; onChanged: () => Promise<void> }) {
  const [payees, setPayees] = useState<Payee[]>([]);
  const [id, setId] = useState("");
  const [name, setName] = useState("");
  const [categoryId, setCategoryId] = useState("");
  const [target, setTarget] = useState("");
  const [tags, setTags] = useState<string[]>([]);
  const [tag, setTag] = useState("");
  const [tagName, setTagName] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [message, setMessage] = useState("");
  const load = async () => { const [p, t] = await Promise.all([apiClient.bookkeeping<Payee[]>(token, "payees"), apiClient.bookkeeping<string[]>(token, "tags")]); setPayees(p); setTags(t); };
  useEffect(() => { if (token) void load().catch(() => {}); }, [token]);
  const reset = () => { setId(""); setName(""); setCategoryId(""); setTarget(""); };
  const run = async (action: () => Promise<void>) => { setBusy(true); setError(""); setMessage(""); try { await action(); await load(); await onChanged(); } catch (e) { setError(e instanceof Error ? e.message : "Operation failed."); } finally { setBusy(false); } };
  return <SectionCard title="Payees and tags">
    {error && <p role="alert">{error}</p>}{message && <p role="status">{message}</p>}
    <label>Edit payee<select value={id} onChange={event => { const payee = payees.find(p => p.id === event.target.value); setId(payee?.id ?? ""); setName(payee?.name ?? ""); setCategoryId(payee?.defaultCategoryId ?? ""); setTarget(""); }}><option value="">New payee</option>{payees.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}</select></label>
    <form className="form-grid" onSubmit={event => { event.preventDefault(); void run(async () => { await apiClient.bookkeeping(token, id ? `payees/${id}` : "payees", id ? "PUT" : "POST", { name, defaultCategoryId: categoryId || null }); reset(); setMessage("Payee saved."); }); }}>
      <label>Payee name<input required maxLength={120} value={name} onChange={e => setName(e.target.value)} /></label>
      <label>Default category<select value={categoryId} onChange={e => setCategoryId(e.target.value)}><option value="">None</option>{categories.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}</select></label>
      <button disabled={busy || !name.trim()}>Save payee</button>
    </form>
    {id && <div className="form-grid">
      <button type="button" disabled={busy} onClick={() => void run(async () => { await apiClient.bookkeeping(token, `payees/${id}`, "DELETE"); reset(); setMessage("Unused payee deleted."); })}>Delete unused payee</button>
      <label>Merge into payee<select value={target} onChange={e => setTarget(e.target.value)}><option value="">Choose target</option>{payees.filter(p => p.id !== id).map(p => <option key={p.id} value={p.id}>{p.name}</option>)}</select></label>
      <button type="button" disabled={busy || !target} onClick={() => { if (window.confirm("Move all transactions to the target payee and remove this payee? The target's default category stays unchanged.")) void run(async () => { await apiClient.bookkeeping(token, `payees/${id}/merge`, "POST", { targetId: target }); reset(); setMessage("Payees merged; transaction amounts are unchanged."); }); }}>Merge payees</button>
    </div>}
    <details><summary>Manage transaction tags</summary><p>Tags are comma separated. Renaming to an existing tag combines them; removing a tag leaves its transactions intact.</p>
      <label>Existing tag<select value={tag} onChange={e => { setTag(e.target.value); setTagName(e.target.value); }}><option value="">Choose tag</option>{tags.map(t => <option key={t}>{t}</option>)}</select></label>
      <label>New tag name<input maxLength={60} value={tagName} onChange={e => setTagName(e.target.value)} /></label>
      <button type="button" disabled={busy || !tag || !tagName.trim()} onClick={() => void run(async () => { await apiClient.bookkeeping(token, "tags/rename", "POST", { name: tag, newName: tagName }); setTag(""); setTagName(""); setMessage("Tag renamed."); })}>Rename or merge tag</button>
      <button type="button" disabled={busy || !tag} onClick={() => { if (window.confirm(`Remove the tag "${tag}" from all your transactions?`)) void run(async () => { await apiClient.bookkeeping(token, "tags/rename", "POST", { name: tag, newName: "" }); setTag(""); setTagName(""); setMessage("Tag removed."); }); }}>Remove tag</button>
    </details>
  </SectionCard>;
}
