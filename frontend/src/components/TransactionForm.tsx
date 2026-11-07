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
  onSaved: (transaction: Transaction) => Promise<void> | void;
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
    savingsGoalId: ""
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
  const [newCategoryName, setNewCategoryName] = useState("");
  const [newCategoryColor, setNewCategoryColor] = useState(defaultCategoryColor);

  useEffect(() => {
    setValues({ ...buildDefaultValues(), ...initialValues });
    setNewCategoryName("");
    setNewCategoryColor(defaultCategoryColor);
  }, [initialValues]);

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
    if (!values.accountId || isSubmitting) {
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
        categoryId: values.type === "Transfer" ? undefined : nextCategoryId,
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
      if (mode === "create") {
        resetForm();
      }
      try {
        await onSaved(savedTransaction);
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
      <label>
        {t("transactionForm.type")}
        <select
          value={values.type}
          onChange={(event) => {
            updateValue("type", event.target.value);
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
            <select value={values.categoryId} onChange={(event) => updateValue("categoryId", event.target.value)}>
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
      <div className="transaction-form-actions">
        <button className="primary-button" type="submit" disabled={isSubmitting}>
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
