import { useCallback, useEffect, useRef, useState } from "react";
import { apiClient } from "../api/client";
import { useAuth } from "../state/AuthContext";
import type { SpendingInsights } from "../types/insights";
export function browserCalendarDate() {
  const today = new Date();
  return `${today.getFullYear()}-${String(today.getMonth() + 1).padStart(2, "0")}-${String(today.getDate()).padStart(2, "0")}`;
}
export function useSpendingInsights(month: string, revision = "") {
  const { auth } = useAuth(); const token = auth?.accessToken ?? ""; const today = browserCalendarDate(); const timeZone = Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC";
  const [data, setData] = useState<SpendingInsights | null>(null); const [error, setError] = useState(""); const [loading, setLoading] = useState(true);
  const sequence = useRef(0);
  const context = `${token}|${month}|${today}|${timeZone}|${revision}`;
  const currentContext = useRef(context); currentContext.current = context;
  const refresh = useCallback(async () => {
    if (context !== currentContext.current) return;
    const requestId = ++sequence.current;
    setLoading(true); setError(""); setData(null);
    if (!token) { setLoading(false); return; }
    try { const result = await apiClient.insights<SpendingInsights>(token, `?month=${encodeURIComponent(month)}&today=${today}&timeZone=${encodeURIComponent(timeZone)}`); if (requestId === sequence.current && context === currentContext.current) setData(result); }
    catch (e) { if (requestId === sequence.current && context === currentContext.current) setError(e instanceof Error ? e.message : "Unable to load spending insights."); }
    finally { if (requestId === sequence.current && context === currentContext.current) setLoading(false); }
  }, [token, month, today, timeZone, revision, context]);
  useEffect(() => { void refresh(); return () => { sequence.current++; }; }, [refresh]);
  return { data, error, loading, refresh, token, today, timeZone };
}
