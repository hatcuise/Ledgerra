import { useEffect, useState } from "react";
import { apiClient } from "../api/client";
import { SectionCard } from "../ui/SectionCard";

type Candidate = { outgoingId: string; incomingId: string; fromAccount: string; toAccount: string; amount: number; currencyCode: string; outgoingDate: string; incomingDate: string; outgoingNote: string | null; incomingNote: string | null; canAutoLink: boolean; reason: string };
type History = { id: string; automatic: boolean; fromAccount: string; toAccount: string; amount: number; currencyCode: string };
export function TransferSuggestions({ token, onChanged }: { token: string; onChanged: () => Promise<void> }) {
  const [candidates, setCandidates] = useState<Candidate[]>([]); const [history, setHistory] = useState<History[]>([]);
  const [days, setDays] = useState(3); const [busy, setBusy] = useState(false); const [error, setError] = useState(""); const [message, setMessage] = useState(""); const [scanned, setScanned] = useState(false);
  const load = async () => { setCandidates(await apiClient.bookkeeping<Candidate[]>(token, `transfers/candidates?dayWindow=${days}`)); setHistory(await apiClient.bookkeeping<History[]>(token, "transfers/recognized")); setScanned(true); };
  useEffect(() => { if (token) void apiClient.bookkeeping<History[]>(token, "transfers/recognized").then(setHistory).catch(e => setError(e.message)); }, [token]);
  const run = async (action: () => Promise<void>) => { setBusy(true); setError(""); setMessage(""); try { await action(); await load(); await onChanged(); } catch (e) { setError(e instanceof Error ? e.message : "Transfer recognition failed."); } finally { setBusy(false); } };
  const money = (amount: number, currency: string) => new Intl.NumberFormat(undefined, { style: "currency", currency }).format(amount);
  return <SectionCard title="Recognize transfers across your banks">
    <p>Find opposite entries already added to your accounts. New imports and ordinary entries automatically link only mutually unique matches with a shared bank reference or transfer descriptions identifying both owned accounts. Equal amounts alone require review.</p>
    <label>Transfer matching date gap (days)<input type="number" min={0} max={7} value={days} disabled={busy} onChange={e => { setDays(Math.min(7, Math.max(0, Number(e.target.value) || 0))); setScanned(false); setCandidates([]); setMessage(""); }} /></label>
    <div className="form-actions"><button disabled={busy || !token} onClick={() => void run(async () => { setMessage("Transfer scan complete."); })}>Find matching transfers</button>
      <button disabled={busy || !scanned || !candidates.some(c => c.canAutoLink)} onClick={() => void run(async () => { const result = await apiClient.bookkeeping<{ linked: number }>(token, `transfers/recognize?dayWindow=${days}`, "POST"); setMessage(`${result.linked} clear transfer match(es) linked.`); })}>Automatically link clear matches</button></div>
    {error && <p className="error-banner" role="alert">{error}</p>}{message && <p role="status">{message}</p>}
    {scanned && candidates.length === 0 && <p>No unmatched transfer candidates found.</p>}
    {candidates.map(c => <div className="transfer-candidate" key={`${c.outgoingId}-${c.incomingId}`}>
      <strong>{c.fromAccount} → {c.toAccount}: {money(c.amount, c.currencyCode)}</strong>
      <p>{c.outgoingDate.slice(0, 10)} {c.outgoingNote} ↔ {c.incomingDate.slice(0, 10)} {c.incomingNote}</p>
      <p>{c.reason} {c.canAutoLink ? "Clear match." : "Review required."}</p>
      <button disabled={busy} onClick={() => void run(async () => { await apiClient.bookkeeping(token, "transfers/link", "POST", { outgoingId: c.outgoingId, incomingId: c.incomingId }); setMessage("Selected transfer linked."); })}>Link this pair</button>
    </div>)}
    {history.length > 0 && <details><summary>Matched transfers ({history.length})</summary>{history.map(h => <p key={h.id}>{h.fromAccount} → {h.toAccount}: {money(h.amount, h.currencyCode)} ({h.automatic ? "automatic" : "reviewed"}) <button disabled={busy} onClick={() => void run(async () => { await apiClient.bookkeeping(token, `transfers/${h.id}/unlink`, "POST"); setMessage("Transfer match undone. Both original transactions were preserved."); })}>Undo match</button></p>)}</details>}
  </SectionCard>;
}
