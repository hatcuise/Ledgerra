import { useEffect, useState } from "react";
import { apiClient } from "../api/client";
import { useAuth } from "../state/AuthContext";
import { useImportAnalysis } from "../state/ImportAnalysisContext";

import { defaultCsvOptions, type CsvOptions, type CsvProfile } from "../types/csv";

export function CsvImportSettings() {
  const { auth } = useAuth();
  const { csvOptions, setCsvOptions, dateColumn, setDateColumn, amountColumn, setAmountColumn,
    descriptionColumn, setDescriptionColumn, invertAmounts, setInvertAmounts, isAnalyzing, setError, csvHeaders } = useImportAnalysis();
  const [profiles, setProfiles] = useState<CsvProfile[]>([]);
  const [name, setName] = useState("");
  const [message, setMessage] = useState("");
  const token = auth?.accessToken;
  useEffect(() => { if (token) void apiClient.getImportProfiles(token).then(setProfiles).catch(e => setError(e.message)); }, [token]);
  const update = (key: keyof CsvOptions, value: string) => setCsvOptions(current => ({ ...current, [key]: value }));
  const column = (label: string, key: "debitColumn" | "creditColumn" | "typeColumn" | "payeeColumn" | "categoryColumn" | "referenceColumn" | "tagsColumn") => <label>{label}<select value={csvOptions[key]} onChange={e => update(key, e.target.value)}><option value="">None</option>{csvHeaders.map(h => <option key={h}>{h}</option>)}</select></label>;
  return <fieldset disabled={isAnalyzing} className="stack-form csv-import-settings">
    <legend>Bank import settings</legend>
    <label>Saved bank profile<select defaultValue="" onChange={e => {
      const p = profiles.find(x => x.id === e.target.value); if (!p) return;
      const s = JSON.parse(p.settingsJson); setCsvOptions({ ...defaultCsvOptions, ...s.options });
      setDateColumn(s.dateColumn); setAmountColumn(s.amountColumn); setDescriptionColumn(s.descriptionColumn ?? ""); setInvertAmounts(s.invertAmounts); setName(p.name);
    }}><option value="">Choose a profile</option>{profiles.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}</select></label>
    <div className="form-grid">
      <label>Delimiter<select value={csvOptions.delimiter} onChange={e => update("delimiter", e.target.value)}><option value=",">Comma</option><option value=";">Semicolon</option><option value={'\t'}>Tab</option><option value="|">Pipe</option></select></label>
      <label>Number and date locale<select value={csvOptions.cultureName} onChange={e => update("cultureName", e.target.value)}><option value="en-US">US (1,234.56)</option><option value="en-GB">UK (1,234.56)</option><option value="de-DE">German (1.234,56)</option><option value="pl-PL">Polish (1 234,56)</option><option value="fr-FR">French (1 234,56)</option></select></label>
      <label>Date format<select value={csvOptions.dateFormat} onChange={e => update("dateFormat", e.target.value)}><option value="">Use locale</option><option value="M/d/yyyy">Month/day/year</option><option value="d/M/yyyy">Day/month/year</option><option value="yyyy-MM-dd">Year-month-day</option><option value="dd.MM.yyyy">Day.month.year</option></select></label>
      {column("Debit column (optional)", "debitColumn")}{column("Credit column (optional)", "creditColumn")}{column("Transaction type column (optional)", "typeColumn")}
      {column("Payee column (optional)", "payeeColumn")}{column("Category column (optional)", "categoryColumn")}{column("Reference column (optional)", "referenceColumn")}{column("Tags column (optional)", "tagsColumn")}
      <label>{invertAmounts ? "Positive amounts after inversion mean" : "Positive amounts mean"}<select value={csvOptions.positiveType} onChange={e => update("positiveType", e.target.value)}><option>Income</option><option>Expense</option><option>Refund</option></select></label>
      <label>{invertAmounts ? "Negative amounts after inversion mean" : "Negative amounts mean"}<select value={csvOptions.negativeType} onChange={e => update("negativeType", e.target.value)}><option>Expense</option><option>Income</option><option>Refund</option></select></label>
    </div>
    <p className="form-helper">Choose how your bank records money. Credit card purchases are expenses; refunds reduce spending. Use transfers for card payments between your own accounts. Debit/credit columns override the amount column; a type column must contain Income, Expense, or Refund.</p>
    <label>Profile name<input value={name} onChange={e => setName(e.target.value)} maxLength={120} /></label>
    <button type="button" disabled={!name.trim() || !token} onClick={async () => {
      try { await apiClient.saveImportProfile(token!, { name, settings: { dateColumn, amountColumn, descriptionColumn, invertAmounts, options: csvOptions } }); setProfiles(await apiClient.getImportProfiles(token!)); setMessage("Bank profile saved."); } catch (e) { setError(e instanceof Error ? e.message : "Could not save profile."); }
    }}>Save bank profile</button>
    {message && <p role="status">{message}</p>}
  </fieldset>;
}
