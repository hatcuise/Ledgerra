import { FormEvent, useCallback, useEffect, useMemo, useState } from "react";
import { apiClient } from "../api/client";
import { useAuth } from "../state/AuthContext";
import { useI18n } from "../state/I18nContext";
import { useLedgerraData } from "../hooks/useLedgerraData";
import type { Account, Category, RecurringTransactionTemplate, RecurringTransactionTemplatePayload } from "../types";
import { PageHeader } from "../ui/PageHeader";
import { SectionCard } from "../ui/SectionCard";
import { CalendarIcon, EditIcon, TrashIcon } from "../ui/icons";
import { formatCurrency } from "../utils/format";
import { getLocaleForLanguageCode } from "../utils/language";

type FormValues = {
  destinationAccountId: string;
  accountId: string;
  categoryId: string;
  amount: string;
  type: string;
  interval: string;
  startOnUtc: string;
  note: string;
  isActive: boolean;
};

const defaultValues: FormValues = {
  destinationAccountId: "",
  accountId: "",
  categoryId: "",
  amount: "0",
  type: "Expense",
  interval: "Monthly",
  startOnUtc: toDateTimeLocal(new Date()),
  note: "",
  isActive: true
};

function toDateTimeLocal(value: string | Date) {
  const date = value instanceof Date ? value : new Date(value);
  const safeDate = Number.isNaN(date.getTime()) ? new Date() : date;
  const year = safeDate.getFullYear();
  const month = String(safeDate.getMonth() + 1).padStart(2, "0");
  const day = String(safeDate.getDate()).padStart(2, "0");
  const hours = String(safeDate.getHours()).padStart(2, "0");
  const minutes = String(safeDate.getMinutes()).padStart(2, "0");
  return `${year}-${month}-${day}T${hours}:${minutes}`;
}

function toUtcIso(value: string) {
  return new Date(value).toISOString();
}

function parseAmount(value: string) {
  if (!value.trim()) {
    throw new Error("Enter a valid amount.");
  }

  const amount = Number(value);
  if (!Number.isFinite(amount)) {
    throw new Error("Enter a valid amount.");
  }

  return amount;
}

function templateToValues(template: RecurringTransactionTemplate): FormValues {
  return {
    destinationAccountId: template.destinationAccountId ?? "",
    accountId: template.accountId,
    categoryId: template.categoryId ?? "",
    amount: String(template.amount),
    type: template.type,
    interval: template.interval,
    startOnUtc: toDateTimeLocal(template.startOnUtc),
    note: template.note ?? "",
    isActive: template.isActive
  };
}

function buildPayload(values: FormValues): RecurringTransactionTemplatePayload {
  const amount = parseAmount(values.amount);

  return {
    destinationAccountId: values.type === "Transfer" ? values.destinationAccountId || null : null,
    accountId: values.accountId,
    categoryId: values.categoryId || null,
    amount,
    type: values.type,
    interval: values.interval,
    startOnUtc: toUtcIso(values.startOnUtc),
    isActive: values.isActive,
    note: values.note.trim() || null
  };
}

function getTemplateLabel(template: RecurringTransactionTemplate) {
  return template.note?.trim() || `${template.interval} ${template.type}`;
}

function getCategoryOptions(categories: Category[], type: string) {
  return categories.filter((category) => category.kind === (type === "Refund" ? "Expense" : type));
}

export function RecurringTransactionsPage() {
  const { auth } = useAuth();
  const { t, languageCode } = useI18n();
  const { accounts, categories, profile, loading: dataLoading, error: dataError } = useLedgerraData({ profile: true, accounts: true, categories: true });
  const [templates, setTemplates] = useState<RecurringTransactionTemplate[]>([]);
  const [values, setValues] = useState<FormValues>(defaultValues);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [submitting, setSubmitting] = useState(false);
  const [generating, setGenerating] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [status, setStatus] = useState<string | null>(null);

  const currencyCode = profile?.preferredCurrencyCode ?? accounts[0]?.currencyCode ?? "USD";
  const locale = getLocaleForLanguageCode(languageCode);
  const dateFormatter = useMemo(() => new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "short" }), [locale]);
  const accountById = useMemo(() => new Map(accounts.map((account) => [account.id, account])), [accounts]);
  const categoryById = useMemo(() => new Map(categories.map((category) => [category.id, category])), [categories]);
  const availableCategories = useMemo(() => getCategoryOptions(categories, values.type), [categories, values.type]);
  const startDateBounds = useMemo(() => {
    const now = new Date();
    return {
      min: toDateTimeLocal(now),
      max: toDateTimeLocal(new Date(now.getTime() + 5 * 365 * 24 * 60 * 60 * 1000))
    };
  }, []);

  const loadTemplates = useCallback(async () => {
    if (!auth?.accessToken) return;
    setLoading(true);
    setError(null);
    try {
      setTemplates(await apiClient.getRecurringTransactions(auth.accessToken));
    } catch (caughtError) {
      setError(caughtError instanceof Error ? caughtError.message : "Unable to load recurring templates.");
    } finally {
      setLoading(false);
    }
  }, [auth?.accessToken]);

  useEffect(() => {
    void loadTemplates();
  }, [loadTemplates]);

  useEffect(() => {
    if (!values.accountId && accounts.length > 0) {
      setValues((current) => ({ ...current, accountId: accounts[0].id }));
    }
  }, [accounts, values.accountId]);

  const resetForm = () => {
    setEditingId(null);
    setValues({ ...defaultValues, accountId: accounts[0]?.id ?? "" });
  };

  const updateValue = (key: keyof FormValues, value: string | boolean) => {
    setValues((current) => ({
      ...current,
      [key]: value,
      ...(key === "type" ? { categoryId: "" } : {})
    }));
  };

  const handleSubmit = async (event: FormEvent) => {
    event.preventDefault();
    if (!auth?.accessToken) return;

    setSubmitting(true);
    setError(null);
    setStatus(null);
    try {
      const payload = buildPayload(values);
      if (editingId) {
        await apiClient.updateRecurringTransaction(auth.accessToken, editingId, payload);
        setStatus("Recurring template updated.");
      } else {
        await apiClient.createRecurringTransaction(auth.accessToken, payload);
        setStatus("Recurring template created.");
      }
      resetForm();
      await loadTemplates();
    } catch (caughtError) {
      setError(caughtError instanceof Error ? caughtError.message : "Unable to save recurring template.");
    } finally {
      setSubmitting(false);
    }
  };

  const handleGenerateDue = async () => {
    if (!auth?.accessToken || generating) return;

    setGenerating(true);
    setError(null);
    setStatus(null);
    try {
      const result = await apiClient.generateDueRecurringTransactions(auth.accessToken);
      setStatus(result.generated === 0 ? "No due transactions were generated. You're already caught up." : `Generated ${result.generated} due transaction${result.generated === 1 ? "" : "s"}.`);
      await loadTemplates();
    } catch (caughtError) {
      setError(caughtError instanceof Error ? caughtError.message : "Unable to generate recurring transactions.");
    } finally {
      setGenerating(false);
    }
  };

  const toggleActive = async (template: RecurringTransactionTemplate) => {
    if (!auth?.accessToken) return;
    setError(null);
    setStatus(null);
    try {
      await apiClient.updateRecurringTransactionStatus(auth.accessToken, template.id, !template.isActive);
      setStatus(template.isActive ? "Recurring template paused." : "Recurring template resumed.");
      await loadTemplates();
    } catch (caughtError) {
      setError(caughtError instanceof Error ? caughtError.message : "Unable to update recurring template status.");
    }
  };

  const deleteTemplate = async (template: RecurringTransactionTemplate) => {
    if (!auth?.accessToken) return;
    if (!window.confirm(`Delete recurring template "${getTemplateLabel(template)}"?`)) return;
    setError(null);
    setStatus(null);
    try {
      await apiClient.deleteRecurringTransaction(auth.accessToken, template.id);
      setStatus("Recurring template deleted.");
      if (editingId === template.id) resetForm();
      await loadTemplates();
    } catch (caughtError) {
      setError(caughtError instanceof Error ? caughtError.message : "Unable to delete recurring template.");
    }
  };

  const editTemplate = (template: RecurringTransactionTemplate) => {
    setEditingId(template.id);
    setValues(templateToValues(template));
    setStatus(null);
    setError(null);
  };

  const totalActive = templates.filter((template) => template.isActive).length;

  return (
    <div className="page-stack recurring-page">
      <PageHeader
        eyebrow="Recurring transactions"
        title="Automate predictable money movement"
        description="Create reusable income and expense templates, pause them when life changes, and generate every due occurrence with duplicate-safe catch-up."
      />

      <div className="recurring-hero-grid">
        <article className="transaction-summary-card positive">
          <span>Active templates</span>
          <strong>{totalActive}</strong>
          <small>{templates.length} total</small>
        </article>
        <article className="transaction-summary-card">
          <span>Generation</span>
          <strong>Due now</strong>
          <small>Runs at startup and hourly in self-hosted deployments</small>
        </article>
        <button className="primary-button recurring-generate-button" type="button" onClick={() => void handleGenerateDue()} disabled={generating || templates.length === 0}>
          {generating ? "Generating..." : "Generate due now"}
        </button>
      </div>

      {dataError ? <p className="error-banner">{dataError}</p> : null}
      {error ? <p className="error-banner" role="alert">{error}</p> : null}
      {status ? <p className="success-banner">{status}</p> : null}

      <div className="recurring-layout">
        <SectionCard title={editingId ? "Edit recurring template" : "Create recurring template"} icon={<CalendarIcon />}>
          <form className="stack-form recurring-form" onSubmit={handleSubmit} noValidate>
            <label>
              Account
              <select value={values.accountId} onChange={(event) => updateValue("accountId", event.target.value)} required>
                <option value="">{t("common.selectAccount")}</option>
                {accounts.map((account: Account) => <option key={account.id} value={account.id}>{account.name}</option>)}
              </select>
            </label>
            <div className="form-row two-columns">
              <label>
                Type
                <select value={values.type} onChange={(event) => updateValue("type", event.target.value)}>
                  <option value="Expense">Expense</option>
                  <option value="Income">Income</option>
                  <option value="Refund">Refund</option>
                  <option value="Transfer">Transfer</option>
                </select>
              </label>
              <label>
                Interval
                <select value={values.interval} onChange={(event) => updateValue("interval", event.target.value)}>
                  <option value="Monthly">Monthly</option>
                  <option value="Weekly">Weekly</option>
                </select>
              </label>
            </div>
            {values.type === "Transfer" && <label>Destination account<select required value={values.destinationAccountId} onChange={event => updateValue("destinationAccountId", event.target.value)}><option value="">Choose destination</option>{accounts.filter(a => a.id !== values.accountId).map(a => <option key={a.id} value={a.id}>{a.name}</option>)}</select></label>}
            <label>
              Category
              <select value={values.categoryId} onChange={(event) => updateValue("categoryId", event.target.value)}>
                <option value="">No category</option>
                {availableCategories.map((category) => <option key={category.id} value={category.id}>{category.name}</option>)}
              </select>
            </label>
            <div className="form-row two-columns">
              <label>
                Amount
                <input type="number" min="0.01" step="0.01" value={values.amount} onChange={(event) => updateValue("amount", event.target.value)} required />
              </label>
              <label>
                Starts on
                <input type="datetime-local" min={startDateBounds.min} max={startDateBounds.max} value={values.startOnUtc} onChange={(event) => updateValue("startOnUtc", event.target.value)} required />
              </label>
            </div>
            <label>
              Note
              <input value={values.note} onChange={(event) => updateValue("note", event.target.value)} placeholder="Rent, salary, subscription..." maxLength={400} />
            </label>
            <label className="checkbox-row">
              <input type="checkbox" checked={values.isActive} onChange={(event) => updateValue("isActive", event.target.checked)} />
              Active template
            </label>
            <div className="form-actions">
              {editingId ? <button className="ghost-button" type="button" onClick={resetForm}>Cancel edit</button> : null}
              <button className="primary-button" type="submit" disabled={submitting || dataLoading || accounts.length === 0}>
                {submitting ? "Saving..." : editingId ? "Save changes" : "Create template"}
              </button>
            </div>
          </form>
        </SectionCard>

        <SectionCard title="Recurring templates" icon={<CalendarIcon />}>
          {loading ? <p className="empty-state">{t("common.loading")}</p> : null}
          {!loading && templates.length === 0 ? <p className="empty-state">No recurring templates yet. Create one to automate scheduled income or expenses.</p> : null}
          <div className="recurring-list">
            {templates.map((template) => {
              const account = accountById.get(template.accountId);
              const category = template.categoryId ? categoryById.get(template.categoryId) : null;
              return (
                <article className={`recurring-card${template.isActive ? "" : " is-paused"}`} key={template.id}>
                  <div className="recurring-card-main">
                    <span className={`status-badge ${template.isActive ? "success" : ""}`}>{template.isActive ? "Active" : "Paused"}</span>
                    <h3>{getTemplateLabel(template)}</h3>
                    <p>{template.interval} {template.type.toLowerCase()} from {dateFormatter.format(new Date(template.startOnUtc))}</p>
                    <small>{account?.name ?? "Unknown account"}{category ? ` · ${category.name}` : " · No category"}</small>
                  </div>
                  <div className="recurring-card-amount">
                    <strong>{formatCurrency(template.amount, account?.currencyCode ?? currencyCode)}</strong>
                    <small>Last generated: {template.lastGeneratedOnUtc ? dateFormatter.format(new Date(template.lastGeneratedOnUtc)) : "Never"}</small>
                  </div>
                  <div className="recurring-card-actions">
                    <button className="ghost-button compact-button" type="button" onClick={() => editTemplate(template)}><EditIcon />Edit</button>
                    <button className="ghost-button compact-button" type="button" onClick={() => void toggleActive(template)}>{template.isActive ? "Pause" : "Resume"}</button>
                    <button className="ghost-button compact-button danger-button" type="button" onClick={() => void deleteTemplate(template)}><TrashIcon />Delete</button>
                  </div>
                </article>
              );
            })}
          </div>
        </SectionCard>
      </div>
    </div>
  );
}
