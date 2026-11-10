import { SavedLedgerViews } from "../components/SavedLedgerViews";
import { useCallback, useEffect, useMemo, useState, type CSSProperties } from "react";
import { apiClient } from "../api/client";
import { TransactionForm, toDateTimeLocal, toFormType, type TransactionFormMode, type TransactionFormValues } from "../components/TransactionForm";
import { useLedgerraData } from "../hooks/useLedgerraData";
import { useAuth } from "../state/AuthContext";
import { useI18n } from "../state/I18nContext";
import type { SavingsGoal, Transaction } from "../types";
import { AccountsIcon, BookmarkIcon, CategoryIcon, ChevronDownIcon, DownloadIcon, DuplicateIcon, EditIcon, TrashIcon } from "../ui/icons";
import { ActionMenu } from "../ui/ActionMenu";
import { PageHeader } from "../ui/PageHeader";
import { formatCurrency, formatDate } from "../utils/format";

const transactionTypes = ["Expense", "Income", "Refund", "Transfer"];

function escapeCsvValue(value: string | number | boolean | null | undefined) {
  const normalized = value == null ? "" : String(value);
  const escaped = normalized.replace(/"/g, "\"\"");
  return /[",\n\r]/.test(escaped) ? `"${escaped}"` : escaped;
}

function toCsv(headers: string[], rows: Array<Array<string | number | boolean | null | undefined>>) {
  const csvRows = [headers, ...rows].map((row) => row.map((cell) => escapeCsvValue(cell)).join(","));
  return csvRows.join("\r\n");
}

function getTransactionTypeLabel(type: string, t: ReturnType<typeof useI18n>["t"]) {
  switch (type) {
    case "Expense":
      return t("transactionType.Expense");
    case "Refund":
      return t("transactionType.Refund");
    case "Income":
      return t("transactionType.Income");
    case "Transfer":
      return t("transactionType.Transfer");
    default:
      return type;
  }
}

function transactionLabel(transaction: Transaction, t: ReturnType<typeof useI18n>["t"], categoryName?: string) {
  return transaction.note?.trim() || categoryName || getTransactionTypeLabel(transaction.type, t);
}

function isTransferTransaction(transaction: Transaction) {
  return transaction.type.startsWith("Transfer");
}

function getCategorisableTransactionKind(transaction: Transaction) {
  if (transaction.splitGroupId) return "";
  return transaction.type === "Refund" ? "Expense" : transaction.type === "Expense" || transaction.type === "Income" ? transaction.type : "";
}

function getCategoryFallbackColor(kind: string) {
  return kind === "Income" ? "#34d399" : "#60a5fa";
}

function getTransactionSignedAmount(transaction: Transaction) {
  if (transaction.type === "Expense" || transaction.type === "TransferOut") {
    return -Math.abs(transaction.amount);
  }

  if (transaction.type === "Income" || transaction.type === "Refund" || transaction.type === "TransferIn") {
    return Math.abs(transaction.amount);
  }

  return transaction.amount;
}

function formatSignedCurrency(value: number, currencyCode: string) {
  if (value > 0) {
    return `+ ${formatCurrency(value, currencyCode)}`;
  }

  if (value < 0) {
    return `- ${formatCurrency(Math.abs(value), currencyCode)}`;
  }

  return formatCurrency(0, currencyCode);
}

function getDateKey(value: string) {
  const date = new Date(value);
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, "0")}-${String(date.getDate()).padStart(2, "0")}`;
}

function getRelativeDayLabel(value: string, t: ReturnType<typeof useI18n>["t"]) {
  const dateKey = getDateKey(value);
  const today = new Date();
  const yesterday = new Date();
  yesterday.setDate(today.getDate() - 1);

  if (dateKey === getDateKey(today.toISOString())) {
    return t("transactions.today");
  }

  if (dateKey === getDateKey(yesterday.toISOString())) {
    return t("transactions.yesterday");
  }

  return formatDate(value);
}

export function TransactionsPage() {
  const { auth } = useAuth();
  const { t } = useI18n();
  const { accounts, categories, budget, refresh } = useLedgerraData({
    accounts: true,
    categories: true,
    budget: true
  });
  const [formMode, setFormMode] = useState<TransactionFormMode>("create");
  const [editingTransactionId, setEditingTransactionId] = useState<string | null>(null);
  const initialQuery = useMemo(() => {
    if (typeof window === "undefined") {
      return new URLSearchParams();
    }
    return new URLSearchParams(window.location.search || window.localStorage.getItem("ledgerra:transactions:view") || "");
  }, []);
  const liveQuery = useMemo(
    () => typeof window === "undefined"
      ? new URLSearchParams()
      : new URLSearchParams(window.location.search),
    []
  );
  const [focusedEntryId, setFocusedEntryId] = useState(() => liveQuery.get("entry") ?? "");
  const initialFormFromQuery = liveQuery.get("form");
  const initialFormAccountId = liveQuery.get("accountId");
  const initialFormSavingsGoalId = liveQuery.get("savingsGoalId");
  const [formValues, setFormValues] = useState<Partial<TransactionFormValues>>(() => ({
    ...(initialFormFromQuery === "transfer" ? { type: "Transfer" } : {}),
    ...(initialFormAccountId ? { accountId: initialFormAccountId } : {}),
    ...(initialFormSavingsGoalId ? { savingsGoalId: initialFormSavingsGoalId } : {})
  }));
  const [filterAccountIds, setFilterAccountIds] = useState<string[]>(() => initialQuery.getAll("accountId"));
  const [filterCategoryIds, setFilterCategoryIds] = useState<string[]>(() => initialQuery.getAll("categoryId"));
  const [filterTimeZone, setFilterTimeZone] = useState(() => initialQuery.get("timeZone") ?? "");
  const [filterType, setFilterType] = useState(() => initialQuery.get("type") ?? "");
  const [fromDate, setFromDate] = useState(() => initialQuery.get("from") ?? "");
  const [toDate, setToDate] = useState(() => initialQuery.get("to") ?? "");
  const [minAmount, setMinAmount] = useState(() => initialQuery.get("minAmount") ?? "");
  const [maxAmount, setMaxAmount] = useState(() => initialQuery.get("maxAmount") ?? "");
  const [noteSearch, setNoteSearch] = useState(() => initialQuery.get("q") ?? "");
  const [showUncategorizedOnly, setShowUncategorizedOnly] = useState(() => initialQuery.get("view") === "uncategorized");
  const [ledgerTransactions, setLedgerTransactions] = useState<Transaction[]>([]);
  const [statusMessage, setStatusMessage] = useState("");
  const [errorMessage, setErrorMessage] = useState("");
  const [selectedTransactionIds, setSelectedTransactionIds] = useState<string[]>([]);
  const [bulkCategoryId, setBulkCategoryId] = useState("");
  const [bulkAccountId, setBulkAccountId] = useState("");
  const [isApplyingBulkAction, setIsApplyingBulkAction] = useState(false);
  const [filtersCollapsed, setFiltersCollapsed] = useState(false);
  const [isEntryOpen, setIsEntryOpen] = useState(initialFormFromQuery === "transfer");
  const [bulkActionsOpen, setBulkActionsOpen] = useState(false);
  const [exportActionsOpen, setExportActionsOpen] = useState(false);
  const [savingsGoals, setSavingsGoals] = useState<SavingsGoal[]>([]);

  useEffect(() => {
    if (!auth?.accessToken) return;
    void apiClient.getSavingsGoals(auth.accessToken).then(setSavingsGoals).catch(() => setSavingsGoals([]));
  }, [auth?.accessToken]);

  const filterQuery = useMemo(() => {
    const params = new URLSearchParams();
    if (filterTimeZone) params.set("timeZone", filterTimeZone);
    filterAccountIds.forEach((id) => params.append("accountId", id));
    if (filterType !== "Transfer") {
      filterCategoryIds.forEach((id) => params.append("categoryId", id));
    }
    if (filterType && filterType !== "Transfer") {
      params.set("type", filterType);
    }
    if (fromDate) {
      params.set("from", fromDate);
    }
    if (toDate) {
      params.set("to", toDate);
    }
    if (minAmount) {
      params.set("minAmount", minAmount);
    }
    if (maxAmount) {
      params.set("maxAmount", maxAmount);
    }
    if (noteSearch.trim()) {
      params.set("q", noteSearch.trim());
    }
    if (showUncategorizedOnly) {
      params.set("uncategorizedOnly", "true");
    }

    const query = params.toString();
    return query ? `?${query}` : "";
  }, [filterAccountIds, filterCategoryIds, filterType, fromDate, toDate, minAmount, maxAmount, noteSearch, showUncategorizedOnly, filterTimeZone]);

  useEffect(() => {
    const params = new URLSearchParams();
    if (filterTimeZone) params.set("timeZone", filterTimeZone);
    filterAccountIds.forEach((id) => params.append("accountId", id));
    if (filterType !== "Transfer") {
      filterCategoryIds.forEach((id) => params.append("categoryId", id));
    }
    if (filterType) params.set("type", filterType);
    if (fromDate) params.set("from", fromDate);
    if (toDate) params.set("to", toDate);
    if (minAmount) params.set("minAmount", minAmount);
    if (maxAmount) params.set("maxAmount", maxAmount);
    if (noteSearch.trim()) params.set("q", noteSearch.trim());
    if (showUncategorizedOnly) params.set("view", "uncategorized");
    const query = params.toString();
    window.history.replaceState(null, "", query ? `?${query}` : window.location.pathname);
  }, [filterAccountIds, filterCategoryIds, filterType, fromDate, toDate, minAmount, maxAmount, noteSearch, showUncategorizedOnly, filterTimeZone]);

  const loadTransactions = useCallback(async () => {
    if (!auth?.accessToken) {
      return;
    }

    try {
      setErrorMessage("");
      const payload = await apiClient.getTransactions(auth.accessToken, filterQuery);
      setLedgerTransactions(payload);
    } catch (caughtError) {
      setErrorMessage(caughtError instanceof Error ? caughtError.message : t("transactions.unableToLoad"));
    }
  }, [auth?.accessToken, filterQuery, t]);

  useEffect(() => {
    void loadTransactions();
  }, [loadTransactions]);

  const visibleTransactions = useMemo(() => {
    const normalizedSearch = noteSearch.trim().toLowerCase();
    const min = minAmount ? Number(minAmount) : null;
    const max = maxAmount ? Number(maxAmount) : null;
    return ledgerTransactions.filter((transaction) => {
      if (transaction.parentTransactionId || (focusedEntryId && transaction.id !== focusedEntryId)) return false;
      if (filterType === "Transfer" && !transaction.type.startsWith("Transfer")) {
        return false;
      }
      if (filterAccountIds.length > 0 && !filterAccountIds.includes(transaction.accountId)) {
        return false;
      }
      if (filterType !== "Transfer" && filterCategoryIds.length > 0 && !filterCategoryIds.includes(transaction.categoryId ?? "") && !ledgerTransactions.some(child => child.parentTransactionId === transaction.id && filterCategoryIds.includes(child.categoryId ?? ""))) {
        return false;
      }
      if (min !== null && transaction.amount < min) {
        return false;
      }
      if (max !== null && transaction.amount > max) {
        return false;
      }

      if (showUncategorizedOnly && ((transaction.type !== "Expense" && transaction.type !== "Refund") || transaction.categoryId || transaction.splitGroupId)) {
        return false;
      }

      if (!normalizedSearch) {
        return true;
      }

      return (transaction.note ?? "").toLowerCase().includes(normalizedSearch);
    });
  }, [filterType, filterAccountIds, filterCategoryIds, ledgerTransactions, minAmount, maxAmount, noteSearch, showUncategorizedOnly, focusedEntryId]);
  useEffect(() => {
    setSelectedTransactionIds((current) => current.filter((id) => visibleTransactions.some((transaction) => transaction.id === id)));
  }, [visibleTransactions]);

  const selectedTransactions = useMemo(
    () => visibleTransactions.filter((transaction) => selectedTransactionIds.includes(transaction.id)),
    [visibleTransactions, selectedTransactionIds]
  );
  const allVisibleSelected = visibleTransactions.length > 0 && selectedTransactionIds.length === visibleTransactions.length;
  const selectedCategorisableTransactions = useMemo(
    () => selectedTransactions.filter((transaction) => getCategorisableTransactionKind(transaction)),
    [selectedTransactions]
  );
  const bulkCategoryKind = useMemo(() => {
    const selectedKinds = new Set(selectedCategorisableTransactions.map(getCategorisableTransactionKind));
    return selectedKinds.size === 1 ? Array.from(selectedKinds)[0] : "";
  }, [selectedCategorisableTransactions]);

  const bulkCategories = useMemo(
    () => categories.filter((category) => bulkCategoryKind && category.kind === bulkCategoryKind),
    [bulkCategoryKind, categories]
  );
  useEffect(() => {
    if (bulkCategoryId && !bulkCategories.some((category) => category.id === bulkCategoryId)) {
      setBulkCategoryId("");
    }
  }, [bulkCategories, bulkCategoryId]);
  const expenseCategories = useMemo(
    () => categories.filter((category) => category.kind === "Expense"),
    [categories]
  );
  const accountById = useMemo(() => new Map(accounts.map((account) => [account.id, account])), [accounts]);
  const categoryById = useMemo(() => new Map(categories.map((category) => [category.id, category])), [categories]);
  const defaultCurrencyCode = accounts[0]?.currencyCode ?? "USD";
  const selectedTotal = useMemo(
    () => selectedTransactions.reduce((total, transaction) => total + Math.abs(transaction.amount), 0),
    [selectedTransactions]
  );
  const uncategorizedExpenseCount = useMemo(
    () => ledgerTransactions.filter((transaction) => (transaction.type === "Expense" || transaction.type === "Refund") && !transaction.categoryId).length,
    [ledgerTransactions]
  );
  const transactionSummary = useMemo(() => {
    const income = visibleTransactions
      .filter((transaction) => transaction.type === "Income" && !transaction.parentTransactionId)
      .reduce((total, transaction) => total + Math.abs(transaction.amount), 0);
    const expenses = visibleTransactions
      .filter((transaction) => (transaction.type === "Expense" || transaction.type === "Refund") && !transaction.parentTransactionId)
      .reduce((total, transaction) => total + (transaction.type === "Refund" ? -1 : 1) * Math.abs(transaction.amount), 0);

    const uniqueDays = new Set(visibleTransactions.map((transaction) => getDateKey(transaction.occurredOnUtc)));
    const dayCount = Math.max(uniqueDays.size, 1);

    return {
      income,
      expenses,
      balance: income - expenses,
      averageDaily: expenses / dayCount
    };
  }, [visibleTransactions]);
  const groupedTransactions = useMemo(() => {
    const groups = new Map<string, { label: string; dateLabel: string; total: number; transactions: Transaction[] }>();
    const sortedTransactions = [...visibleTransactions].sort((first, second) => new Date(second.occurredOnUtc).getTime() - new Date(first.occurredOnUtc).getTime());

    sortedTransactions.forEach((transaction) => {
      const key = getDateKey(transaction.occurredOnUtc);
      const existing = groups.get(key);
      const signedAmount = getTransactionSignedAmount(transaction);

      if (existing) {
        existing.total += signedAmount;
        existing.transactions.push(transaction);
        return;
      }

      groups.set(key, {
        label: getRelativeDayLabel(transaction.occurredOnUtc, t),
        dateLabel: formatDate(transaction.occurredOnUtc),
        total: signedAmount,
        transactions: [transaction]
      });
    });

    return Array.from(groups.values());
  }, [t, visibleTransactions]);
  const hasActiveFilters =
    filterAccountIds.length > 0 ||
    filterCategoryIds.length > 0 ||
    Boolean(filterType || fromDate || toDate || minAmount || maxAmount || noteSearch.trim() || showUncategorizedOnly);

  const setSingleFilterAccount = (accountId: string) => {
    setFilterAccountIds(accountId ? [accountId] : []);
  };

  const toggleCategoryFilter = (categoryId: string, selected: boolean) => {
    setFilterCategoryIds((current) =>
      selected ? (current.includes(categoryId) ? current : [...current, categoryId]) : current.filter((id) => id !== categoryId)
    );
  };

  const clearFilters = () => {
    setFilterTimeZone(""); setFocusedEntryId("");
    setFilterAccountIds([]);
    setFilterCategoryIds([]);
    setFilterType("");
    setFromDate("");
    setToDate("");
    setMinAmount("");
    setMaxAmount("");
    setNoteSearch("");
    setShowUncategorizedOnly(false);
  };

  const saveCurrentView = () => {
    localStorage.setItem("ledgerra:transactions:view", window.location.search);
    setStatusMessage(t("transactions.viewSaved"));
  };

  const downloadCsv = (filename: string, csvContent: string) => {
    const blob = new Blob([csvContent], { type: "text/csv;charset=utf-8;" });
    const link = document.createElement("a");
    const href = URL.createObjectURL(blob);
    link.href = href;
    link.setAttribute("download", filename);
    document.body.append(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(href);
  };

  const exportFilteredTransactions = () => {
    const rows = visibleTransactions.map((transaction) => {
      const account = accounts.find((item) => item.id === transaction.accountId);
      const category = categories.find((item) => item.id === transaction.categoryId);
      return [transaction.id, transaction.occurredOnUtc, transaction.type, transaction.amount, account?.name ?? "", account?.currencyCode ?? "", category?.name ?? "", transaction.note ?? ""];
    });
    const csv = toCsv(["id", "occurredOnUtc", "type", "amount", "account", "currency", "category", "note"], rows);
    downloadCsv(`transactions-${new Date().toISOString().slice(0, 10)}.csv`, csv);
    setStatusMessage(`Exported ${visibleTransactions.length} filtered transactions.`);
  };

  const exportCategoriesAndBudgets = () => {
    const categoriesCsv = toCsv(
      ["id", "name", "kind", "isSystem"],
      categories.map((category) => [category.id, category.name, category.kind, category.isSystem])
    );
    downloadCsv(`categories-${new Date().toISOString().slice(0, 10)}.csv`, categoriesCsv);

    if (budget) {
      const budgetRows = budget.categories.map((category) => {
        const categoryName = category.categoryName || categories.find((item) => item.id === category.categoryId)?.name || "";
        return [category.categoryId, categoryName, category.planned, category.spent, category.remaining];
      });
      const budgetCsv = toCsv(["categoryId", "category", "planned", "spent", "remaining"], budgetRows);
      downloadCsv(`budget-${new Date().toISOString().slice(0, 10)}.csv`, budgetCsv);
    }

    setStatusMessage("Exported categories and budget data.");
  };

  const resetForm = () => {
    setFormMode("create");
    setEditingTransactionId(null);
    setFormValues({});
    setIsEntryOpen(false);
  };

  const refreshAfterMutation = async () => {
    await refresh();
    await loadTransactions();
  };
  const clearSelection = () => {
    setSelectedTransactionIds([]);
    setBulkCategoryId("");
    setBulkAccountId("");
    setBulkActionsOpen(false);
  };
  const toggleTransactionSelection = (transactionId: string, selected: boolean) => {
    setBulkActionsOpen(false);
    setSelectedTransactionIds((current) =>
      selected ? (current.includes(transactionId) ? current : [...current, transactionId]) : current.filter((id) => id !== transactionId)
    );
  };
  const toggleSelectAllVisible = (selected: boolean) => {
    setBulkActionsOpen(false);
    setSelectedTransactionIds(selected ? visibleTransactions.map((transaction) => transaction.id) : []);
  };
  const getUniqueDeleteTargets = () => {
    const deleteTargets = new Map<string, Transaction>();
    selectedTransactions.forEach((transaction) => {
      const deleteKey = transaction.transferGroupId ? `transfer:${transaction.transferGroupId}` : `transaction:${transaction.id}`;
      if (!deleteTargets.has(deleteKey)) {
        deleteTargets.set(deleteKey, transaction);
      }
    });

    return Array.from(deleteTargets.values());
  };

  const startEdit = (transaction: Transaction) => {
    setIsEntryOpen(false);
    setFormMode("edit");
    setEditingTransactionId(transaction.id);
    setFormValues({
      type: toFormType(transaction.type),
      accountId: transaction.accountId,
      destinationAccountId: "",
      categoryId: transaction.categoryId ?? "",
      amount: String(transaction.amount),
      occurredOnUtc: toDateTimeLocal(transaction.occurredOnUtc),
      note: transaction.note ?? "",
      savingsGoalId: transaction.savingsGoalId ?? ""
    });
    setStatusMessage("");
    setErrorMessage("");
  };

  const duplicateTransaction = async (transaction: Transaction) => {
    if (!auth?.accessToken) {
      return;
    }

    if (isTransferTransaction(transaction)) {
      setIsEntryOpen(true);
      setFormMode("create");
      setEditingTransactionId(null);
      setFormValues({
        type: "Transfer",
        accountId: transaction.accountId,
        destinationAccountId: "",
        amount: String(transaction.amount),
        occurredOnUtc: toDateTimeLocal(transaction.occurredOnUtc),
        note: transaction.note ?? "",
        savingsGoalId: transaction.savingsGoalId ?? ""
      });
      setErrorMessage("");
      setStatusMessage(t("transactions.chooseDestination"));
      return;
    }

    try {
      setErrorMessage("");
      const details = await apiClient.bookkeeping<{ metadata: { payeeId: string | null; tags: string; paymentMethod: string }; splitLines: Array<{ categoryId: string; amount: number }> }>(auth.accessToken, `entries/${transaction.id}/details`);
      setIsEntryOpen(true); setFormMode("create"); setEditingTransactionId(null);
      setFormValues({ type: transaction.type, accountId: transaction.accountId, categoryId: transaction.categoryId ?? "", amount: String(transaction.amount),
        occurredOnUtc: toDateTimeLocal(new Date().toISOString()), note: transaction.note ?? "", payeeId: details.metadata.payeeId ?? "", tags: details.metadata.tags, paymentMethod: details.metadata.paymentMethod ?? "",
        reference: "", status: "Uncleared", splitLines: details.splitLines.map(line => ({ categoryId: line.categoryId, amount: String(line.amount) })) });
      setStatusMessage("Review the copy, then save it. Bank reference and cleared state have been reset.");
    } catch (caughtError) {
      setErrorMessage(caughtError instanceof Error ? caughtError.message : t("transactions.unableToDuplicate"));
    }
  };

  const openCreateEntry = () => {
    setIsEntryOpen(true);
    setFormMode("create");
    setEditingTransactionId(null);
    setFormValues({});
    setErrorMessage("");
  };

  const deleteTransaction = async (transaction: Transaction) => {
    if (!auth?.accessToken) {
      return;
    }

    try {
      setErrorMessage("");
      await apiClient.deleteTransaction(auth.accessToken, transaction.id);
      setStatusMessage(t("transactions.deleted"));
      if (editingTransactionId === transaction.id) {
        resetForm();
      }
      await refreshAfterMutation();
    } catch (caughtError) {
      setErrorMessage(caughtError instanceof Error ? caughtError.message : t("transactions.unableToDelete"));
    }
  };

  const assignTransactionCategory = async (transaction: Transaction, nextCategoryId: string) => {
    if (!auth?.accessToken || !nextCategoryId) {
      return;
    }

    const category = categories.find((item) => item.id === nextCategoryId);

    try {
      setErrorMessage("");
      setStatusMessage("");
      await apiClient.updateTransaction(auth.accessToken, transaction.id, {
        categoryId: nextCategoryId,
        amount: transaction.amount,
        type: transaction.type,
        occurredOnUtc: transaction.occurredOnUtc,
        note: transaction.note?.trim() || undefined
      });
      setStatusMessage(t("transactions.categorizedAs", { label: transactionLabel(transaction, t, category?.name), category: category?.name ?? t("transactions.selectedCategory") }));
      await refreshAfterMutation();
    } catch (caughtError) {
      setErrorMessage(caughtError instanceof Error ? caughtError.message : t("transactions.unableToCategorize"));
    }
  };

  const bulkDeleteTransactions = async () => {
    if (!auth?.accessToken || selectedTransactions.length === 0) return;
    const deleteTargets = getUniqueDeleteTargets();
    try {
      setIsApplyingBulkAction(true);
      await Promise.all(deleteTargets.map((transaction) => apiClient.deleteTransaction(auth.accessToken, transaction.id)));
      setStatusMessage(`Deleted ${selectedTransactions.length} transactions.`);
      clearSelection();
      await refreshAfterMutation();
    } catch (caughtError) {
      setErrorMessage(caughtError instanceof Error ? caughtError.message : t("transactions.unableToDelete"));
    } finally {
      setIsApplyingBulkAction(false);
    }
  };
  const bulkAssignCategory = async () => {
    if (!auth?.accessToken || !bulkCategoryId || selectedCategorisableTransactions.length === 0 || !bulkCategoryKind) return;
    try {
      setIsApplyingBulkAction(true);
      await Promise.all(
        selectedCategorisableTransactions.map((transaction) =>
          apiClient.updateTransaction(auth.accessToken, transaction.id, {
            categoryId: bulkCategoryId,
            amount: transaction.amount,
            type: transaction.type,
            occurredOnUtc: transaction.occurredOnUtc,
            note: transaction.note?.trim() || undefined
          })
        )
      );
      setStatusMessage(`Updated category for ${selectedCategorisableTransactions.length} transactions.`);
      clearSelection();
      await refreshAfterMutation();
    } catch (caughtError) {
      setErrorMessage(caughtError instanceof Error ? caughtError.message : t("transactions.unableToCategorize"));
    } finally {
      setIsApplyingBulkAction(false);
    }
  };
  const bulkMoveAccount = async () => {
    if (!auth?.accessToken || !bulkAccountId || selectedTransactions.length === 0) return;
    const targets = selectedTransactions.filter((transaction) => !isTransferTransaction(transaction));
    try {
      setIsApplyingBulkAction(true);
      await Promise.all(
        targets.map((transaction) => apiClient.moveTransactionAccount(auth.accessToken, transaction.id, bulkAccountId))
      );
      setStatusMessage(`Moved ${targets.length} transactions to the selected account.`);
      clearSelection();
      await refreshAfterMutation();
    } catch (caughtError) {
      setErrorMessage(caughtError instanceof Error ? caughtError.message : "Unable to move selected transactions.");
    } finally {
      setIsApplyingBulkAction(false);
    }
  };

  return (
    <div className="page-stack">
      <PageHeader
        eyebrow={t("transactions.eyebrow")}
        title={t("transactions.title")}
        description={t("transactions.description")}
      />

      <div className={`transaction-workspace${filtersCollapsed ? " is-filters-collapsed" : ""}`}>
        <div className="transaction-primary-column">
          <div className="transaction-summary-grid" aria-label={t("transactions.currentView")}>
            <article className="transaction-summary-card positive">
              <span>{t("transactions.summaryIncome")}</span>
              <strong>{formatSignedCurrency(transactionSummary.income, defaultCurrencyCode)}</strong>
              <small>{t("transactions.currentView")}</small>
            </article>
            <article className="transaction-summary-card negative">
              <span>{t("transactions.summaryExpenses")}</span>
              <strong>{formatSignedCurrency(-transactionSummary.expenses, defaultCurrencyCode)}</strong>
              <small>{t("transactions.transactionCount", { count: visibleTransactions.length })}</small>
            </article>
            <article className="transaction-summary-card positive">
              <span>{t("transactions.summaryBalance")}</span>
              <strong>{formatSignedCurrency(transactionSummary.balance, defaultCurrencyCode)}</strong>
              <small>{t("transactions.filteredBalance")}</small>
            </article>
            <article className="transaction-summary-card">
              <span>{t("transactions.summaryAverageDaily")}</span>
              <strong>{formatCurrency(transactionSummary.averageDaily, defaultCurrencyCode)}</strong>
              <small>{t("transactions.currentView")}</small>
            </article>
          </div>

          {focusedEntryId && <p role="status">Showing the transaction linked from a spending alert. <button type="button" onClick={() => setFocusedEntryId("")}>Show all matching transactions</button></p>}
          {errorMessage ? <p className="error-banner">{errorMessage}</p> : null}
          {statusMessage ? <p className="success-banner">{statusMessage}</p> : null}
          {isEntryOpen ? (
            <section className={`transaction-entry-panel${formMode === "edit" ? " is-editing" : ""}`} id="transaction-entry-form">
              <div className="transaction-entry-header">
                <div>
                  <span>{t("transactions.quickEntry")}</span>
                  <h2>{formMode === "edit" ? t("transactions.editTransaction") : t("transactions.addTransaction")}</h2>
                </div>
                <div className="transaction-entry-header-actions">
                  <strong>{formMode === "edit" ? t("transactions.editing") : t("transactions.ready")}</strong>
                  <button className="ghost-button compact-button" type="button" onClick={resetForm}>
                    {t("common.close")}
                  </button>
                </div>
              </div>
              {auth?.accessToken ? (
                <TransactionForm
                  key={`${formMode}-${editingTransactionId ?? "new"}`}
                  token={auth.accessToken}
                  accounts={accounts}
                  categories={categories}
                  savingsGoals={savingsGoals}
                  mode={formMode}
                  transactionId={editingTransactionId}
                  initialValues={formValues}
                  onCancel={formMode === "edit" ? resetForm : undefined}
                  onError={setErrorMessage}
                  onStatus={setStatusMessage}
                  onSaved={async (_, keepDetails) => {
                    if (!keepDetails) resetForm();
                    await refreshAfterMutation();
                  }}
                />
              ) : null}
            </section>
          ) : (
            <section className="transaction-entry-launcher">
              <div>
                <span>{t("transactions.quickEntry")}</span>
                <h2>{t("transactions.addTransaction")}</h2>
              </div>
              <button className="primary-button transaction-entry-open-button" type="button" aria-expanded={isEntryOpen} aria-controls="transaction-entry-form" onClick={openCreateEntry}>
                {t("transactions.addTransaction")}
              </button>
            </section>
          )}

          <section className="transaction-ledger-panel">
            <div className="transaction-ledger-heading">
              <div>
                <span>{t("transactions.currentView")}</span>
                <h2>{t("transactions.ledger")}</h2>
              </div>
              <strong>{t("transactions.transactionCount", { count: visibleTransactions.length })}</strong>
            </div>

            {showUncategorizedOnly ? (
              <p className="workflow-banner">
                {t("transactions.workflowBanner", { count: uncategorizedExpenseCount })}
              </p>
            ) : null}
            {visibleTransactions.length > 0 ? (
              <div className="transaction-bulk-toolbar" aria-label="Bulk transaction actions">
                <div className="transaction-bulk-selection">
                  <label className="transaction-select-all-control">
                    <input type="checkbox" aria-label="Select all in current filtered view" checked={allVisibleSelected} onChange={(event) => toggleSelectAllVisible(event.target.checked)} />
                    <span>
                      <strong>{selectedTransactionIds.length} selected</strong>
                      <small>{formatCurrency(selectedTotal, defaultCurrencyCode)} selected total · {visibleTransactions.length} in view</small>
                    </span>
                  </label>
                  <button
                    className="ghost-button compact-button transaction-bulk-toggle"
                    type="button"
                    aria-expanded={bulkActionsOpen}
                    aria-controls="transaction-bulk-action-panel"
                    onClick={() => setBulkActionsOpen((current) => !current)}
                  >
                    {t("transactions.bulkActions")}
                    <ChevronDownIcon />
                  </button>
                </div>
                {bulkActionsOpen ? (
                  <div className="transaction-bulk-actions" id="transaction-bulk-action-panel">
                    <button className="ghost-button compact-button danger-button transaction-bulk-button" type="button" onClick={() => void bulkDeleteTransactions()} disabled={selectedTransactionIds.length === 0 || isApplyingBulkAction}>
                      <TrashIcon />
                      {t("transactions.bulkDelete")}
                    </button>
                    <div className="transaction-bulk-control">
                      <label>
                        {t("transactions.bulkCategory")}
                        <select value={bulkCategoryId} onChange={(event) => setBulkCategoryId(event.target.value)} disabled={!bulkCategoryKind}>
                          <option value="">{t("common.chooseCategory")}</option>
                          {bulkCategories.map((category) => <option key={category.id} value={category.id}>{category.name}</option>)}
                        </select>
                      </label>
                      <button className="ghost-button compact-button transaction-bulk-button" type="button" onClick={() => void bulkAssignCategory()} disabled={selectedCategorisableTransactions.length === 0 || !bulkCategoryKind || !bulkCategoryId || isApplyingBulkAction}>
                        <CategoryIcon />
                        {t("transactions.applyCategory")}
                      </button>
                    </div>
                    <div className="transaction-bulk-control">
                      <label>
                        {t("transactions.moveToAccount")}
                        <select value={bulkAccountId} onChange={(event) => setBulkAccountId(event.target.value)}>
                          <option value="">{t("common.selectAccount")}</option>
                          {accounts.map((account) => <option key={account.id} value={account.id}>{account.name}</option>)}
                        </select>
                      </label>
                      <button className="ghost-button compact-button transaction-bulk-button" type="button" onClick={() => void bulkMoveAccount()} disabled={selectedTransactionIds.length === 0 || !bulkAccountId || isApplyingBulkAction}>
                        <AccountsIcon />
                        {t("transactions.moveTransactions")}
                      </button>
                    </div>
                  </div>
                ) : null}
              </div>
            ) : null}

            <div className="transaction-table-shell">
              {visibleTransactions.length === 0 ? (
                <p className="empty-state">{t("transactions.noMatches")}</p>
              ) : (
                <>
                  <div className="transaction-table-header" aria-hidden="true">
                    <span>{t("transactions.tableMerchant")}</span>
                    <span>{t("transactions.tableAccount")}</span>
                    <span>{t("transactions.tableAmount")}</span>
                    <span>{t("transactions.tableType")}</span>
                    <span>{t("transactions.tableActions")}</span>
                  </div>
                  <div className="transaction-day-groups">
                    {groupedTransactions.map((group) => (
                      <section className="transaction-day-group" key={group.dateLabel}>
                        <div className="transaction-day-heading">
                          <div>
                            <strong>{group.label}</strong>
                            <span>{group.dateLabel}</span>
                          </div>
                          <span>{t("transactions.dayBalance")} {formatSignedCurrency(group.total, defaultCurrencyCode)}</span>
                        </div>
                        {group.transactions.map((transaction) => {
                          const account = accountById.get(transaction.accountId);
                          const category = transaction.categoryId ? categoryById.get(transaction.categoryId) : undefined;
                          const label = transactionLabel(transaction, t, category?.name);
                          const signedAmount = getTransactionSignedAmount(transaction);
                          const rowCurrencyCode = account?.currencyCode ?? defaultCurrencyCode;

                          const isEditingRow = editingTransactionId === transaction.id;

                          return (
                            <article className={`transaction-ledger-row${isEditingRow ? " is-editing" : ""}`} key={transaction.id} aria-label={t("transactions.rowLabel", { label })}>
                              <label className="transaction-row-selector">
                                <input type="checkbox" checked={selectedTransactionIds.includes(transaction.id)} onChange={(event) => toggleTransactionSelection(transaction.id, event.target.checked)} aria-label={`Select ${label}`} />
                              </label>
                              <div className="transaction-merchant-cell">
                                <span
                                  className="transaction-category-avatar"
                                  style={{ "--transaction-category-color": category?.color ?? getCategoryFallbackColor(category?.kind ?? transaction.type) } as CSSProperties}
                                >
                                  {(category?.name ?? getTransactionTypeLabel(toFormType(transaction.type), t)).slice(0, 1)}
                                </span>
                                <div>
                                  <strong>{label}</strong>
                                  <p>{category?.name ?? getTransactionTypeLabel(toFormType(transaction.type), t)}</p>
                                </div>
                              </div>
                              <div className="transaction-account-cell">
                                <strong>{account?.name ?? t("transactions.unknownAccount")}</strong>
                                <p>{formatDate(transaction.occurredOnUtc)}</p>
                              </div>
                              <div className={`transaction-amount-cell${signedAmount >= 0 ? " positive" : " negative"}`}>
                                <strong>{formatSignedCurrency(signedAmount, rowCurrencyCode)}</strong>
                              </div>
                              <div className="transaction-type-cell">
                                <span>{getTransactionTypeLabel(toFormType(transaction.type), t)}</span>
                              </div>
                              <div className="transaction-row-actions">
                                {!transaction.categoryId && !transaction.splitGroupId && (transaction.type === "Expense" || transaction.type === "Refund") ? (
                                  <label className="quick-category-control">
                                    {t("transactions.assignCategoryTo", { label })}
                                    <select
                                      aria-label={t("transactions.assignCategoryTo", { label })}
                                      value=""
                                      onChange={(event) => void assignTransactionCategory(transaction, event.target.value)}
                                    >
                                      <option value="">{t("common.chooseCategory")}</option>
                                      {expenseCategories.map((categoryOption) => (
                                        <option key={categoryOption.id} value={categoryOption.id}>
                                          {categoryOption.name}
                                        </option>
                                      ))}
                                    </select>
                                  </label>
                                ) : null}
                                <ActionMenu label={`Transaction actions for ${label}`} panelClassName="transaction-row-action-menu">
                                  <button
                                    className="action-menu-item"
                                    type="button"
                                    onClick={() => startEdit(transaction)}
                                    aria-label={`${t("transactions.edit")} ${label}`}
                                  >
                                    <EditIcon />
                                    {t("transactions.edit")}
                                  </button>
                                  <button
                                    className="action-menu-item"
                                    type="button"
                                    onClick={() => void duplicateTransaction(transaction)}
                                    aria-label={`${t("transactions.duplicate")} ${label}`}
                                  >
                                    <DuplicateIcon />
                                    {t("transactions.duplicate")}
                                  </button>
                                  <button
                                    className="action-menu-item danger-button"
                                    type="button"
                                    onClick={() => void deleteTransaction(transaction)}
                                    aria-label={`${t("transactions.delete")} ${label}`}
                                  >
                                    <TrashIcon />
                                    {t("transactions.delete")}
                                  </button>
                                </ActionMenu>
                              </div>
                              {isEditingRow && auth?.accessToken ? (
                                <div className="transaction-inline-editor">
                                  <div className="transaction-inline-editor-heading">
                                    <span>{t("transactions.editing")}</span>
                                    <h3>{t("transactions.editTransaction")}</h3>
                                  </div>
                                  <TransactionForm
                                    key={`inline-edit-${transaction.id}`}
                                    token={auth.accessToken}
                                    accounts={accounts}
                                    categories={categories}
                                    savingsGoals={savingsGoals}
                                    mode="edit"
                                    transactionId={transaction.id}
                                    initialValues={formValues}
                                    onCancel={resetForm}
                                    onError={setErrorMessage}
                                    onStatus={setStatusMessage}
                                    onSaved={async () => {
                                      resetForm();
                                      await refreshAfterMutation();
                                    }}
                                  />
                                </div>
                              ) : null}
                            </article>
                          );
                        })}
                      </section>
                    ))}
                  </div>
                </>
              )}
            </div>
          </section>
        </div>

        <aside className={`transaction-settings-panel${filtersCollapsed ? " is-collapsed" : ""}`} aria-label={t("transactions.filters")}>
          <div className="transaction-settings-header">
            <h2>{t("transactions.filters")}</h2>
            <div className="transaction-settings-header-actions">
              <button className="transaction-filter-reset" type="button" onClick={clearFilters} disabled={!hasActiveFilters}>
                {t("common.clear")}
              </button>
              <button
                className="transaction-collapse-button"
                type="button"
                onClick={() => setFiltersCollapsed((current) => !current)}
                aria-expanded={!filtersCollapsed}
                aria-controls="transaction-filter-content"
                aria-label={filtersCollapsed ? t("transactions.expandFilters") : t("transactions.collapseFilters")}
                title={filtersCollapsed ? t("transactions.expandFilters") : t("transactions.collapseFilters")}
              >
                <ChevronDownIcon />
              </button>
            </div>
          </div>

          <div id="transaction-filter-content" className="transaction-settings-content" hidden={filtersCollapsed}>
          {auth?.accessToken && <SavedLedgerViews token={auth.accessToken} onApply={params => {
            setFilterTimeZone(params.get("timeZone") ?? ""); setFocusedEntryId("");
            setFilterAccountIds(params.getAll("accountId")); setFilterCategoryIds(params.getAll("categoryId"));
            setFilterType(params.get("type") ?? ""); setFromDate(params.get("from") ?? ""); setToDate(params.get("to") ?? "");
            setMinAmount(params.get("minAmount") ?? ""); setMaxAmount(params.get("maxAmount") ?? ""); setNoteSearch(params.get("q") ?? "");
            setShowUncategorizedOnly(params.get("view") === "uncategorized" || params.get("uncategorizedOnly") === "true");
          }} />}
          <div className="transaction-filter-section">
            <span className="transaction-filter-label">{t("transactions.filterByType")}</span>
            <div className="transaction-filter-segmented" aria-label={t("transactions.filterByType")}>
              <button type="button" className={!filterType ? "active" : ""} onClick={() => setFilterType("")} aria-pressed={!filterType}>
                {t("common.allTypes")}
              </button>
              {transactionTypes.map((option) => (
                <button
                  key={option}
                  type="button"
                  className={filterType === option ? "active" : ""}
                  onClick={() => {
                    setFilterType(option);
                    if (option === "Transfer") {
                      setFilterCategoryIds([]);
                    }
                  }}
                  aria-pressed={filterType === option}
                >
                  {getTransactionTypeLabel(option, t)}
                </button>
              ))}
            </div>
          </div>

          <label className="transaction-filter-section">
            <span className="transaction-filter-label">{t("transactions.filterByAccount")}</span>
            <select value={filterAccountIds[0] ?? ""} onChange={(event) => setSingleFilterAccount(event.target.value)}>
              <option value="">{t("common.allAccounts")}</option>
              {accounts.map((account) => (
                <option key={account.id} value={account.id}>
                  {account.name}
                </option>
              ))}
            </select>
          </label>

          <div className="transaction-filter-section">
            <span className="transaction-filter-label">{t("transactions.filterByCategory")}</span>
            <div className="transaction-category-chips" aria-label={t("transactions.filterByCategory")}>
              {categories.map((category) => {
                const checked = filterCategoryIds.includes(category.id);
                const disabled = filterType === "Transfer";
                return (
                  <label
                    className={`transaction-filter-chip${checked ? " active" : ""}${disabled ? " disabled" : ""}`}
                    key={category.id}
                  >
                    <input
                      checked={checked}
                      disabled={disabled}
                      onChange={(event) => toggleCategoryFilter(category.id, event.target.checked)}
                      type="checkbox"
                    />
                    <span
                      className="transaction-filter-chip-dot"
                      style={{ background: category.color ?? getCategoryFallbackColor(category.kind) }}
                    />
                    <span>{category.name}</span>
                  </label>
                );
              })}
            </div>
            {filterType === "Transfer" ? (
              <p className="field-hint">{t("transactions.transferCategoryFilterHint")}</p>
            ) : null}
          </div>

          {filterTimeZone && <p className="form-helper">Date filters use {filterTimeZone}.</p>}
          <div className="transaction-filter-section">
            <span className="transaction-filter-label">{t("transactions.dateRange")}</span>
            <div className="transaction-filter-grid">
              <label>
                {t("transactions.fromDate")}
                <input value={fromDate} onChange={(event) => setFromDate(event.target.value)} type="date" />
              </label>
              <label>
                {t("transactions.toDate")}
                <input value={toDate} onChange={(event) => setToDate(event.target.value)} type="date" />
              </label>
            </div>
          </div>

          <div className="transaction-filter-section">
            <span className="transaction-filter-label">{t("transactions.amountRange")}</span>
            <div className="transaction-filter-grid">
              <label>
                {t("transactions.minAmount")}
                <input value={minAmount} onChange={(event) => setMinAmount(event.target.value)} type="number" step="0.01" placeholder="0" />
              </label>
              <label>
                {t("transactions.maxAmount")}
                <input value={maxAmount} onChange={(event) => setMaxAmount(event.target.value)} type="number" step="0.01" placeholder="∞" />
              </label>
            </div>
          </div>

          <label className="transaction-filter-section">
            <span className="transaction-filter-label">{t("transactions.searchNotes")}</span>
            <input value={noteSearch} onChange={(event) => setNoteSearch(event.target.value)} placeholder={t("transactions.searchPlaceholder")} />
          </label>

          <label className="transaction-toggle-row">
            <span>
              <strong>{t("transactions.uncategorizedOnly")}</strong>
              <small>{t("transactions.uncategorizedOnlyDescription")}</small>
            </span>
            <input
              aria-label={t("transactions.needsCategory")}
              checked={showUncategorizedOnly}
              onChange={(event) => setShowUncategorizedOnly(event.target.checked)}
              type="checkbox"
            />
          </label>

          <div className="transaction-filter-actions">
            <button
              className="transaction-filter-action transaction-filter-action-toggle"
              type="button"
              aria-expanded={exportActionsOpen}
              aria-controls="transaction-export-action-panel"
              onClick={() => setExportActionsOpen((current) => !current)}
            >
              <DownloadIcon />
              Export and views
              <ChevronDownIcon />
            </button>
            {exportActionsOpen ? (
              <div className="transaction-filter-action-panel" id="transaction-export-action-panel">
                <button className="transaction-filter-action" type="button" onClick={exportFilteredTransactions} disabled={visibleTransactions.length === 0}>
                  <DownloadIcon />
                  {t("transactions.exportCsv")}
                </button>
                <button className="transaction-filter-action" type="button" onClick={exportCategoriesAndBudgets} disabled={categories.length === 0}>
                  <DownloadIcon />
                  {t("transactions.exportCategoriesBudget")}
                </button>
                <button className="transaction-filter-action" type="button" onClick={saveCurrentView}>
                  <BookmarkIcon />
                  {t("transactions.saveView")}
                </button>
              </div>
            ) : null}
          </div>
          </div>
        </aside>
      </div>
    </div>
  );
}
