import { ExistingRuleReview } from "../components/ExistingRuleReview";
import { PayeeManager } from "../components/PayeeManager";
import { TransferSuggestions } from "../components/TransferSuggestions";
import { useEffect, useMemo, useState } from "react";
import { apiClient } from "../api/client";
import { useAuth } from "../state/AuthContext";
import { useLedgerraData } from "../hooks/useLedgerraData";
import { PageHeader } from "../ui/PageHeader";
import { SectionCard } from "../ui/SectionCard";

type Metadata = { payeeId: string | null; reference: string; tags: string; status: string; reconciliationId: string | null };
type Entry = { id: string; categoryId: string | null; amount: number; type: string; note: string | null; occurredOnUtc: string; balance: number; metadata: Metadata };
type Statement = { id: string; statementDate: string; statementBalance: number };
type Register = { name: string; currencyCode: string; openingBalance: number; balance: number; entries: Entry[]; reconciliations: Statement[] };
type Payee = { id: string; name: string; defaultCategoryId: string | null };
const signed = (e: Entry) => ["Expense", "TransferOut"].includes(e.type) ? -e.amount : e.amount;
const cents = (n: number) => Math.round(n * 100);

export function BookkeepingPage() {
  const { auth } = useAuth();
  const { accounts, categories } = useLedgerraData({ accounts: true, categories: true });
  const token = auth?.accessToken ?? "";
  const [accountId, setAccountId] = useState("");
  const [register, setRegister] = useState<Register | null>(null);
  const [payees, setPayees] = useState<Payee[]>([]);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [statementDate, setStatementDate] = useState(new Date().toISOString().slice(0, 10));
  const [statementBalance, setStatementBalance] = useState("");
  const [bulkPayee, setBulkPayee] = useState("");
  const [bulkType, setBulkType] = useState("");
  const [bulkCategory, setBulkCategory] = useState("");
  const [otherAccount, setOtherAccount] = useState("");
  const [otherEntries, setOtherEntries] = useState<Entry[]>([]);
  const [incomingId, setIncomingId] = useState("");
  const [search, setSearch] = useState("");
  const [error, setError] = useState("");
  const [message, setMessage] = useState("");
  const [busy, setBusy] = useState(false);
  const load = async () => {
    if (accountId) setRegister(await apiClient.bookkeeping<Register>(token, `accounts/${accountId}/register`));
    setPayees(await apiClient.bookkeeping<Payee[]>(token, "payees"));
  };
  useEffect(() => { setRegister(null); setSelected(new Set()); if (token) void load().catch(e => setError(e.message)); }, [accountId, token]);
  useEffect(() => { setOtherEntries([]); setIncomingId(""); if (otherAccount) void apiClient.bookkeeping<Register>(token, `accounts/${otherAccount}/register`).then(r => setOtherEntries(r.entries)).catch(e => setError(e.message)); }, [otherAccount, token]);
  const run = async (action: () => Promise<void>) => { setBusy(true); setError(""); setMessage(""); try { await action(); await load(); } catch (e) { setError(e instanceof Error ? e.message : "Operation failed."); } finally { setBusy(false); } };
  const money = (n: number) => new Intl.NumberFormat(undefined, { style: "currency", currency: register?.currencyCode ?? "USD" }).format(n);
  const selectedEntries = register?.entries.filter(e => selected.has(e.id)) ?? [];
  const reconciled = register?.entries.filter(e => e.metadata.status === "Reconciled") ?? [];
  const calculatedCents = cents(register?.openingBalance ?? 0) + [...reconciled, ...selectedEntries].reduce((n, e) => n + cents(signed(e)), 0);
  const differenceCents = statementBalance === "" ? null : cents(Number(statementBalance)) - calculatedCents;
  const visibleEntries = useMemo(() => register?.entries.filter(e => `${e.note ?? ""} ${e.type} ${e.metadata.tags} ${e.metadata.reference} ${payees.find(p => p.id === e.metadata.payeeId)?.name ?? ""}`.toLowerCase().includes(search.toLowerCase())) ?? [], [register, search, payees]);
  const setMetadata = (id: string, patch: Partial<Metadata>) => setRegister(r => r && ({ ...r, entries: r.entries.map(e => e.id === id ? { ...e, metadata: { ...e.metadata, ...patch } } : e) }));
  return <div className="page-stack bookkeeping-page">
    <PageHeader eyebrow="Bookkeeping" title="Balance your books" description="Keep payees, check cleared transactions, link card payments, and reconcile against your statement." />
    {error && <p className="error-banner" role="alert">{error}</p>}{message && <p role="status">{message}</p>}
    <ExistingRuleReview token={token} categories={categories} onChanged={load} />
    <TransferSuggestions token={token} onChanged={load} />
    <PayeeManager token={token} categories={categories} onChanged={load} />
    <SectionCard title="Account register">
      <label>Account<select value={accountId} onChange={e => setAccountId(e.target.value)}><option value="">Choose account</option>{accounts.map(a => <option key={a.id} value={a.id}>{a.name}</option>)}</select></label>
      {register && <>
        <p>Opening balance: {money(register.openingBalance)} · Current balance: {money(register.balance)}</p>
        <p className="form-helper">Credit card debt is a negative balance. Payments from your bank are transfers, not income. Reconciled entries are locked until their statement is reopened.</p>
        <label>Search payee, note, reference, or tags<input value={search} onChange={e => setSearch(e.target.value)} /></label>
        <div className="table-scroll"><table><thead><tr><th>Select</th><th>Date / note</th><th>Type</th><th>Amount / balance</th><th>Payee</th><th>Status</th><th>Reference / tags</th><th>Save</th></tr></thead><tbody>
          {visibleEntries.map(e => <tr key={e.id}>
            <td><input aria-label={`Select ${e.note ?? e.id}`} type="checkbox" disabled={busy || e.metadata.status === "Reconciled"} checked={selected.has(e.id)} onChange={event => setSelected(s => { const next = new Set(s); if (event.target.checked) next.add(e.id); else next.delete(e.id); return next; })} /></td>
            <td>{e.occurredOnUtc.slice(0, 10)}<br />{e.note}</td><td>{e.type}</td><td>{money(signed(e))}<br />{money(e.balance)}</td>
            <td><select aria-label={`Payee ${e.id}`} value={e.metadata.payeeId ?? ""} disabled={e.metadata.status === "Reconciled"} onChange={event => setMetadata(e.id, { payeeId: event.target.value || null })}><option value="">None</option>{payees.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}</select></td>
            <td><select aria-label={`Status ${e.id}`} value={e.metadata.status} disabled={e.metadata.status === "Reconciled"} onChange={event => setMetadata(e.id, { status: event.target.value })}><option>Uncleared</option><option>Cleared</option>{e.metadata.status === "Reconciled" && <option>Reconciled</option>}</select></td>
            <td><input aria-label={`Reference ${e.id}`} maxLength={120} value={e.metadata.reference} disabled={e.metadata.status === "Reconciled"} onChange={event => setMetadata(e.id, { reference: event.target.value })} /><input aria-label={`Tags ${e.id}`} maxLength={400} value={e.metadata.tags} disabled={e.metadata.status === "Reconciled"} onChange={event => setMetadata(e.id, { tags: event.target.value })} /></td>
            <td><button disabled={busy || e.metadata.status === "Reconciled"} onClick={() => void run(async () => { await apiClient.bookkeeping(token, `entries/${e.id}`, "PUT", e.metadata); setMessage("Entry saved."); })}>Save</button></td>
          </tr>)}
        </tbody></table></div>
        <div className="form-grid">
          <label>Payee for selected<select value={bulkPayee} onChange={e => setBulkPayee(e.target.value)}><option value="">Keep current</option>{payees.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}</select></label>
          <label>Category for selected<select value={bulkCategory} onChange={e => setBulkCategory(e.target.value)}><option value="">Keep current</option>{categories.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}</select></label>
          <label>Type for selected<select value={bulkType} onChange={e => setBulkType(e.target.value)}><option value="">Keep current</option><option>Expense</option><option>Income</option><option>Refund</option></select></label>
          <button disabled={busy || !selected.size} onClick={() => void run(async () => {
            await apiClient.bookkeeping(token, "entries/bulk", "PUT", { ids: [...selected], payeeId: bulkPayee || null, categoryId: bulkCategory || null, type: bulkType || null }); setMessage("Selected entries updated.");
          })}>Apply to selected</button>
        </div>
      </>}
    </SectionCard>
    {register && <>
      <SectionCard title="Reconcile statement">
        <p>Select the transactions present on your statement. Previously reconciled entries are included automatically.</p>
        <form className="form-grid" onSubmit={e => { e.preventDefault(); void run(async () => {
          await apiClient.bookkeeping(token, `accounts/${accountId}/reconcile`, "POST", { statementDate, statementBalance: Number(statementBalance), transactionIds: [...selected] }); setSelected(new Set()); setMessage("Statement reconciled.");
        }); }}>
          <label>Statement date<input required type="date" value={statementDate} onChange={e => setStatementDate(e.target.value)} /></label>
          <label>Statement ending balance<input required type="number" step="0.01" value={statementBalance} onChange={e => setStatementBalance(e.target.value)} /></label>
          <p>Selected balance: {money(calculatedCents / 100)} · Difference: {differenceCents === null ? "Enter statement balance" : money(differenceCents / 100)}</p>
          <button disabled={busy || differenceCents !== 0}>Finish reconciliation</button>
        </form>
        {register.reconciliations.map((s, index) => <p key={s.id}>{s.statementDate}: {money(s.statementBalance)} {index === 0 && <button disabled={busy} onClick={() => void run(async () => { await apiClient.bookkeeping(token, `reconciliations/${s.id}/reopen`, "POST"); setMessage("Latest statement reopened."); })}>Reopen statement</button>}</p>)}
      </SectionCard>
      <SectionCard title="Match imported transfers and card payments">
        <p>Select one outgoing entry in this account, then its matching incoming entry in the other account. Linking preserves both balances and removes the payment from income and spending.</p>
        <label>Other account<select value={otherAccount} onChange={e => setOtherAccount(e.target.value)}><option value="">Choose account</option>{accounts.filter(a => a.id !== accountId).map(a => <option key={a.id} value={a.id}>{a.name}</option>)}</select></label>
        <label>Incoming transaction<select value={incomingId} onChange={e => setIncomingId(e.target.value)}><option value="">Choose transaction</option>{otherEntries.filter(e => signed(e) > 0 && e.metadata.status !== "Reconciled").map(e => <option key={e.id} value={e.id}>{e.occurredOnUtc.slice(0, 10)} {e.note} {e.amount}</option>)}</select></label>
        <button disabled={busy || selected.size !== 1 || !incomingId} onClick={() => void run(async () => { await apiClient.bookkeeping(token, "transfers/link", "POST", { outgoingId: [...selected][0], incomingId }); setSelected(new Set()); setMessage("Transfer linked."); })}>Link transfer</button>
      </SectionCard>
    </>}
  </div>;
}
