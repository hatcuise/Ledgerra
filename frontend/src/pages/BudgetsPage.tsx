import { FormEvent, useEffect, useMemo, useState, type CSSProperties } from "react";
import { Link } from "react-router-dom";
import { apiClient } from "../api/client";
import { useLedgerraData } from "../hooks/useLedgerraData";
import { useAuth } from "../state/AuthContext";
import { useI18n } from "../state/I18nContext";
import { useMonthSelection } from "../state/MonthContext";
import type { BudgetCategory, Category, Transaction } from "../types";
import { BookmarkIcon, BudgetsIcon, CategoryIcon, DuplicateIcon, EditIcon, TrendIcon } from "../ui/icons";
import { formatCurrency } from "../utils/format";
import { getMonthRangeParams } from "../utils/monthRange";

type BudgetStatus = "ok" | "near" | "over" | "unset";
type BudgetFilter = "all" | "ok" | "near" | "over";
type BudgetGroupId = "fixed" | "daily" | "flexible";

type BudgetEnvelope = {
  categoryId: string;
  categoryName: string;
  color: string;
  groupId: BudgetGroupId;
  planned: number;
  spent: number;
  carryForward: number;
  available: number;
  carryOverUnspent: boolean;
  remaining: number;
  operationCount: number;
  ratio: number;
  status: BudgetStatus;
};

const rowPalette = ["#34d9a8", "#c084fc", "#93c5fd", "#f9a8d4", "#fbbf24", "#fb9f7b"];
const budgetKeywords = {
  en: {
    fixed: ["rent", "mortgage", "housing", "home", "utilities", "utility", "bill", "bills", "subscription", "insurance", "internet", "phone"],
    daily: ["groceries", "grocery", "food", "dining", "restaurant", "transport", "fuel", "shopping", "health", "pharmacy"]
  },
  pl: {
    fixed: ["czynsz", "mieszkanie", "rachunki", "subskrypcje", "prad", "prąd", "woda", "abonament", "ubezpieczenie", "internet", "telefon"],
    daily: ["jedzenie", "spozywcze", "spożywcze", "restauracje", "transport", "paliwo", "zakupy", "zdrowie", "apteka"]
  },
  de: {
    fixed: ["miete", "hypothek", "wohnen", "wohnung", "nebenkosten", "rechnung", "rechnungen", "abo", "abonnement", "versicherung", "internet", "telefon", "strom", "wasser"],
    daily: ["lebensmittel", "einkauf", "essen", "restaurant", "restaurants", "verkehr", "transport", "kraftstoff", "tanken", "shopping", "gesundheit", "apotheke"]
  },
  es: {
    fixed: ["alquiler", "hipoteca", "vivienda", "casa", "servicios", "factura", "facturas", "suscripcion", "suscripción", "seguro", "internet", "telefono", "teléfono", "luz", "agua"],
    daily: ["supermercado", "comestibles", "alimentacion", "alimentación", "comida", "restaurante", "restaurantes", "transporte", "combustible", "gasolina", "compras", "salud", "farmacia"]
  }
};

function normalizeSearchText(value: string) {
  return value.toLocaleLowerCase().normalize("NFD").replace(/[\u0300-\u036f]/g, "");
}

function parseDraftAmount(value: string | undefined, fallback = 0) {
  if (!value) {
    return fallback;
  }

  const parsed = Number(value.replace(",", "."));
  return Number.isFinite(parsed) ? parsed : fallback;
}

function getMonthDate(year: number, month: number) {
  return new Date(Date.UTC(year, month - 1, 1));
}

function getDaysInMonth(year: number, month: number) {
  return new Date(year, month, 0).getDate();
}

function getBudgetTiming(year: number, month: number) {
  const now = new Date();
  const daysInMonth = getDaysInMonth(year, month);
  const selectedIndex = year * 12 + month;
  const currentIndex = now.getFullYear() * 12 + now.getMonth() + 1;
  const isCurrentMonth = selectedIndex === currentIndex;
  const isPastMonth = selectedIndex < currentIndex;
  const elapsedDays = isCurrentMonth ? Math.min(now.getDate(), daysInMonth) : isPastMonth ? daysInMonth : 0;

  return {
    daysInMonth,
    elapsedDays,
    daysRemaining: Math.max(daysInMonth - elapsedDays, 0),
    monthProgress: daysInMonth > 0 ? elapsedDays / daysInMonth : 0,
    isFutureMonth: selectedIndex > currentIndex
  };
}

function getLocale(languageCode: string) {
  if (languageCode.startsWith("pl")) return "pl-PL";
  if (languageCode.startsWith("de")) return "de-DE";
  if (languageCode.startsWith("es")) return "es-ES";
  return "en-US";
}

function formatMonthLabel(date: Date, languageCode: string) {
  return new Intl.DateTimeFormat(getLocale(languageCode), { month: "long", year: "numeric", timeZone: "UTC" }).format(date);
}

function formatCompactCurrency(value: number, currencyCode: string, languageCode: string) {
  return new Intl.NumberFormat(getLocale(languageCode), {
    style: "currency",
    currency: currencyCode,
    notation: "compact",
    maximumFractionDigits: 1
  }).format(value);
}

function getKeywordLanguage(languageCode: string): keyof typeof budgetKeywords {
  if (languageCode.startsWith("pl")) return "pl";
  if (languageCode.startsWith("de")) return "de";
  if (languageCode.startsWith("es")) return "es";
  return "en";
}

function getBudgetCopy(t: ReturnType<typeof useI18n>["t"]) {
  return {
    plan: t("budgets.plan"),
    searchLabel: t("budgets.searchLabel"),
    searchPlaceholder: t("budgets.searchPlaceholder"),
    copyPrevious: t("budgets.copyPrevious"),
    copying: t("budgets.copying"),
    template: t("budgets.template"),
    totalBudget: t("budgets.totalBudget"),
    spentAlready: t("budgets.spentAlready"),
    remaining: t("budgets.remaining"),
    dailyLimit: t("budgets.dailyLimit"),
    daysLeft: (count: number) => t("budgets.daysLeft", { count }),
    categoriesStatic: t("budgets.categoriesStatic"),
    categoriesMutable: t("budgets.categoriesMutable"),
    ofBudget: t("budgets.ofBudget"),
    pace: t("budgets.pace"),
    toKeepPlan: t("budgets.toKeepPlan"),
    dayProgress: (day: number, days: number, percent: number) =>
      t("budgets.dayProgress", { day, days, percent }),
    futureMonth: t("budgets.futureMonth"),
    envelopes: t("budgets.envelopes"),
    editInline: t("budgets.editInline"),
    all: t("budgets.all"),
    ok: t("budgets.ok"),
    attention: t("budgets.attention"),
    over: t("budgets.over"),
    addCategory: t("budgets.addCategory"),
    fixed: t("budgets.fixed"),
    fixedDescription: t("budgets.fixedDescription"),
    daily: t("budgets.daily"),
    dailyDescription: t("budgets.dailyDescription"),
    flexible: t("budgets.flexible"),
    flexibleDescription: t("budgets.flexibleDescription"),
    operation: (count: number) => t("budgets.operation", { count }),
    noOperations: t("budgets.noOperations"),
    noLimit: t("budgets.noLimit"),
    nearLimit: t("budgets.nearLimit"),
    monthlyLimit: t("budgets.monthlyLimit"),
    carryOverUnspent: t("budgets.carryOverUnspent"),
    remainingAmount: t("budgets.remainingAmount"),
    overBy: t("budgets.overBy"),
    rhythm: t("budgets.rhythm"),
    cumulative: t("budgets.cumulative"),
    actualSpending: t("budgets.actualSpending"),
    forecast: t("budgets.forecast"),
    idealPace: t("budgets.idealPace"),
    today: t("budgets.today"),
    limit: t("budgets.limit"),
    projection: t("budgets.projection"),
    vsLimit: t("budgets.vsLimit"),
    currentPaceOver: (daily: string) => t("budgets.currentPaceOver", { daily }),
    currentPaceOk: (daily: string) => t("budgets.currentPaceOk", { daily }),
    projectionTipOver: (category: string, amount: string) => t("budgets.projectionTipOver", { category, amount }),
    projectionTipOk: (amount: string) => t("budgets.projectionTipOk", { amount }),
    recurring: t("budgets.recurring"),
    manage: t("budgets.manage"),
    emptyTitle: t("budgets.emptyTitle"),
    emptyBody: t("budgets.emptyBody")
  };
}

function classifyBudgetCategory(categoryName: string, languageCode: string): BudgetGroupId {
  const normalized = normalizeSearchText(categoryName);
  const keywords = budgetKeywords[getKeywordLanguage(languageCode)];

  if (keywords.fixed.some((keyword) => normalized.includes(normalizeSearchText(keyword)))) {
    return "fixed";
  }

  if (keywords.daily.some((keyword) => normalized.includes(normalizeSearchText(keyword)))) {
    return "daily";
  }

  return "flexible";
}

function getBudgetStatus(planned: number, spent: number): BudgetStatus {
  if (planned <= 0) return "unset";
  if (spent > planned) return "over";
  if (spent / planned >= 0.9) return "near";
  return "ok";
}

function buildBudgetRows(
  expenseCategories: Category[],
  budgetCategories: BudgetCategory[],
  draft: Map<string, string>,
  carryOverDraft: Map<string, boolean>,
  transactions: Transaction[],
  selectedMonth: string,
  languageCode: string
): BudgetEnvelope[] {
  const budgetByCategory = new Map(budgetCategories.map((category) => [category.categoryId, category]));
  const monthlyExpenseTransactions = transactions.filter((transaction) => (transaction.type === "Expense" || transaction.type === "Refund") && transaction.occurredOnUtc.slice(0, 7) === selectedMonth);

  return expenseCategories.map((category, index) => {
    const budgetCategory = budgetByCategory.get(category.id);
    const fallbackPlanned = budgetCategory?.planned ?? 0;
    const planned = Math.max(parseDraftAmount(draft.get(category.id), fallbackPlanned), 0);
    const spent = budgetCategory?.spent ?? 0;
    const operationCount = monthlyExpenseTransactions.filter((transaction) => transaction.categoryId === category.id).length;
    const carryOverUnspent = carryOverDraft.get(category.id) ?? budgetCategory?.carryOverUnspent ?? false;
    const carryForward = carryOverUnspent ? budgetCategory?.carryForward ?? 0 : 0;
    const available = planned + carryForward;
    const remaining = available - spent;
    const ratio = available > 0 ? spent / available : 0;

    return {
      categoryId: category.id,
      categoryName: category.name,
      color: category.color ?? rowPalette[index % rowPalette.length],
      groupId: classifyBudgetCategory(category.name, languageCode),
      planned,
      spent,
      carryForward,
      available,
      carryOverUnspent,
      remaining,
      operationCount,
      ratio,
      status: getBudgetStatus(available, spent)
    };
  });
}

function buildTemplateDraft(rows: BudgetEnvelope[], baseline: number, draft: Map<string, string>) {
  const next = new Map(draft);
  const allocationByGroup: Record<BudgetGroupId, number> = {
    fixed: 0.5,
    daily: 0.3,
    flexible: 0.2
  };

  (Object.keys(allocationByGroup) as BudgetGroupId[]).forEach((groupId) => {
    const groupRows = rows.filter((row) => row.groupId === groupId);
    if (groupRows.length === 0) {
      return;
    }

    const groupWeight = groupRows.reduce((sum, row) => sum + Math.max(row.planned, row.spent, 1), 0);
    const groupBudget = baseline * allocationByGroup[groupId];
    groupRows.forEach((row) => {
      const rowWeight = Math.max(row.planned, row.spent, 1);
      next.set(row.categoryId, (groupBudget * rowWeight / groupWeight).toFixed(2));
    });
  });

  return next;
}

function pointsToString(points: Array<{ x: number; y: number }>) {
  return points.map((point) => `${point.x.toFixed(1)},${point.y.toFixed(1)}`).join(" ");
}

function BudgetRhythmChart({
  totalPlanned,
  totalSpent,
  projectedEnd,
  transactions,
  selectedMonth,
  timing,
  currencyCode,
  languageCode,
  copy
}: {
  totalPlanned: number;
  totalSpent: number;
  projectedEnd: number;
  transactions: Transaction[];
  selectedMonth: string;
  timing: ReturnType<typeof getBudgetTiming>;
  currencyCode: string;
  languageCode: string;
  copy: ReturnType<typeof getBudgetCopy>;
}) {
  const width = 520;
  const height = 218;
  const padding = { top: 14, right: 18, bottom: 32, left: 46 };
  const plotWidth = width - padding.left - padding.right;
  const plotHeight = height - padding.top - padding.bottom;
  const monthlyExpenseTransactions = transactions.filter((transaction) => (transaction.type === "Expense" || transaction.type === "Refund") && transaction.occurredOnUtc.slice(0, 7) === selectedMonth);
  const maxValue = Math.max(totalPlanned, totalSpent, projectedEnd, 1000) * 1.12;
  const elapsedDay = Math.max(timing.elapsedDays, 1);
  const xForDay = (day: number) => padding.left + ((day - 1) / Math.max(timing.daysInMonth - 1, 1)) * plotWidth;
  const yForValue = (value: number) => padding.top + plotHeight - (Math.max(value, 0) / maxValue) * plotHeight;
  const dailyAmounts = Array.from({ length: timing.daysInMonth }, () => 0);

  monthlyExpenseTransactions.forEach((transaction) => {
    const transactionDay = new Date(transaction.occurredOnUtc).getDate();
    if (transactionDay >= 1 && transactionDay <= timing.daysInMonth) {
      if (!transaction.parentTransactionId) dailyAmounts[transactionDay - 1] += (transaction.type === "Refund" ? -1 : 1) * Math.abs(transaction.amount);
    }
  });

  const actualByDay: Array<{ day: number; value: number }> = [];
  let runningTotal = 0;
  for (let day = 1; day <= elapsedDay; day += 1) {
    runningTotal += dailyAmounts[day - 1] ?? 0;
    actualByDay.push({ day, value: runningTotal });
  }

  const actualSeries = monthlyExpenseTransactions.length > 0
    ? actualByDay
    : [
        { day: 1, value: totalSpent * 0.72 },
        { day: Math.max(1, Math.round(elapsedDay / 2)), value: totalSpent * 0.84 },
        { day: elapsedDay, value: totalSpent }
      ];
  const dedupedActualSeries = actualSeries.filter((point, index, series) => index === 0 || point.day !== series[index - 1].day || point.value !== series[index - 1].value);
  const actualPoints = dedupedActualSeries.map((point) => ({ x: xForDay(point.day), y: yForValue(point.value) }));
  const lastActual = dedupedActualSeries[dedupedActualSeries.length - 1] ?? { day: elapsedDay, value: totalSpent };
  const forecastPoints = [
    { x: xForDay(lastActual.day), y: yForValue(lastActual.value) },
    { x: xForDay(timing.daysInMonth), y: yForValue(projectedEnd) }
  ];
  const idealPoints = [
    { x: xForDay(1), y: yForValue(0) },
    { x: xForDay(timing.daysInMonth), y: yForValue(totalPlanned) }
  ];
  const areaPoints = [
    ...actualPoints,
    { x: xForDay(lastActual.day), y: yForValue(0) },
    { x: xForDay(1), y: yForValue(0) }
  ];
  const gridValues = [0, maxValue / 2, maxValue];

  return (
    <div className="budget-rhythm-chart">
      <svg viewBox={`0 0 ${width} ${height}`} role="img" aria-label={copy.rhythm}>
        <defs>
          <linearGradient id="budgetRhythmFill" x1="0" x2="0" y1="0" y2="1">
            <stop offset="0%" stopColor="var(--accent)" stopOpacity="0.34" />
            <stop offset="100%" stopColor="var(--accent)" stopOpacity="0.02" />
          </linearGradient>
        </defs>
        {gridValues.map((value) => (
          <g key={value}>
            <line className="budget-chart-grid" x1={padding.left} x2={width - padding.right} y1={yForValue(value)} y2={yForValue(value)} />
            <text className="budget-chart-axis" x={padding.left - 10} y={yForValue(value) + 4} textAnchor="end">
              {formatCompactCurrency(value, currencyCode, languageCode)}
            </text>
          </g>
        ))}
        <line className="budget-chart-today-line" x1={xForDay(elapsedDay)} x2={xForDay(elapsedDay)} y1={padding.top} y2={height - padding.bottom} />
        <polygon className="budget-chart-area" points={pointsToString(areaPoints)} />
        <polyline className="budget-chart-ideal" points={pointsToString(idealPoints)} />
        <polyline className="budget-chart-forecast" points={pointsToString(forecastPoints)} />
        <polyline className="budget-chart-actual" points={pointsToString(actualPoints)} />
        <circle className="budget-chart-marker" cx={xForDay(lastActual.day)} cy={yForValue(lastActual.value)} r="5.5" />
        <text className="budget-chart-limit-label" x={xForDay(Math.max(2, timing.daysInMonth - 8))} y={yForValue(totalPlanned) - 8}>
          {copy.limit} · {formatCompactCurrency(totalPlanned, currencyCode, languageCode)}
        </text>
        {[1, elapsedDay, Math.ceil(timing.daysInMonth / 2), timing.daysInMonth].map((day, index) => (
          <text key={`${day}-${index}`} className="budget-chart-axis" x={xForDay(day)} y={height - 12} textAnchor={index === 0 ? "start" : index === 3 ? "end" : "middle"}>
            {day === elapsedDay ? `${copy.today} · ${day}` : day}
          </text>
        ))}
      </svg>
    </div>
  );
}

export function BudgetsPage() {
  const { auth } = useAuth();
  const { languageCode, t } = useI18n();
  const { selectedMonth, selectedYear, selectedMonthNumber } = useMonthSelection();
  const { categories, budget, profile, transactions = [], refresh } = useLedgerraData({
    categories: true,
    budget: true,
    profile: true,
    transactions: true
  });
  const copy = getBudgetCopy(t);
  const mainCurrencyCode = profile?.preferredCurrencyCode ?? "USD";
  const expenseCategories = useMemo(
    () => categories.filter((category) => category.kind === "Expense"),
    [categories]
  );

  const initialValues = useMemo(() => {
    const values = new Map<string, string>();
    budget?.categories.forEach((category) => values.set(category.categoryId, String(category.planned)));
    return values;
  }, [budget]);
  const initialCarryOverValues = useMemo(() => {
    const values = new Map<string, boolean>();
    budget?.categories.forEach((category) => values.set(category.categoryId, category.carryOverUnspent));
    return values;
  }, [budget]);

  const [draft, setDraft] = useState<Map<string, string>>(initialValues);
  const [carryOverDraft, setCarryOverDraft] = useState<Map<string, boolean>>(initialCarryOverValues);
  const [query, setQuery] = useState("");
  const [filter, setFilter] = useState<BudgetFilter>("all");
  const [isCopying, setIsCopying] = useState(false);
  const [isSaving, setIsSaving] = useState(false);
  const [errorMessage, setErrorMessage] = useState<string | null>(null);

  useEffect(() => {
    setDraft(initialValues);
  }, [initialValues]);

  useEffect(() => {
    setCarryOverDraft(initialCarryOverValues);
  }, [initialCarryOverValues]);

  const budgetRows = useMemo(
    () => buildBudgetRows(expenseCategories, budget?.categories ?? [], draft, carryOverDraft, transactions, selectedMonth, languageCode),
    [budget?.categories, carryOverDraft, draft, expenseCategories, languageCode, selectedMonth, transactions]
  );
  const timing = useMemo(() => getBudgetTiming(selectedYear, selectedMonthNumber), [selectedMonthNumber, selectedYear]);
  const monthLabel = formatMonthLabel(getMonthDate(selectedYear, selectedMonthNumber), languageCode);
  const monthRangeParams = getMonthRangeParams(selectedMonth);
  const totalPlanned = budgetRows.reduce((sum, row) => sum + row.available, 0);
  const totalSpent = budgetRows.reduce((sum, row) => sum + row.spent, 0);
  const totalRemaining = totalPlanned - totalSpent;
  const spentRatio = totalPlanned > 0 ? totalSpent / totalPlanned : 0;
  const monthProgressPercent = Math.round(timing.monthProgress * 100);
  const budgetProgressPercent = Math.round(Math.min(spentRatio, 1) * 100);
  const dailyLimit = timing.daysRemaining > 0 ? Math.max(totalRemaining, 0) / timing.daysRemaining : Math.max(totalRemaining, 0);
  const dailyAverage = timing.elapsedDays > 0 ? totalSpent / timing.elapsedDays : 0;
  const projectedEnd = timing.elapsedDays > 0 ? dailyAverage * timing.daysInMonth : totalSpent;
  const projectionDelta = projectedEnd - totalPlanned;
  const pressureCategory = [...budgetRows]
    .filter((row) => row.planned > 0)
    .sort((first, second) => second.ratio - first.ratio)[0];
  const normalizedQuery = normalizeSearchText(query.trim());
  const visibleRows = budgetRows.filter((row) => {
    const matchesQuery = normalizedQuery.length === 0 || normalizeSearchText(row.categoryName).includes(normalizedQuery);
    const matchesFilter = filter === "all" || row.status === filter || (filter === "near" && row.status === "near");
    return matchesQuery && matchesFilter;
  });
  const groupedRows = [
    { id: "fixed" as const, label: copy.fixed, description: copy.fixedDescription, rows: visibleRows.filter((row) => row.groupId === "fixed") },
    { id: "daily" as const, label: copy.daily, description: copy.dailyDescription, rows: visibleRows.filter((row) => row.groupId === "daily") },
    { id: "flexible" as const, label: copy.flexible, description: copy.flexibleDescription, rows: visibleRows.filter((row) => row.groupId === "flexible") }
  ].filter((group) => group.rows.length > 0);
  const statusCounts = budgetRows.reduce<Record<BudgetStatus, number>>(
    (counts, row) => ({ ...counts, [row.status]: counts[row.status] + 1 }),
    { ok: 0, near: 0, over: 0, unset: 0 }
  );
  const recurringRows = budgetRows
    .filter((row) => row.groupId === "fixed" && row.planned > 0)
    .sort((first, second) => second.planned - first.planned)
    .slice(0, 3);

  const handleSubmit = async (event: FormEvent) => {
    event.preventDefault();
    if (!auth?.accessToken || isSaving) {
      return;
    }

    setErrorMessage(null);
    setIsSaving(true);

    const payload = expenseCategories
      .map((category) => ({
        categoryId: category.id,
        plannedAmount: parseDraftAmount(draft.get(category.id), 0),
        carryOverUnspent: carryOverDraft.get(category.id) ?? false
      }))
      .filter((item) => item.plannedAmount > 0);

    try {
      await apiClient.updateBudget(auth.accessToken, selectedYear, selectedMonthNumber, payload);
      await refresh();
    } catch (error) {
      console.error("Unable to save budget", error);
      setErrorMessage(error instanceof Error ? error.message : t("budgets.saveError"));
    } finally {
      setIsSaving(false);
    }
  };

  const handleCopyPreviousMonth = async () => {
    if (!auth?.accessToken) {
      return;
    }

    const previousMonthDate = new Date(Date.UTC(selectedYear, selectedMonthNumber - 2, 1));
    setErrorMessage(null);
    setIsCopying(true);

    try {
      const previousBudget = await apiClient.getBudget(auth.accessToken, previousMonthDate.getUTCFullYear(), previousMonthDate.getUTCMonth() + 1);
      const next = new Map(draft);
      previousBudget.categories.forEach((category) => {
        next.set(category.categoryId, String(category.planned));
      });
      setDraft(next);
      setCarryOverDraft(new Map(previousBudget.categories.map((category) => [category.categoryId, category.carryOverUnspent])));
    } catch (error) {
      console.error("Unable to copy previous budget", error);
      setErrorMessage(error instanceof Error ? error.message : t("budgets.copyPreviousError"));
    } finally {
      setIsCopying(false);
    }
  };

  const handleApplyTemplate = () => {
    if (budgetRows.length === 0) {
      return;
    }

    setDraft(buildTemplateDraft(budgetRows, Math.max(totalPlanned, totalSpent, 1000), draft));
  };

  return (
    <form className="budget-plan-page" onSubmit={handleSubmit}>
      <div className="budget-page-topbar">
        <div className="budget-breadcrumb" aria-label="Breadcrumb">
          <span>Ledgerra</span>
          <span>/</span>
          <span>{copy.plan}</span>
          <span>/</span>
          <strong>{t("nav.budgets")} · {monthLabel}</strong>
        </div>
        <label className="budget-search">
          <span className="sr-only">{copy.searchLabel}</span>
          <input value={query} onChange={(event) => setQuery(event.target.value)} placeholder={copy.searchPlaceholder} />
        </label>
      </div>

      <section className="budget-hero">
        <div className="budget-hero-copy">
          <span className="eyebrow">{t("budgets.eyebrow")}</span>
          <h1>{t("budgets.title")}</h1>
          <p>
            {t("budgets.description")}
            <strong>{timing.isFutureMonth ? copy.futureMonth : copy.dayProgress(timing.elapsedDays, timing.daysInMonth, monthProgressPercent)}</strong>
          </p>
        </div>

        <div className="budget-hero-actions">
          <div className="budget-period-toggle" role="group" aria-label={t("appShell.month")}>
            <span className="active" aria-current="true">{t("appShell.month")}</span>
          </div>
          <button className="budget-secondary-action" type="button" onClick={handleCopyPreviousMonth} disabled={isCopying}>
            <DuplicateIcon />
            {isCopying ? copy.copying : copy.copyPrevious}
          </button>
          <button className="budget-secondary-action" type="button" onClick={handleApplyTemplate}>
            <BookmarkIcon />
            {copy.template}
          </button>
          <button className="budget-primary-action" type="submit" disabled={isSaving}>
            <span aria-hidden="true">✓</span>
            {isSaving ? t("budgets.saving") : t("budgets.saveBudget")}
          </button>
        </div>
      </section>

      {errorMessage ? <p className="form-error" role="alert">{errorMessage}</p> : null}

      <section className="budget-summary-grid" aria-label={t("budgets.progress")}>
        <article className="budget-summary-card">
          <span>{copy.totalBudget}</span>
          <strong>{formatCurrency(totalPlanned, mainCurrencyCode)}</strong>
          <p>{budgetRows.length} {copy.categoriesStatic} · {budgetRows.filter((row) => row.planned > 0).length} {copy.categoriesMutable}</p>
        </article>
        <article className="budget-summary-card">
          <span>{copy.spentAlready}</span>
          <strong>{formatCurrency(totalSpent, mainCurrencyCode)}</strong>
          <div className="budget-summary-progress" aria-hidden="true">
            <div style={{ width: `${budgetProgressPercent}%` }} />
          </div>
          <p><b>{budgetProgressPercent}%</b> {copy.ofBudget} · {copy.pace} {monthProgressPercent}%</p>
        </article>
        <article className={totalRemaining < 0 ? "budget-summary-card is-negative" : "budget-summary-card is-positive"}>
          <span>{copy.remaining}</span>
          <strong>{formatCurrency(totalRemaining, mainCurrencyCode)}</strong>
          <p>{copy.daysLeft(timing.daysRemaining)}</p>
        </article>
        <article className="budget-summary-card">
          <span>{copy.dailyLimit}</span>
          <strong>{formatCurrency(dailyLimit, mainCurrencyCode)}</strong>
          <p>{copy.toKeepPlan}</p>
        </article>
      </section>

      <div className="budget-workspace-grid">
        <section className="budget-envelope-panel">
          <div className="budget-panel-heading">
            <div>
              <h2>{copy.envelopes}</h2>
              <p>{copy.editInline}</p>
            </div>
            <div className="budget-envelope-tools">
              <div className="budget-filter-tabs" role="group" aria-label={copy.envelopes}>
                {[
                  { id: "all" as const, label: copy.all, count: budgetRows.length },
                  { id: "ok" as const, label: copy.ok, count: statusCounts.ok },
                  { id: "near" as const, label: copy.attention, count: statusCounts.near },
                  { id: "over" as const, label: copy.over, count: statusCounts.over }
                ].map((option) => (
                  <button key={option.id} className={filter === option.id ? "active" : ""} type="button" onClick={() => setFilter(option.id)}>
                    {option.label}
                    <span>{option.count}</span>
                  </button>
                ))}
              </div>
              <a className="budget-add-category" href="/categories">
                <span aria-hidden="true">+</span>
                {copy.addCategory}
              </a>
            </div>
          </div>

          {groupedRows.length === 0 ? (
            <div className="budget-empty-state">
              <strong>{copy.emptyTitle}</strong>
              <p>{copy.emptyBody}</p>
            </div>
          ) : (
            <div className="budget-groups">
              {groupedRows.map((group) => {
                const groupPlanned = group.rows.reduce((sum, row) => sum + row.planned, 0);
                const groupSpent = group.rows.reduce((sum, row) => sum + row.spent, 0);
                const groupPercent = groupPlanned > 0 ? Math.round((groupSpent / groupPlanned) * 100) : 0;

                return (
                  <section key={group.id} className="budget-envelope-group">
                    <div className="budget-group-heading">
                      <div>
                        <span><BudgetsIcon /> {group.label}</span>
                        <p>{group.description}</p>
                      </div>
                      <strong>{formatCurrency(groupSpent, mainCurrencyCode)} <span>{t("common.of")} {formatCurrency(groupPlanned, mainCurrencyCode)} · {groupPercent}%</span></strong>
                    </div>

                    <div className="budget-envelope-list">
                      {group.rows.map((row) => {
                        const progressWidth = `${Math.min(row.ratio, 1) * 100}%`;
                        const inputId = `budget-limit-${row.categoryId}`;
                        const statusLabel = row.status === "over"
                          ? copy.over
                          : row.status === "near"
                            ? copy.nearLimit
                            : row.status === "unset"
                              ? copy.noLimit
                              : copy.ok;

                        return (
                          <article
                            key={row.categoryId}
                            className={`budget-envelope-row is-${row.status}`}
                            style={{ "--row-accent": row.color } as CSSProperties}
                          >
                            <div className="budget-envelope-icon" aria-hidden="true">
                              <CategoryIcon />
                            </div>
                            <div className="budget-envelope-main">
                              <div className="budget-envelope-titleline">
                                <Link to={`/transactions?type=Expense&categoryId=${row.categoryId}&${monthRangeParams}`}><strong>{row.categoryName}</strong></Link>
                                <span>· {row.operationCount > 0 ? copy.operation(row.operationCount) : copy.noOperations}</span>
                                <em>{statusLabel}</em>
                              </div>
                              <p>{row.planned > 0 ? `${copy.pace} ${Math.round(row.ratio * 100)}%` : copy.noLimit}</p>
                              <div className="budget-envelope-progress" aria-hidden="true">
                                <div style={{ width: progressWidth }} />
                              </div>
                              <div className="budget-envelope-meta">
                                <span><b>{formatCurrency(row.spent, mainCurrencyCode)}</b> {t("common.of")} {formatCurrency(row.available, mainCurrencyCode)}</span>
                                <strong>
                                  {row.remaining < 0 ? copy.overBy : copy.remainingAmount} {formatCurrency(Math.abs(row.remaining), mainCurrencyCode)}
                                </strong>
                              </div>
                            </div>
                            <div className="budget-envelope-limit">
                              <span>{copy.monthlyLimit}</span>
                              <div className="budget-limit-input-wrap">
                                <label className="sr-only" htmlFor={inputId}>{row.categoryName}</label>
                                <input
                                  id={inputId}
                                  type="number"
                                  step="0.01"
                                  value={draft.get(row.categoryId) ?? ""}
                                  onChange={(event) => {
                                    const next = new Map(draft);
                                    next.set(row.categoryId, event.target.value);
                                    setDraft(next);
                                  }}
                                />
                                <EditIcon aria-hidden="true" />
                              </div>
                              <label className="budget-rollover-toggle">
                                <input
                                  type="checkbox"
                                  checked={row.carryOverUnspent}
                                  onChange={(event) => {
                                    const next = new Map(carryOverDraft);
                                    next.set(row.categoryId, event.target.checked);
                                    setCarryOverDraft(next);
                                  }}
                                />
                                <span>{copy.carryOverUnspent}</span>
                              </label>
                            </div>
                          </article>
                        );
                      })}
                    </div>
                  </section>
                );
              })}
            </div>
          )}
        </section>

        <aside className="budget-side-stack">
          <section className="budget-side-card">
            <div className="budget-side-card-heading">
              <h2><TrendIcon /> {copy.rhythm}</h2>
              <span>{copy.cumulative} · {monthLabel}</span>
            </div>
            <BudgetRhythmChart
              totalPlanned={totalPlanned}
              totalSpent={totalSpent}
              projectedEnd={projectedEnd}
              transactions={transactions}
              selectedMonth={selectedMonth}
              timing={timing}
              currencyCode={mainCurrencyCode}
              languageCode={languageCode}
              copy={copy}
            />
            <div className="budget-chart-legend">
              <span className="actual">{copy.actualSpending}</span>
              <span className="forecast">{copy.forecast}</span>
              <span className="ideal">{copy.idealPace}</span>
            </div>
          </section>

          <section className="budget-side-card budget-projection-card">
            <div className="budget-side-card-heading">
              <h2><TrendIcon /> {copy.projection}</h2>
            </div>
            <strong className={projectionDelta > 0 ? "is-warning" : "is-good"}>
              {formatCurrency(projectedEnd, mainCurrencyCode)}
              <span>{projectionDelta >= 0 ? "+" : ""}{formatCurrency(projectionDelta, mainCurrencyCode)} {copy.vsLimit}</span>
            </strong>
            <p>{projectionDelta > 0 ? copy.currentPaceOver(formatCurrency(dailyAverage, mainCurrencyCode)) : copy.currentPaceOk(formatCurrency(dailyAverage, mainCurrencyCode))}</p>
            <div className="budget-tip">
              <BookmarkIcon />
              <span>
                {projectionDelta > 0 && pressureCategory
                  ? copy.projectionTipOver(pressureCategory.categoryName, formatCurrency(dailyLimit, mainCurrencyCode))
                  : copy.projectionTipOk(formatCurrency(dailyLimit, mainCurrencyCode))}
              </span>
            </div>
          </section>

          <section className="budget-side-card">
            <div className="budget-side-card-heading">
              <h2><BudgetsIcon /> {copy.recurring} · {monthLabel}</h2>
              <a href="/categories">{copy.manage} →</a>
            </div>
            <div className="budget-recurring-list">
              {(recurringRows.length > 0 ? recurringRows : budgetRows.slice(0, 3)).map((row) => (
                <article key={`recurring-${row.categoryId}`}>
                  <span style={{ background: row.color }} />
                  <div>
                    <Link to={`/transactions?type=Expense&categoryId=${row.categoryId}&${monthRangeParams}`}><strong>{row.categoryName}</strong></Link>
                    <p>{row.operationCount > 0 ? copy.operation(row.operationCount) : copy.noOperations}</p>
                  </div>
                  <b>{formatCurrency(row.planned, mainCurrencyCode)}</b>{row.carryForward > 0 ? ` (+${formatCurrency(row.carryForward, mainCurrencyCode)} rollover)` : ""}
                </article>
              ))}
            </div>
          </section>
        </aside>
      </div>
    </form>
  );
}
