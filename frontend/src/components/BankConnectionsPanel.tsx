import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { apiClient } from "../api/client";
import { useAuth } from "../state/AuthContext";
import { useImportAnalysis } from "../state/ImportAnalysisContext";
import type { Account, MonthlyReportAnalysis } from "../types";
import { SectionCard } from "../ui/SectionCard";

type Settings = { isConfigured: boolean; clientId: string; environment: string; country: string; redirectUri: string | null };
type BankAccount = { id: string; name: string; mask: string; currency: string; type: string; reportedBalance: number | null; ledgerAccountId: string | null };
type Connection = { id: string; name: string; environment: string; lastSyncedAtUtc: string | null; accounts: BankAccount[] };
type PlaidHandler = { open: () => void; destroy: () => void };
type PlaidOptions = { token: string; receivedRedirectUri?: string; onSuccess: (token: string, metadata: { institution?: { name?: string } | null }) => void; onExit: () => void };
declare global { interface Window { Plaid?: { create: (options: PlaidOptions) => PlaidHandler }; } }
let plaidLoading: Promise<void> | null = null;
function loadPlaid() {
  if (window.Plaid) return Promise.resolve();
  if (!plaidLoading) plaidLoading = new Promise<void>((resolve, reject) => {
    const script = document.createElement("script"); script.src = "https://cdn.plaid.com/link/v2/stable/link-initialize.js"; script.async = true;
    const timeout = window.setTimeout(() => { script.remove(); plaidLoading = null; reject(new Error("Plaid Link did not load. Check your network and try again.")); }, 20000);
    script.onload = () => { window.clearTimeout(timeout); if (window.Plaid) resolve(); else { plaidLoading = null; reject(new Error("Plaid Link is unavailable.")); } };
    script.onerror = () => { window.clearTimeout(timeout); script.remove(); plaidLoading = null; reject(new Error("Unable to load Plaid Link.")); };
    document.head.appendChild(script);
  });
  return plaidLoading;
}
const flowKey = "ledgerra.plaid.flow";

export function BankConnectionsPanel({ accounts }: { accounts: Account[] }) {
  const { auth } = useAuth(); const token = auth?.accessToken ?? "";
  const { applyAnalysis, clearReviewSession, isAnalyzing, isCommitting } = useImportAnalysis();
  const [settings, setSettings] = useState<Settings>({ isConfigured: false, clientId: "", environment: "Sandbox", country: "US", redirectUri: null });
  const [secret, setSecret] = useState(""); const [connections, setConnections] = useState<Connection[]>([]);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false); const [error, setError] = useState(""); const [message, setMessage] = useState(""); const [warnings, setWarnings] = useState<string[]>([]);
  const disabled = loading || busy || isAnalyzing || isCommitting;
  const load = async () => { const [s, c] = await Promise.all([apiClient.banks<Settings>(token, "settings"), apiClient.banks<Connection[]>(token, "connections")]); setSettings(s); setConnections(c); };
  const run = async (action: () => Promise<void>) => { setBusy(true); setError(""); setMessage(""); try { await action(); } catch (e) { setError(e instanceof Error ? e.message : "Bank connection failed."); } finally { setBusy(false); } };
  const openLink = async (linkToken: string, connectionId?: string, redirect = false) => {
    await loadPlaid();
    sessionStorage.setItem(flowKey, JSON.stringify({ token: linkToken, userId: auth?.userId, connectionId }));
    let handler: PlaidHandler;
    handler = window.Plaid!.create({ token: linkToken, ...(redirect ? { receivedRedirectUri: window.location.href } : {}),
      onSuccess: (publicToken, metadata) => { sessionStorage.removeItem(flowKey); if (redirect) window.history.replaceState(null, "", "/imports"); void run(async () => {
        if (!connectionId) await apiClient.banks(token, "exchange", "POST", { publicToken, name: (metadata.institution?.name ?? "Bank connection").slice(0, 120) });
        setMessage(connectionId ? "Bank authentication updated. Sync available transactions next." : "Bank linked. Map its accounts, then sync and preview transactions.");
        await load(); handler?.destroy();
      }); },
      onExit: () => { sessionStorage.removeItem(flowKey); if (redirect) window.history.replaceState(null, "", "/imports"); setBusy(false); setMessage("Bank linking closed. You can try again."); handler?.destroy(); }
    });
    handler.open();
  };
  useEffect(() => {
    if (!token) return;
    let active = true;
    void load().catch(e => { if (active) setError(e instanceof Error ? e.message : "Unable to load bank connections."); }).finally(() => { if (active) setLoading(false); });
    if (new URLSearchParams(window.location.search).has("oauth_state_id")) {
      try {
        const flow = JSON.parse(sessionStorage.getItem(flowKey) ?? "null");
        if (flow?.userId === auth?.userId && typeof flow.token === "string") { setBusy(true); void openLink(flow.token, flow.connectionId, true).catch(e => { if (active) { setBusy(false); setError(e.message); } }); }
        else setError("The bank redirect session expired. Start Connect bank again in this browser tab.");
      } catch { setError("The bank redirect session is unavailable. Start Connect bank again."); }
    }
    return () => { active = false; };
  }, [token]);

  const connect = (connectionId?: string) => {
    setBusy(true); setError(""); setMessage("");
    void apiClient.banks<{ linkToken: string }>(token, "link-token", "POST", { connectionId })
      .then(result => openLink(result.linkToken, connectionId)).catch(e => { setBusy(false); setError(e instanceof Error ? e.message : "Unable to connect bank."); });
  };
  return <SectionCard title="Connect US and Canadian banks">
    <p>Connect through Plaid, then review downloaded transactions before adding them to Ledgerra. Bank sign-in happens in Plaid Link. Pending transactions are excluded; bank balances never overwrite your opening balances. <Link to="/accounts">Create Ledgerra destination accounts first</Link>.</p>
    {error && <p className="error-banner" role="alert">{error}</p>}{message && <p role="status">{message}</p>}
    <fieldset disabled={disabled} className="stack-form">
      <details><summary>Plaid provider settings {settings.isConfigured ? "(configured)" : "(required)"}</summary>
        <p>Use your own Plaid developer application. Sandbox connects test banks only; real banks require approved Production access. Register your HTTPS /imports redirect URL in the Plaid dashboard for OAuth banks. Keep the app's data-protection key volume when updating Synology.</p>
        <form className="form-grid" onSubmit={e => { e.preventDefault(); void run(async () => { await apiClient.banks(token, "settings", "PUT", { clientId: settings.clientId, secret: secret || undefined, environment: settings.environment, country: settings.country, redirectUri: settings.redirectUri || null }); setSecret(""); clearReviewSession(); await load(); setMessage("Plaid settings saved. The secret is encrypted and is not returned to the browser."); }); }}>
          <label>Plaid client ID<input required maxLength={200} value={settings.clientId} onChange={e => setSettings(s => ({ ...s, clientId: e.target.value }))} autoComplete="off" /></label>
          <label>Plaid secret<input type="password" required={!settings.isConfigured} maxLength={1000} value={secret} onChange={e => setSecret(e.target.value)} placeholder={settings.isConfigured ? "Leave empty to keep current secret" : "Enter in this form"} autoComplete="new-password" /></label>
          <label>Plaid environment<select value={settings.environment} onChange={e => setSettings(s => ({ ...s, environment: e.target.value }))}><option>Sandbox</option><option>Production</option></select></label>
          <label>Bank country<select value={settings.country} onChange={e => setSettings(s => ({ ...s, country: e.target.value }))}><option value="US">United States</option><option value="CA">Canada</option></select></label>
          <label>OAuth redirect URL<input type="url" maxLength={2048} value={settings.redirectUri ?? ""} placeholder="https://ledgerra.example/imports" onChange={e => setSettings(s => ({ ...s, redirectUri: e.target.value }))} /></label>
          <button type="submit">Save Plaid settings</button>
        </form>
        <button type="button" disabled={connections.length > 0 || !settings.isConfigured} onClick={() => void run(async () => { await apiClient.banks(token, "settings", "DELETE"); setSecret(""); await load(); setMessage("Provider credentials removed."); })}>Remove provider credentials</button>
      </details>
      <p>{settings.environment === "Sandbox" ? "Sandbox mode — test banks only." : "Production mode — real bank consent required."}</p>
      <button type="button" disabled={!settings.isConfigured} onClick={() => connect()}>Connect bank with Plaid</button>
      {connections.map(connection => <section key={connection.id} className="section-card" aria-label={`Bank connection ${connection.name}`}>
        <h3>{connection.name} · {connection.environment}</h3><p>Last sync: {connection.lastSyncedAtUtc ? new Date(connection.lastSyncedAtUtc).toLocaleString() : "Not synced"}</p>
        {connection.accounts.map(source => <div key={source.id}>
          <p>{source.name} {source.mask ? `•••• ${source.mask}` : ""} · {source.currency || "Unknown currency"} · {source.type} · Bank reported {source.type === "credit" ? "amount owed" : "balance"}: {source.reportedBalance ?? "Unavailable"}. This is separate from your ledger balance.</p>
          <label>Map connected {source.name}<select aria-label={`Map connected ${source.name}`} value={source.ledgerAccountId ?? ""} onChange={e => { const value = e.target.value; clearReviewSession(); setWarnings([]); void run(async () => { await apiClient.banks(token, `accounts/${source.id}/mapping`, "PUT", { accountId: value || null }); clearReviewSession(); setWarnings([]); await load(); }); }}><option value="">Skip / unmapped</option>{accounts.filter(a => a.currencyCode === source.currency).map(a => <option key={a.id} value={a.id}>{a.name} ({a.currencyCode})</option>)}</select></label>
        </div>)}
        <div className="transaction-form-actions">
          <button type="button" onClick={() => { clearReviewSession(); setWarnings([]); void run(async () => { const result = await apiClient.banks<{ changes: number; initialDataPending?: boolean }>(token, `connections/${connection.id}/sync`, "POST"); clearReviewSession(); setWarnings([]); await load(); setMessage(result.initialDataPending ? "Your bank’s initial transaction data is not ready yet. Wait a few minutes and sync again; no ledger entries were changed." : `${result.changes} provider changes downloaded. Preview to review posted transactions; no ledger entries were changed.`); }); }}>Sync available transactions</button>
          <button type="button" disabled={!connection.accounts.some(a => a.ledgerAccountId)} onClick={() => void run(async () => { const analysis = await apiClient.banks<MonthlyReportAnalysis>(token, `connections/${connection.id}/preview`, "POST"); applyAnalysis(analysis); setWarnings(analysis.warnings); setMessage("Review the drafts below. Select the entries to import, then use Save selected drafts."); })}>Preview bank transactions</button>
          <button type="button" onClick={() => connect(connection.id)}>Reconnect bank</button>
          <button type="button" onClick={() => { if (window.confirm("Revoke this Plaid connection and delete its downloaded review queue? Already imported ledger transactions will remain.")) void run(async () => { await apiClient.banks(token, `connections/${connection.id}`, "DELETE"); clearReviewSession(); setWarnings([]); await load(); setMessage("Bank disconnected. Imported ledger transactions are unchanged."); }); }}>Disconnect bank</button>
        </div>
      </section>)}
    </fieldset>
    {warnings.length > 0 && <ul className="form-helper">{warnings.map((warning, i) => <li key={i}>{warning}</li>)}</ul>}
    <p className="form-helper">Sync retrieves updates already available from Plaid; your bank may take time to supply recent transactions. Initial history depends on your bank and Plaid plan. Portable Ledgerra backups contain imported transactions, not bank tokens or the unimported review queue.</p>
  </SectionCard>;
}
