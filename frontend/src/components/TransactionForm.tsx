import { FormEvent, useEffect, useMemo, useState } from "react";
import { apiClient } from "../api/client";
import { useI18n } from "../state/I18nContext";
import type { Account, Category, SavingsGoal, Transaction } from "../types";

const transactionTypes = ["Expense", "Income", "Refund", "Transfer"] as const;
const createNewCategoryValue = "__create_new__";
const defaultCategoryColor = "#5f8f7b";

export type TransactionFormMode = "create" | "edit";

export type TransactionFormValues = {
  type: string;
  accountId: string;
  destinationAccountId: string;
  categoryId: string;
  amount: string;
  occurredOnUtc: string;
  note: string;
  savingsGoalId: string;
  payeeId: string;
  reference: string;
  tags: string;
  paymentMethod: string;
  status: string;
  splitLines: Array<{ categoryId: string; amount: string }>;

};

type TransactionFormProps = {
  token: string;
  accounts: Account[];
  categories: Category[];
  savingsGoals?: SavingsGoal[];
  mode: TransactionFormMode;
  transactionId?: string | null;
  initialValues?: Partial<TransactionFormValues>;
  submitLabel?: string;
  onCancel?: () => void;
  onSaved: (transaction: Transaction, keepDetails?: boolean) => Promise<void> | void;
  onError?: (message: string) => void;
  onStatus?: (message: string) => void;
};

function getTransactionTypeLabel(type: (typeof transactionTypes)[number], t: ReturnType<typeof useI18n>["t"]) {
  switch (type) {
    case "Expense":
      return t("transactionType.Expense");
    case "Refund":
      return t("transactionType.Refund");
    case "Income":
      return t("transactionType.Income");
    case "Transfer":
      return t("transactionType.Transfer");
  }
}

function toLocalDateTimeInputValue(date: Date) {
  const year = date.getFullYear();
  const month = String(date.getMonth() + 1).padStart(2, "0");
  const day = String(date.getDate()).padStart(2, "0");
  const hours = String(date.getHours()).padStart(2, "0");
  const minutes = String(date.getMinutes()).padStart(2, "0");

  return `${year}-${month}-${day}T${hours}:${minutes}`;
}

export function toDateTimeLocal(value: string) {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return toLocalDateTimeInputValue(new Date());
  }

  return toLocalDateTimeInputValue(date);
}

export function toFormType(type: string) {
  return type.startsWith("Transfer") ? "Transfer" : type;
}

function buildDefaultValues(): TransactionFormValues {
  return {
    type: "Expense",
    accountId: "",
    destinationAccountId: "",
    categoryId: "",
    amount: "0",
    occurredOnUtc: toLocalDateTimeInputValue(new Date()),
    note: "",
    savingsGoalId: "", payeeId: "", reference: "", tags: "", paymentMethod: "", status: "Uncleared", splitLines: []
  };
}

function getErrorMessage(caughtError: unknown, fallback: string) {
  return caughtError instanceof Error ? caughtError.message : fallback;
}

export function TransactionForm({
  token,
  accounts,
  categories,
  savingsGoals = [],
  mode,
  transactionId,
  initialValues,
  submitLabel,
  onCancel,
  onSaved,
  onError,
  onStatus
}: TransactionFormProps) {
  const { t } = useI18n();
  const [values, setValues] = useState<TransactionFormValues>(() => ({ ...buildDefaultValues(), ...initialValues }));
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [payees, setPayees] = useState<Array<{ id: string; name: string; defaultCategoryId: string | null }>>([]);
  const [detailsLoading, setDetailsLoading] = useState(mode === "edit");
  const [detailsError, setDetailsError] = useState("");
  const [keepDetails, setKeepDetails] = useState(false);
  const [newCategoryName, setNewCategoryName] = useState("");
  const [newCategoryColor, setNewCategoryColor] = useState(defaultCategoryColor);

  useEffect(() => {
    setValues({ ...buildDefaultValues(), ...initialValues });
    setNewCategoryName("");
    setNewCategoryColor(defaultCategoryColor);
  }, [initialValues]);

  useEffect(() => {
    let active = true;
    void apiClient.bookkeeping<typeof payees>(token, "payees").then(items => { if (active) setPayees(items); }).catch(() => {});
    return () => { active = false; };
  }, [token]);

  useEffect(() => {
    let active = true;
    setDetailsError("");
    if (mode !== "edit" || !transactionId) { setDetailsLoading(false); return; }
    setDetailsLoading(true);
    void apiClient.bookkeeping<{ metadata: { payeeId: string | null; reference: string; tags: string; status: string; paymentMethod: string }; splitLines: Array<{ categoryId: string; amount: number }>; destinationAccountId: string | null }>(token, `entries/${transactionId}/details`)
      .then(details => { if (active) setValues(current => ({ ...current, ...details.metadata, payeeId: details.metadata.payeeId ?? "", paymentMethod: details.metadata.paymentMethod ?? "", destinationAccountId: details.destinationAccountId ?? "", splitLines: details.splitLines.map(line => ({ categoryId: line.categoryId, amount: String(line.amount) })) })); })
      .catch(error => { if (active) setDetailsError(error instanceof Error ? error.message : "Unable to load transaction details."); })
      .finally(() => { if (active) setDetailsLoading(false); });
    return () => { active = false; };
  }, [token, transactionId, mode, initialValues]);

  const splitTotal = values.splitLines.reduce((total, line) => total + Math.round(Number(line.amount) * 100), 0);
  const splitDifference = Math.round(Number(values.amount) * 100) - splitTotal;
  const invalidSplits = values.splitLines.length > 0 && (values.splitLines.length < 2 || splitDifference !== 0 || values.splitLines.some(line => !line.categoryId || !(Number(line.amount) > 0)));

  const filteredCategories = useMemo(() => {
    if (values.type === "Income") {
      return categories.filter((category) => category.kind === "Income");
    }

    if (values.type === "Transfer") {
      return [];
    }

    return categories.filter((category) => category.kind === "Expense");
  }, [categories, values.type]);

  const isCreatingCategory = values.categoryId === createNewCategoryValue && values.type !== "Transfer";

  const updateValue = (key: keyof TransactionFormValues, value: string) => {
    setValues((current) => ({ ...current, [key]: value }));
  };

  const resetForm = () => {
    setValues(buildDefaultValues());
    setNewCategoryName("");
    setNewCategoryColor(defaultCategoryColor);
  };

  const handleSubmit = async (event: FormEvent) => {
    event.preventDefault();
    if (!values.accountId || isSubmitting || detailsLoading || detailsError || invalidSplits || values.status === "Reconciled") {
      return;
    }

    setIsSubmitting(true);
    onError?.("");
    onStatus?.("");

    try {
      let nextCategoryId = values.categoryId || undefined;

      if (isCreatingCategory) {
        const createdCategory = await apiClient.createCategory(token, {
          name: newCategoryName.trim(),
          kind: values.type === "Refund" ? "Expense" : values.type,
          color: newCategoryColor
        });
        nextCategoryId = createdCategory.id;
        setValues((current) => ({ ...current, categoryId: createdCategory.id }));
      }

      const payload = {
        categoryId: values.type === "Transfer" || values.splitLines.length ? undefined : nextCategoryId,
        splitLines: values.type === "Expense" && values.splitLines.length ? values.splitLines.map(line => ({ categoryId: line.categoryId, amount: Number(line.amount) })) : undefined,
        updateMetadata: true, payeeId: values.payeeId || undefined, reference: values.reference,
        tags: values.tags, paymentMethod: values.paymentMethod, status: values.status,
        destinationAccountId: values.type === "Transfer" ? values.destinationAccountId : undefined,
        savingsGoalId: values.type === "Transfer" ? values.savingsGoalId || undefined : undefined,
        amount: Number(values.amount),
        type: values.type,
        occurredOnUtc: new Date(values.occurredOnUtc).toISOString(),
        note: values.note.trim() || undefined
      };

      const savedTransaction =
        mode === "edit" && transactionId
          ? await apiClient.updateTransaction(token, transactionId, payload)
          : await apiClient.createTransaction(token, {
              accountId: values.accountId,
              ...payload
            });

      onStatus?.(mode === "edit" ? t("transactionForm.transactionUpdated") : t("transactionForm.transactionSaved"));
      if (mode === "create" && !keepDetails) {
        resetForm();
      }
      try {
        await onSaved(savedTransaction, keepDetails);
      } catch (caughtError) {
        onError?.(getErrorMessage(caughtError, t("transactionForm.refreshFailed")));
      }
    } catch (caughtError) {
      onError?.(getErrorMessage(caughtError, t("transactionForm.unableToSave")));
    } finally {
      setIsSubmitting(false);
    }
  };

  return (
    <form className="stack-form" onSubmit={handleSubmit}>
      {detailsLoading && <p role="status">Loading transaction details…</p>}
      {detailsError && <p role="alert">{detailsError} Close and reopen the editor to retry.</p>}
      {values.status === "Reconciled" && <p role="alert">Reopen the statement before editing this transaction.</p>}
      <label>
        {t("transactionForm.type")}
        <select
          value={values.type}
          onChange={(event) => {
            updateValue("type", event.target.value);
            setValues(current => ({ ...current, splitLines: [] }));
            updateValue("categoryId", "");
            updateValue("destinationAccountId", "");
            setNewCategoryName("");
            setNewCategoryColor(defaultCategoryColor);
          }}
        >
          {transactionTypes.map((option) => (
            <option key={option} value={option}>
              {getTransactionTypeLabel(option, t)}
            </option>
          ))}
        </select>
      </label>
      <label>
        {t("transactionForm.account")}
        <select value={values.accountId} onChange={(event) => updateValue("accountId", event.target.value)} required disabled={mode === "edit"}>
          <option value="">{t("common.selectAccount")}</option>
          {accounts.map((account) => (
            <option key={account.id} value={account.id}>
              {account.name}
            </option>
          ))}
        </select>
      </label>

      {values.type === "Transfer" ? (
        <>
          <label>
            {t("transactionForm.destinationAccount")}
            <select value={values.destinationAccountId} onChange={(event) => updateValue("destinationAccountId", event.target.value)} required>
              <option value="">{t("transactionForm.selectDestination")}</option>
              {accounts
                .filter((account) => account.id !== values.accountId)
                .map((account) => (
                  <option key={account.id} value={account.id}>
                    {account.name}
                  </option>
                ))}
            </select>
          </label>
          <label>
            {t("transactionForm.savingsGoal")}
            <select value={values.savingsGoalId} onChange={(event) => updateValue("savingsGoalId", event.target.value)}>
              <option value="">{t("transactionForm.noSavingsGoal")}</option>
              {savingsGoals.map((goal) => (
                <option key={goal.id} value={goal.id}>
                  {goal.name}
                </option>
              ))}
            </select>
          </label>
        </>
      ) : (
        <>
          <label>
            {t("transactionForm.category")}
            <select disabled={values.splitLines.length > 0} value={values.categoryId} onChange={(event) => updateValue("categoryId", event.target.value)}>
              <option value="">{t("common.chooseCategory")}</option>
              {filteredCategories.map((category) => (
                <option key={category.id} value={category.id}>
                  {category.name}
                </option>
              ))}
              <option value={createNewCategoryValue}>{t("transactionForm.createNewCategory")}</option>
            </select>
          </label>
          {isCreatingCategory ? (
            <div className="inline-category-form">
              <label>
                {t("transactionForm.newCategoryName")}
                <input value={newCategoryName} onChange={(event) => setNewCategoryName(event.target.value)} required />
              </label>
              <label>
                {t("transactionForm.newCategoryColor")}
                <input value={newCategoryColor} onChange={(event) => setNewCategoryColor(event.target.value)} type="color" />
              </label>
            </div>
          ) : null}
        </>
      )}

      {values.type === "Expense" && <fieldset className="split-editor">
        <legend>Split allocations</legend>
        {values.splitLines.map((line, index) => <div className="form-grid" key={index}>
          <label>Split category {index + 1}<select required value={line.categoryId} onChange={event => setValues(current => ({ ...current, splitLines: current.splitLines.map((item, i) => i === index ? { ...item, categoryId: event.target.value } : item) }))}><option value="">Choose category</option>{filteredCategories.map(category => <option key={category.id} value={category.id}>{category.name}</option>)}</select></label>
          <label>Split amount {index + 1}<input required type="number" min="0.01" step="0.01" value={line.amount} onChange={event => setValues(current => ({ ...current, splitLines: current.splitLines.map((item, i) => i === index ? { ...item, amount: event.target.value } : item) }))} /></label>
          <button type="button" onClick={() => setValues(current => ({ ...current, splitLines: current.splitLines.filter((_, i) => i !== index) }))}>Remove split {index + 1}</button>
        </div>)}
        <button type="button" onClick={() => setValues(current => ({ ...current, splitLines: [...current.splitLines, ...(!current.splitLines.length ? [{ categoryId: current.categoryId, amount: "" }] : []), { categoryId: "", amount: "" }] }))}>Add split</button>
        {values.splitLines.length > 0 && <><p role="status">Unallocated: {(splitDifference / 100).toFixed(2)}. Use at least two positive allocations totaling the amount.</p><button type="button" onClick={() => setValues(current => ({ ...current, splitLines: [] }))}>Use single category</button></>}
      </fieldset>}
      <label>
        {t("transactionForm.amount")}
        <input value={values.amount} onChange={(event) => updateValue("amount", event.target.value)} type="number" step="0.01" required />
      </label>
      <label>
        {t("transactionForm.dateAndTime")}
        <input value={values.occurredOnUtc} onChange={(event) => updateValue("occurredOnUtc", event.target.value)} type="datetime-local" required />
      </label>
      <label>
        {t("transactionForm.note")}
        <textarea value={values.note} onChange={(event) => updateValue("note", event.target.value)} rows={3} />
      </label>
      <details className="transaction-metadata" open={mode === "edit" || undefined}>
        <summary>Payee, reference, tags and payment details</summary>
        <label>Payee<select value={values.payeeId} onChange={event => {
          const payee = payees.find(item => item.id === event.target.value);
          const category = categories.find(item => item.id === payee?.defaultCategoryId);
          setValues(current => ({ ...current, payeeId: event.target.value, categoryId: !current.categoryId && !current.splitLines.length && category?.kind === (current.type === "Income" ? "Income" : "Expense") ? category.id : current.categoryId }));
        }}><option value="">None</option>{payees.map(payee => <option key={payee.id} value={payee.id}>{payee.name}</option>)}</select></label>
        <label>Reference<input maxLength={120} value={values.reference} onChange={event => updateValue("reference", event.target.value)} /></label>
        <label>Tags (comma separated)<input maxLength={400} value={values.tags} onChange={event => updateValue("tags", event.target.value)} /></label>
        <label>Payment method<input list="payment-methods" maxLength={40} value={values.paymentMethod} onChange={event => updateValue("paymentMethod", event.target.value)} /></label>
        <datalist id="payment-methods">{["Card", "Cash", "Check", "Direct debit", "Bank transfer", "Standing order"].map(method => <option key={method} value={method} />)}</datalist>
        <label>Cleared status<select value={values.status} disabled={values.status === "Reconciled"} onChange={event => updateValue("status", event.target.value)}><option>Uncleared</option><option>Cleared</option>{values.status === "Reconciled" && <option>Reconciled</option>}</select></label>
      </details>
      {mode === "create" && <label><input type="checkbox" checked={keepDetails} onChange={event => setKeepDetails(event.target.checked)} />Save and keep details for another transaction</label>}
      <div className="transaction-form-actions">
        <button className="primary-button" type="submit" disabled={isSubmitting || detailsLoading || !!detailsError || invalidSplits || values.status === "Reconciled"}>
          {isSubmitting ? t("transactionForm.saving") : submitLabel ?? (mode === "edit" ? t("transactionForm.saveChanges") : t("transactionForm.saveTransaction"))}
        </button>
        {onCancel ? (
          <button className="ghost-button" type="button" onClick={onCancel} disabled={isSubmitting}>
            {t("common.cancel")}
          </button>
        ) : null}
      </div>
    </form>
  );
}
