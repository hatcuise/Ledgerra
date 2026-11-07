import { useState } from "react";
import { Link } from "react-router-dom";
import { apiClient } from "../api/client";
import { useAuth } from "../state/AuthContext";
import { useImportAnalysis } from "../state/ImportAnalysisContext";
import type { Account, MonthlyReportAnalysis } from "../types";
import { SectionCard } from "../ui/SectionCard";

export type NativeInspection = { format: string; accounts: { key: string; name: string; currency: string | null; openingBalance: number | null; transactionCount: number }[]; warnings: string[] };
export function NativeImportPanel({ accounts }: { accounts: Account[] }) {
  const { auth } = useAuth(); const { applyAnalysis, clearReviewSession, isCommitting, isAnalyzing } = useImportAnalysis();
  const [file, setFile] = useState<File | null>(null); const [encoding, setEncoding] = useState("utf-8"); const [dateOrder, setDateOrder] = useState("mdy");
  const [inspection, setInspection] = useState<NativeInspection | null>(null); const [mappings, setMappings] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState(false); const [error, setError] = useState(""); const [message, setMessage] = useState(""); const [warnings, setWarnings] = useState<string[]>([]);
  const disabled = busy || isAnalyzing || isCommitting;
  const formData = () => { const data = new FormData(); data.append("file", file!); data.append("encoding", encoding); data.append("dateOrder", dateOrder); return data; };
  const reset = () => { clearReviewSession(); setInspection(null); setMappings({}); setWarnings([]); setMessage(""); setError(""); };
  return <SectionCard title="Import QIF, OFX, QFX, or HomeBank">
    <p>Map each source account before previewing. <Link to="/accounts">Create destination accounts first</Link>. Existing opening balances are never changed by transaction import.</p>
    <fieldset disabled={disabled} className="stack-form csv-import-settings">
      <label>Native statement file<input type="file" accept=".qif,.ofx,.qfx,.xhb" onChange={e => { setFile(e.target.files?.[0] ?? null); reset(); }} /></label>
      <div className="form-grid"><label>File encoding<select value={encoding} onChange={e => { setEncoding(e.target.value); reset(); }}><option value="utf-8">UTF-8 / Unicode BOM</option><option value="windows-1252">Windows-1252</option></select></label>
        <label>QIF date order<select value={dateOrder} onChange={e => { setDateOrder(e.target.value); reset(); }}><option value="mdy">Month/day/year</option><option value="dmy">Day/month/year</option></select></label></div>
      <button type="button" disabled={!file || !auth} onClick={async () => {
        setBusy(true); setError(""); setMessage(""); try {
          const result = await apiClient.nativeImport<NativeInspection>(auth!.accessToken, "inspect", formData()); setInspection(result); setWarnings(result.warnings);
          setMappings(Object.fromEntries(result.accounts.map(source => {
            const matching = accounts.filter(a => a.name.toLowerCase() === source.name.toLowerCase() && (!source.currency || a.currencyCode === source.currency));
            return [source.key, matching.length === 1 ? matching[0].id : ""];
          })));
        } catch (e) { setError(e instanceof Error ? e.message : "Could not inspect statement."); } finally { setBusy(false); }
      }}>{busy ? "Reading statement…" : "Inspect native file"}</button>
      {inspection && <>
        <p>{inspection.format}: {inspection.accounts.length} source account(s).</p>
        {inspection.accounts.map(source => <label key={source.key}>Map {source.name} {source.currency ? `(${source.currency})` : ""} — {source.transactionCount} transactions
          {source.openingBalance !== null && <span className="form-helper">Source opening balance: {source.openingBalance}. Review the destination opening balance separately.</span>}
          <select aria-label={`Map ${source.name}`} value={mappings[source.key] ?? ""} onChange={e => { clearReviewSession(); setMappings(current => ({ ...current, [source.key]: e.target.value })); }}>
            <option value="">Choose destination</option><option value="skip">Skip this source account</option>
            {accounts.filter(a => !source.currency || a.currencyCode === source.currency).map(a => <option key={a.id} value={a.id}>{a.name} ({a.currencyCode})</option>)}
          </select>
        </label>)}
        <button type="button" disabled={!auth || inspection.accounts.some(a => !mappings[a.key]) || Object.values(mappings).every(v => v === "skip")} onClick={async () => {
          setBusy(true); setError(""); try {
            const data = formData(); data.append("accountMappings", JSON.stringify(Object.fromEntries(Object.entries(mappings).map(([k, v]) => [k, v === "skip" ? null : v]))));
            const preview = await apiClient.nativeImport<MonthlyReportAnalysis>(auth!.accessToken, "preview", data); applyAnalysis(preview); setWarnings(preview.warnings); setMessage("Review the transaction drafts below, then save only the entries you accept.");
          } catch (e) { setError(e instanceof Error ? e.message : "Could not preview statement."); } finally { setBusy(false); }
        }}>Preview native transactions</button>
      </>}
    </fieldset>
    {error && <p className="error-banner" role="alert">{error}</p>}{message && <p role="status">{message}</p>}
    {warnings.length > 0 && <ul className="form-helper">{warnings.map(w => <li key={w}>{w}</li>)}</ul>}
  </SectionCard>;
}
