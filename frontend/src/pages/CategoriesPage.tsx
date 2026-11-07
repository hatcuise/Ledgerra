import { CategoryOrganization } from "../components/CategoryOrganization";
import { FormEvent, useEffect, useMemo, useState, type CSSProperties } from "react";
import { apiClient } from "../api/client";
import { useLedgerraData } from "../hooks/useLedgerraData";
import { useAuth } from "../state/AuthContext";
import { useI18n } from "../state/I18nContext";
import type { Category, ImportRule, Transaction } from "../types";
import {
  ArchiveIcon,
  BasketIcon,
  BoltIcon,
  BriefcaseIcon,
  CalendarIcon,
  CategoryIcon,
  DownloadIcon,
  DuplicateIcon,
  EditIcon,
  ExpenseIcon,
  GoalsIcon,
  GripIcon,
  HeartIcon,
  HomeIcon,
  IncomeIcon,
  PlusIcon,
  SearchIcon,
  SparklesIcon,
  TransitIcon,
  TrendIcon,
  UtensilsIcon
} from "../ui/icons";
import { ActionMenu } from "../ui/ActionMenu";
import { PageHeader } from "../ui/PageHeader";
import { formatCurrency } from "../utils/format";

type CategoryKind = "Expense" | "Income";
type CategoryFilter = "all" | "expense" | "income" | "attention";
type CategoryGroupId = "fixed" | "daily" | "lifestyle" | "income";
type CategoryIconKey =
  | "basket"
  | "dining"
  | "transport"
  | "home"
  | "utilities"
  | "health"
  | "income"
  | "freelance"
  | "calendar"
  | "goal"
  | "trend"
  | "category";

type CategoryPreferences = {
  groups: Record<string, CategoryGroupId>;
  icons: Record<string, CategoryIconKey>;
};

type CategoryRow = {
  category: Category;
  kind: CategoryKind;
  color: string;
  groupId: CategoryGroupId;
  iconKey: CategoryIconKey;
  amount: number;
  share: number;
  monthlyOperations: number;
  allOperations: number;
  ruleCount: number;
  lastActivityUtc?: string;
  sparklineValues: number[];
  attentionReasons: string[];
  canArchive: boolean;
};

const preferencesStorageKey = "ledgerra:category-view-preferences";
const fallbackPalette = ["#34d9a8", "#73b8f2", "#a7b8ff", "#c790f5", "#f8b894", "#f8c766", "#f1786b", "#9eb3b6"];
const colorSwatches = ["#34d9a8", "#73b8f2", "#a7b8ff", "#c790f5", "#f8b894", "#f8c766", "#f1786b", "#9eb3b6"];
const groupOrder: CategoryGroupId[] = ["fixed", "daily", "lifestyle", "income"];

const categoryIconOptions: Array<{ id: CategoryIconKey; label: string; Icon: typeof CategoryIcon }> = [
  { id: "basket", label: "Groceries", Icon: BasketIcon },
  { id: "dining", label: "Dining", Icon: UtensilsIcon },
  { id: "transport", label: "Transport", Icon: TransitIcon },
  { id: "home", label: "Home", Icon: HomeIcon },
  { id: "utilities", label: "Utilities", Icon: BoltIcon },
  { id: "health", label: "Health", Icon: HeartIcon },
  { id: "income", label: "Income", Icon: IncomeIcon },
  { id: "freelance", label: "Freelance", Icon: BriefcaseIcon },
  { id: "calendar", label: "Calendar", Icon: CalendarIcon },
  { id: "goal", label: "Goal", Icon: GoalsIcon },
  { id: "trend", label: "Trend", Icon: TrendIcon },
  { id: "category", label: "Category", Icon: CategoryIcon }
];

const categoryCopy = {
  en: {
    breadcrumb: (month: string) => `LEDGERRA / PLAN / CATEGORIES · ${month}`,
    totalCategories: "All categories",
    inGroups: (count: number) => `in ${count} groups`,
    expenseVsIncome: "Expenses vs income",
    expenseShort: "expenses",
    incomeShort: "inflows / goals",
    mostUsed: "Most used",
    needsReview: "To organize",
    reviewSummary: (unused: number, withoutRules: number) => `${unused} unused · ${withoutRules} without rules`,
    export: "Export",
    suggestions: "Merge suggestions",
    newCategory: "New category",
    yourCategories: "Your categories",
    searchPlaceholder: "Search categories...",
    filters: {
      all: "All",
      expense: "Expenses",
      income: "Income",
      attention: "Notes"
    },
    groupLabels: {
      fixed: "Fixed commitments",
      daily: "Everyday life",
      lifestyle: "Lifestyle",
      income: "Income and goals"
    },
    groupDescriptions: {
      fixed: "Paid automatically or predictably",
      daily: "Operational day-to-day spending",
      lifestyle: "Leisure, sport, hobbies",
      income: "Money coming in"
    },
    operations: (count: number) => `${count} op.`,
    rules: (count: number) => `${count} ${count === 1 ? "rule" : "rules"}`,
    noRules: "no rules",
    system: "system",
    custom: "custom",
    noActivity: "no activity",
    today: "today",
    yesterday: "yesterday",
    edit: "Edit",
    duplicate: "Duplicate",
    archive: "Archive",
    archiveUnavailable: "Used categories cannot be archived",
    archiveUnavailableFor: (name: string) => `Archive unavailable for ${name}`,
    actionMenuLabel: "Category actions",
    rowActionMenuLabel: (name: string) => `Category actions for ${name}`,
    categoryEditor: "Category editor",
    editInline: (name: string) => `Edit ${name} inline`,
    newInline: "New category inline",
    close: "Close",
    livePreview: "Live preview",
    previewName: "Category name...",
    name: "Name",
    namePlaceholder: "e.g. Work coffee",
    group: "Group",
    type: "Type",
    color: "Color",
    icon: "Icon",
    addRule: "Add rule",
    saveCategory: "Save category",
    updateCategory: "Update category",
    saving: "Saving...",
    created: "Category saved.",
    updated: "Category updated.",
    archived: "Category archived.",
    exported: "Categories exported.",
    unableToSave: "Unable to save this category.",
    unableToArchive: "Unable to archive this category.",
    emptyTitle: "No categories in this view",
    emptyBody: "Adjust the search or choose another filter.",
    systemSuggestions: "System suggestions",
    mergeTitle: (first: string, second: string) => `Review "${first}" and "${second}"`,
    mergeBody: "These names look close enough to simplify reports.",
    ruleTitle: (name: string) => `Add a rule for "${name}"`,
    ruleBody: "Regular operations are easier to classify automatically.",
    archiveTitle: (count: number) => `Archive ${count} unused`,
    archiveBody: "Unused custom labels can be removed from the list.",
    unusedTitle: (count: number) => `Review ${count} unused`,
    unusedBody: "Unused system labels are worth checking before imports grow.",
    review: "Review"
  },
  pl: {
    breadcrumb: (month: string) => `LEDGERRA / PLAN / KATEGORIE · ${month}`,
    totalCategories: "Wszystkich kategorii",
    inGroups: (count: number) => `w ${count} grupach`,
    expenseVsIncome: "Wydatki vs przychody",
    expenseShort: "wydatki",
    incomeShort: "wpływy / cele",
    mostUsed: "Najczęściej używana",
    needsReview: "Do uporządkowania",
    reviewSummary: (unused: number, withoutRules: number) => `${unused} nieużywanych · ${withoutRules} bez reguł`,
    export: "Eksportuj",
    suggestions: "Sugestie scalania",
    newCategory: "Nowa kategoria",
    yourCategories: "Twoje kategorie",
    searchPlaceholder: "Szukaj kategorii...",
    filters: {
      all: "Wszystkie",
      expense: "Wydatki",
      income: "Wpływy",
      attention: "Uwagi"
    },
    groupLabels: {
      fixed: "Stałe zobowiązania",
      daily: "Codzienne życie",
      lifestyle: "Styl życia",
      income: "Przychody i cele"
    },
    groupDescriptions: {
      fixed: "Płatne automatycznie lub przewidywalnie",
      daily: "Operacyjne wydatki dnia",
      lifestyle: "Rozrywka, sport, hobby",
      income: "Pieniądze, które wpływają"
    },
    operations: (count: number) => `${count} op.`,
    rules: (count: number) => `${count} reguł`,
    noRules: "bez reguł",
    system: "systemowa",
    custom: "własna",
    noActivity: "brak aktywności",
    today: "dzisiaj",
    yesterday: "wczoraj",
    edit: "Edytuj",
    duplicate: "Duplikuj",
    archive: "Archiwizuj",
    archiveUnavailable: "Używanych kategorii nie można archiwizować",
    archiveUnavailableFor: (name: string) => `Archiwizacja niedostępna dla ${name}`,
    actionMenuLabel: "Akcje kategorii",
    rowActionMenuLabel: (name: string) => `Akcje kategorii dla ${name}`,
    categoryEditor: "Edytor kategorii",
    editInline: (name: string) => `Edytuj ${name} inline`,
    newInline: "Nowa kategoria inline",
    close: "Zamknij",
    livePreview: "Podgląd na żywo",
    previewName: "Nazwa kategorii...",
    name: "Nazwa",
    namePlaceholder: "np. Kawa służbowa",
    group: "Grupa",
    type: "Typ",
    color: "Kolor",
    icon: "Ikona",
    addRule: "Dodaj regułę",
    saveCategory: "Zapisz kategorię",
    updateCategory: "Zaktualizuj kategorię",
    saving: "Zapisywanie...",
    created: "Kategoria zapisana.",
    updated: "Kategoria zaktualizowana.",
    archived: "Kategoria zarchiwizowana.",
    exported: "Kategorie wyeksportowane.",
    unableToSave: "Nie udało się zapisać tej kategorii.",
    unableToArchive: "Nie udało się zarchiwizować tej kategorii.",
    emptyTitle: "Brak kategorii w tym widoku",
    emptyBody: "Zmień wyszukiwanie albo wybierz inny filtr.",
    systemSuggestions: "Sugestie systemu",
    mergeTitle: (first: string, second: string) => `Przejrzyj "${first}" i "${second}"`,
    mergeBody: "Te nazwy są podobne i mogą uprościć raport.",
    ruleTitle: (name: string) => `Dodaj regułę dla "${name}"`,
    ruleBody: "Powtarzalne operacje łatwiej klasyfikować automatycznie.",
    archiveTitle: (count: number) => `Archiwizuj ${count} nieużywane`,
    archiveBody: "Nieużywane własne etykiety można usunąć z listy.",
    unusedTitle: (count: number) => `Przejrzyj ${count} nieużywane`,
    unusedBody: "Nieużywane etykiety systemowe warto sprawdzić przed kolejnymi importami.",
    review: "Przejrzyj"
  }
};

type CategoryCopyKey = keyof typeof categoryCopy;

function isCategoryCopyKey(value: string): value is CategoryCopyKey {
  return Object.prototype.hasOwnProperty.call(categoryCopy, value);
}

function getCategoryPageCopy(languageCode: string) {
  const primaryLanguage = languageCode.split("-")[0].toLowerCase();
  return isCategoryCopyKey(primaryLanguage) ? categoryCopy[primaryLanguage] : categoryCopy.en;
}

function useMediaQuery(query: string) {
  const getMatches = () => {
    if (typeof window === "undefined" || typeof window.matchMedia !== "function") {
      return false;
    }

    return window.matchMedia(query).matches;
  };

  const [matches, setMatches] = useState(getMatches);

  useEffect(() => {
    if (typeof window === "undefined" || typeof window.matchMedia !== "function") {
      return undefined;
    }

    const mediaQuery = window.matchMedia(query);
    const updateMatches = () => setMatches(mediaQuery.matches);
    updateMatches();
    mediaQuery.addEventListener("change", updateMatches);

    return () => mediaQuery.removeEventListener("change", updateMatches);
  }, [query]);

  return matches;
}

function getCategoryKindLabel(kind: string, t: ReturnType<typeof useI18n>["t"]) {
  switch (kind) {
    case "Expense":
      return t("transactionType.Expense");
    case "Income":
      return t("transactionType.Income");
    default:
      return kind;
  }
}

function normalizeSearchText(value: string) {
  return value.toLocaleLowerCase().normalize("NFD").replace(/[\u0300-\u036f]/g, "");
}

function getLocale(languageCode: string) {
  if (languageCode.startsWith("pl")) return "pl-PL";
  if (languageCode.startsWith("de")) return "de-DE";
  if (languageCode.startsWith("es")) return "es-ES";
  return "en-US";
}

function formatMonthLabel(monthKey: string, languageCode: string) {
  const [year, month] = monthKey.split("-").map(Number);
  const date = new Date(Date.UTC(year, month - 1, 1));
  return new Intl.DateTimeFormat(getLocale(languageCode), { month: "long", year: "numeric", timeZone: "UTC" }).format(date);
}

function getDaysInSelectedMonth(monthKey: string) {
  const [year, month] = monthKey.split("-").map(Number);
  return new Date(Date.UTC(year, month, 0)).getUTCDate();
}

function readCategoryPreferences(): CategoryPreferences {
  if (typeof window === "undefined") {
    return { groups: {}, icons: {} };
  }

  try {
    const value = window.localStorage.getItem(preferencesStorageKey);
    if (!value) {
      return { groups: {}, icons: {} };
    }

    const parsed = JSON.parse(value) as Partial<CategoryPreferences>;
    return {
      groups: parsed.groups ?? {},
      icons: parsed.icons ?? {}
    };
  } catch {
    return { groups: {}, icons: {} };
  }
}

function writeCategoryPreferences(preferences: CategoryPreferences) {
  if (typeof window !== "undefined") {
    window.localStorage.setItem(preferencesStorageKey, JSON.stringify(preferences));
  }
}

function getSafeKind(kind: string): CategoryKind {
  return kind === "Income" ? "Income" : "Expense";
}

function getCategoryGroup(category: Category, languageCode: string): CategoryGroupId {
  if (category.kind === "Income") {
    return "income";
  }

  const normalized = normalizeSearchText(category.name);
  const fixedKeywords = [
    "rent",
    "mortgage",
    "utilities",
    "utility",
    "subscription",
    "insurance",
    "internet",
    "phone",
    "czynsz",
    "mieszkanie",
    "rachunki",
    "subskrypcje",
    "abonament",
    "ubezpieczenie"
  ];
  const dailyKeywords = [
    "groceries",
    "grocery",
    "dining",
    "restaurant",
    "transport",
    "fuel",
    "health",
    "pharmacy",
    "shopping",
    "spozywcze",
    "spożywcze",
    "restauracje",
    "transport",
    "zdrowie",
    "apteka",
    "zakupy"
  ];

  if (fixedKeywords.some((keyword) => normalized.includes(normalizeSearchText(keyword)))) {
    return "fixed";
  }

  if (dailyKeywords.some((keyword) => normalized.includes(normalizeSearchText(keyword)))) {
    return "daily";
  }

  return languageCode.startsWith("pl") && normalized.includes("styl") ? "lifestyle" : "lifestyle";
}

function getDefaultCategoryIcon(category: Category): CategoryIconKey {
  const normalized = normalizeSearchText(category.name);

  if (category.kind === "Income") {
    return normalized.includes("freelance") || normalized.includes("faktura") ? "freelance" : "income";
  }

  if (normalized.includes("rent") || normalized.includes("home") || normalized.includes("mieszkanie") || normalized.includes("czynsz")) {
    return "home";
  }

  if (normalized.includes("utilit") || normalized.includes("bill") || normalized.includes("rach") || normalized.includes("prad") || normalized.includes("prąd")) {
    return "utilities";
  }

  if (normalized.includes("transport") || normalized.includes("fuel") || normalized.includes("paliwo")) {
    return "transport";
  }

  if (normalized.includes("dining") || normalized.includes("restaurant") || normalized.includes("restaur")) {
    return "dining";
  }

  if (normalized.includes("health") || normalized.includes("pharma") || normalized.includes("zdrow") || normalized.includes("apte")) {
    return "health";
  }

  if (normalized.includes("grocer") || normalized.includes("spozy") || normalized.includes("spoży")) {
    return "basket";
  }

  return "category";
}

function getIconByKey(iconKey: CategoryIconKey) {
  return categoryIconOptions.find((option) => option.id === iconKey)?.Icon ?? CategoryIcon;
}

function getLastActivity(transactions: Transaction[]) {
  const dates = transactions
    .map((transaction) => transaction.occurredOnUtc)
    .sort();
  return dates[dates.length - 1];
}

function formatActivityLabel(value: string | undefined, languageCode: string, copy: ReturnType<typeof getCategoryPageCopy>) {
  if (!value) {
    return copy.noActivity;
  }

  const activityDate = new Date(value);
  const today = new Date();
  const activityKey = activityDate.toISOString().slice(0, 10);
  const todayKey = today.toISOString().slice(0, 10);
  const yesterday = new Date(today);
  yesterday.setDate(today.getDate() - 1);
  const yesterdayKey = yesterday.toISOString().slice(0, 10);

  if (activityKey === todayKey) {
    return copy.today;
  }

  if (activityKey === yesterdayKey) {
    return copy.yesterday;
  }

  return new Intl.DateTimeFormat(getLocale(languageCode), { month: "short", day: "numeric" }).format(activityDate);
}

function buildSparklineValues(transactions: Transaction[], daysInMonth: number) {
  const bucketCount = 10;
  const buckets = Array.from({ length: bucketCount }, () => 0);

  transactions.forEach((transaction) => {
    const day = Math.max(1, Math.min(new Date(transaction.occurredOnUtc).getUTCDate(), daysInMonth));
    const index = Math.min(bucketCount - 1, Math.floor(((day - 1) / daysInMonth) * bucketCount));
    buckets[index] += (transaction.type === "Refund" ? -1 : 1) * Math.abs(transaction.amount);
  });

  return buckets;
}

function buildSparklinePath(values: number[], width = 116, height = 34) {
  if (values.length < 2 || values.every((value) => value === 0)) {
    return `M 0 ${height / 2} L ${width} ${height / 2}`;
  }

  const max = Math.max(...values, 1);
  return values
    .map((value, index) => {
      const x = (index / (values.length - 1)) * width;
      const y = height - 4 - (value / max) * (height - 8);
      return `${index === 0 ? "M" : "L"} ${x.toFixed(1)} ${y.toFixed(1)}`;
    })
    .join(" ");
}

function escapeCsv(value: string | number | boolean) {
  const serialized = String(value);
  const neutralized = /^[=+\-@]/.test(serialized) ? `'${serialized}` : serialized;
  return /[",\n]/.test(neutralized) ? `"${neutralized.replace(/"/g, '""')}"` : neutralized;
}

function findSimilarCategoryPair(rows: CategoryRow[]) {
  for (let index = 0; index < rows.length; index += 1) {
    for (let nextIndex = index + 1; nextIndex < rows.length; nextIndex += 1) {
      const current = rows[index];
      const next = rows[nextIndex];
      const currentName = normalizeSearchText(current.category.name);
      const nextName = normalizeSearchText(next.category.name);

      if (current.kind !== next.kind) {
        continue;
      }

      if (
        currentName.includes(nextName) ||
        nextName.includes(currentName) ||
        (currentName.includes("dining") && nextName.includes("restaurant")) ||
        (currentName.includes("restaurant") && nextName.includes("dining"))
      ) {
        return [current, next] as const;
      }
    }
  }

  return null;
}

export function CategoriesPage() {
  const { auth } = useAuth();
  const { t, languageCode } = useI18n();
  const { categories, transactions = [], budget, importRules, profile, selectedMonth, loading, error, refresh } = useLedgerraData({
    categories: true,
    transactions: true,
    budget: true,
    importRules: true,
    profile: true
  });
  const copy = getCategoryPageCopy(languageCode);
  const isCompactCategoryLayout = useMediaQuery("(max-width: 1500px)");
  const monthLabel = formatMonthLabel(selectedMonth, languageCode);
  const currencyCode = profile?.preferredCurrencyCode ?? "USD";
  const [preferences, setPreferences] = useState(readCategoryPreferences);
  const [query, setQuery] = useState("");
  const [activeFilter, setActiveFilter] = useState<CategoryFilter>("all");
  const [selectedCategoryId, setSelectedCategoryId] = useState<string | null>(null);
  const [name, setName] = useState("");
  const [kind, setKind] = useState<CategoryKind>("Expense");
  const [color, setColor] = useState(colorSwatches[0]);
  const [group, setGroup] = useState<CategoryGroupId>("daily");
  const [iconKey, setIconKey] = useState<CategoryIconKey>("basket");
  const [saving, setSaving] = useState(false);
  const [formError, setFormError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [compactEditorOpen, setCompactEditorOpen] = useState(false);

  useEffect(() => {
    if (selectedCategoryId && !categories.some((category) => category.id === selectedCategoryId)) {
      setSelectedCategoryId(null);
      setCompactEditorOpen(false);
    }
  }, [categories, selectedCategoryId]);

  const daysInMonth = useMemo(() => getDaysInSelectedMonth(selectedMonth), [selectedMonth]);
  const monthlyTransactions = useMemo(
    () => transactions.filter((transaction) => transaction.occurredOnUtc.slice(0, 7) === selectedMonth),
    [selectedMonth, transactions]
  );
  const rulesByCategory = useMemo(() => {
    const rules = new Map<string, ImportRule[]>();
    importRules.forEach((rule) => {
      const current = rules.get(rule.assignCategoryId) ?? [];
      current.push(rule);
      rules.set(rule.assignCategoryId, current);
    });
    return rules;
  }, [importRules]);
  const budgetByCategory = useMemo(
    () => new Map((budget?.categories ?? []).map((category) => [category.categoryId, category])),
    [budget?.categories]
  );
  const totalsByKind = useMemo(() => {
    return monthlyTransactions.reduce(
      (totals, transaction) => {
        if (transaction.type === "Income" && !transaction.parentTransactionId) {
          totals.Income += Math.abs(transaction.amount);
        }

        if ((transaction.type === "Expense" || transaction.type === "Refund") && !transaction.parentTransactionId) {
          totals.Expense += (transaction.type === "Refund" ? -1 : 1) * Math.abs(transaction.amount);
        }

        return totals;
      },
      { Expense: 0, Income: 0 }
    );
  }, [monthlyTransactions]);

  const categoryRows = useMemo<CategoryRow[]>(() => {
    return categories.map((category, index) => {
      const safeKind = getSafeKind(category.kind);
      const categoryMonthlyTransactions = monthlyTransactions.filter((transaction) => transaction.categoryId === category.id && (transaction.parentTransactionId || !transaction.splitGroupId));
      const categoryAllTransactions = transactions.filter((transaction) => transaction.categoryId === category.id && (transaction.parentTransactionId || !transaction.splitGroupId));
      const budgetCategory = budgetByCategory.get(category.id);
      const amount = categoryMonthlyTransactions.reduce((sum, transaction) => sum + (transaction.type === "Refund" ? -1 : 1) * Math.abs(transaction.amount), 0);
      const monthlyAmount = safeKind === "Expense" && budgetCategory ? Math.max(budgetCategory.spent, amount) : amount;
      const ruleCount = rulesByCategory.get(category.id)?.length ?? 0;
      const groupId = preferences.groups[category.id] ?? getCategoryGroup(category, languageCode);
      const resolvedIconKey = preferences.icons[category.id] ?? getDefaultCategoryIcon(category);
      const totalForKind = totalsByKind[safeKind] || 0;
      const attentionReasons = [
        categoryAllTransactions.length === 0 ? "unused" : null,
        ruleCount === 0 && safeKind === "Expense" ? "without-rules" : null,
        category.color ? null : "without-color"
      ].filter(Boolean) as string[];

      return {
        category,
        kind: safeKind,
        color: category.color ?? fallbackPalette[index % fallbackPalette.length],
        groupId,
        iconKey: resolvedIconKey,
        amount: monthlyAmount,
        share: totalForKind > 0 ? Math.round((monthlyAmount / totalForKind) * 100) : 0,
        monthlyOperations: categoryMonthlyTransactions.length,
        allOperations: categoryAllTransactions.length,
        ruleCount,
        lastActivityUtc: getLastActivity(categoryAllTransactions),
        sparklineValues: buildSparklineValues(categoryMonthlyTransactions, daysInMonth),
        attentionReasons,
        canArchive: !category.isSystem && categoryAllTransactions.length === 0 && ruleCount === 0
      };
    }).sort((first, second) => {
      const groupDelta = groupOrder.indexOf(first.groupId) - groupOrder.indexOf(second.groupId);
      if (groupDelta !== 0) {
        return groupDelta;
      }

      return second.amount - first.amount || first.category.name.localeCompare(second.category.name);
    });
  }, [budgetByCategory, categories, daysInMonth, languageCode, monthlyTransactions, preferences.groups, preferences.icons, rulesByCategory, totalsByKind, transactions]);

  const selectedCategory = useMemo(
    () => categories.find((category) => category.id === selectedCategoryId) ?? null,
    [categories, selectedCategoryId]
  );

  const normalizedQuery = normalizeSearchText(query.trim());
  const visibleRows = useMemo(
    () => categoryRows.filter((row) => {
      const matchesQuery = normalizedQuery.length === 0 ||
        normalizeSearchText(row.category.name).includes(normalizedQuery) ||
        normalizeSearchText(copy.groupLabels[row.groupId]).includes(normalizedQuery);
      const matchesFilter = activeFilter === "all" ||
        (activeFilter === "expense" && row.kind === "Expense") ||
        (activeFilter === "income" && row.kind === "Income") ||
        (activeFilter === "attention" && row.attentionReasons.length > 0);

      return matchesQuery && matchesFilter;
    }),
    [activeFilter, categoryRows, copy.groupLabels, normalizedQuery]
  );
  const groupedRows = useMemo(
    () => groupOrder
      .map((groupId) => ({
        id: groupId,
        rows: visibleRows.filter((row) => row.groupId === groupId)
      }))
      .filter((item) => item.rows.length > 0),
    [visibleRows]
  );

  const expenseCount = categoryRows.filter((row) => row.kind === "Expense").length;
  const incomeCount = categoryRows.filter((row) => row.kind === "Income").length;
  const expenseRatio = categoryRows.length > 0 ? Math.round((expenseCount / categoryRows.length) * 100) : 0;
  const usedGroupCount = new Set(categoryRows.map((row) => row.groupId)).size;
  const unusedCount = categoryRows.filter((row) => row.allOperations === 0).length;
  const withoutRulesCount = categoryRows.filter((row) => row.kind === "Expense" && row.ruleCount === 0).length;
  const attentionCount = categoryRows.filter((row) => row.attentionReasons.length > 0).length;
  const mostUsedRow = [...categoryRows].sort((first, second) => second.monthlyOperations - first.monthlyOperations || second.amount - first.amount)[0];
  const archivableRows = categoryRows.filter((row) => row.canArchive);
  const similarPair = findSimilarCategoryPair(categoryRows);
  const firstRuleCandidate = categoryRows.find((row) => row.kind === "Expense" && row.ruleCount === 0 && row.allOperations > 0);
  const PreviewIcon = getIconByKey(iconKey);
  const formGroupOptions = kind === "Income" ? (["income"] as CategoryGroupId[]) : (["fixed", "daily", "lifestyle"] as CategoryGroupId[]);

  const updatePreferences = (categoryId: string, nextGroup: CategoryGroupId, nextIconKey: CategoryIconKey) => {
    setPreferences((current) => {
      const next = {
        groups: { ...current.groups, [categoryId]: nextGroup },
        icons: { ...current.icons, [categoryId]: nextIconKey }
      };
      writeCategoryPreferences(next);
      return next;
    });
  };

  const removePreferences = (categoryId: string) => {
    setPreferences((current) => {
      const next = {
        groups: { ...current.groups },
        icons: { ...current.icons }
      };
      delete next.groups[categoryId];
      delete next.icons[categoryId];
      writeCategoryPreferences(next);
      return next;
    });
  };

  const resetCategoryForm = () => {
    setSelectedCategoryId(null);
    setName("");
    setKind("Expense");
    setColor(colorSwatches[0]);
    setGroup("daily");
    setIconKey("basket");
    setFormError(null);
    setNotice(null);
  };

  const startNewCategory = () => {
    resetCategoryForm();
    setCompactEditorOpen(true);
  };

  const selectCategory = (row: CategoryRow) => {
    setSelectedCategoryId(row.category.id);
    setName(row.category.name);
    setKind(row.kind);
    setColor(row.color);
    setGroup(row.groupId);
    setIconKey(row.iconKey);
    setFormError(null);
    setNotice(null);
    setCompactEditorOpen(true);
  };

  const duplicateCategory = (row: CategoryRow) => {
    setSelectedCategoryId(null);
    setName(`${row.category.name} copy`);
    setKind(row.kind);
    setColor(row.color);
    setGroup(row.kind === "Income" ? "income" : row.groupId);
    setIconKey(row.iconKey);
    setFormError(null);
    setNotice(null);
    setCompactEditorOpen(true);
  };

  const handleKindChange = (nextKind: CategoryKind) => {
    setKind(nextKind);
    if (nextKind === "Income") {
      setGroup("income");
      setIconKey((current) => current === "basket" || current === "dining" ? "income" : current);
    } else if (group === "income") {
      setGroup("daily");
      setIconKey((current) => current === "income" || current === "freelance" ? "basket" : current);
    }
  };

  const handleSubmit = async (event: FormEvent) => {
    event.preventDefault();
    if (!auth?.accessToken || saving) {
      return;
    }

    const trimmedName = name.trim();
    if (!trimmedName) {
      return;
    }

    setSaving(true);
    setFormError(null);
    setNotice(null);

    try {
      const savedCategory = selectedCategory
        ? await apiClient.updateCategory(auth.accessToken, {
            id: selectedCategory.id,
            name: trimmedName,
            kind,
            color,
            isSystem: selectedCategory.isSystem
          })
        : await apiClient.createCategory(auth.accessToken, { name: trimmedName, kind, color });

      updatePreferences(savedCategory.id, kind === "Income" ? "income" : group, iconKey);
      setSelectedCategoryId(savedCategory.id);
      setName(savedCategory.name);
      setKind(getSafeKind(savedCategory.kind));
      setColor(savedCategory.color ?? color);
      setNotice(selectedCategory ? copy.updated : copy.created);
      await refresh();
      setCompactEditorOpen(false);
    } catch {
      setFormError(copy.unableToSave);
    } finally {
      setSaving(false);
    }
  };

  const archiveCategory = async (row: CategoryRow) => {
    if (!auth?.accessToken || !row.canArchive) {
      return;
    }

    setSaving(true);
    setFormError(null);
    setNotice(null);

    try {
      await apiClient.deleteCategory(auth.accessToken, row.category.id);
      removePreferences(row.category.id);
      if (selectedCategoryId === row.category.id) {
        resetCategoryForm();
        setCompactEditorOpen(false);
      }
      setNotice(copy.archived);
      await refresh();
    } catch {
      setFormError(copy.unableToArchive);
    } finally {
      setSaving(false);
    }
  };

  const exportCategories = () => {
    const header = ["Name", "Kind", "Group", "Monthly amount", "Monthly operations", "Rules", "System"];
    const rows = categoryRows.map((row) => [
      row.category.name,
      row.kind,
      copy.groupLabels[row.groupId],
      row.amount.toFixed(2),
      row.monthlyOperations,
      row.ruleCount,
      row.category.isSystem
    ]);
    const csv = [header, ...rows].map((row) => row.map(escapeCsv).join(",")).join("\n");
    const blob = new Blob([csv], { type: "text/csv;charset=utf-8" });
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = `ledgerra-categories-${selectedMonth}.csv`;
    link.click();
    URL.revokeObjectURL(url);
    setNotice(copy.exported);
  };

  const renderCategoryEditorForm = (placement: "side" | "inline") => {
    const isInline = placement === "inline";
    const formLabel = isInline
      ? selectedCategory
        ? copy.editInline(selectedCategory.name)
        : copy.newInline
      : copy.categoryEditor;

    return (
      <form className={`category-editor-form ${isInline ? "is-inline" : ""}`} aria-label={formLabel} onSubmit={handleSubmit}>
        <div className="category-preview" style={{ "--category-color": color } as CSSProperties}>
          <span className="category-preview-icon">
            <PreviewIcon />
          </span>
          <div>
            <strong>{name.trim() || copy.previewName}</strong>
            <span>{copy.groupLabels[group]} · {getCategoryKindLabel(kind, t)}</span>
          </div>
          <em>{kind === "Expense" ? "-" : "+"} {getCategoryKindLabel(kind, t).toLocaleUpperCase()}</em>
        </div>

        <label>
          {copy.name}
          <input value={name} onChange={(event) => setName(event.target.value)} placeholder={copy.namePlaceholder} required />
        </label>

        <label>
          {copy.group}
          <select value={group} onChange={(event) => setGroup(event.target.value as CategoryGroupId)}>
            {formGroupOptions.map((groupId) => (
              <option key={groupId} value={groupId}>
                {copy.groupLabels[groupId]}
              </option>
            ))}
          </select>
        </label>

        <div className="category-form-field">
          <span>{copy.type}</span>
          <div className="category-type-options">
            <button className={kind === "Expense" ? "is-active" : undefined} type="button" onClick={() => handleKindChange("Expense")}>
              <ExpenseIcon />
              {getCategoryKindLabel("Expense", t)}
            </button>
            <button className={kind === "Income" ? "is-active" : undefined} type="button" onClick={() => handleKindChange("Income")}>
              <IncomeIcon />
              {getCategoryKindLabel("Income", t)}
            </button>
          </div>
        </div>

        <div className="category-form-field">
          <span>{copy.color}</span>
          <div className="category-color-grid" role="radiogroup" aria-label={copy.color}>
            {colorSwatches.map((swatch) => (
              <button
                aria-label={swatch}
                className={color === swatch ? "is-active" : undefined}
                key={swatch}
                type="button"
                style={{ "--category-color": swatch } as CSSProperties}
                onClick={() => setColor(swatch)}
              />
            ))}
          </div>
        </div>

        <div className="category-form-field">
          <span>{copy.icon}</span>
          <div className="category-icon-grid">
            {categoryIconOptions.map((option) => (
              <button
                className={iconKey === option.id ? "is-active" : undefined}
                key={option.id}
                type="button"
                title={option.label}
                onClick={() => setIconKey(option.id)}
              >
                <option.Icon />
              </button>
            ))}
          </div>
        </div>

        <div className="category-editor-actions">
          {isInline ? (
            <button className="ghost-button" type="button" onClick={() => setCompactEditorOpen(false)}>
              {copy.close}
            </button>
          ) : (
            <button className="ghost-button" type="button" onClick={() => firstRuleCandidate && selectCategory(firstRuleCandidate)} disabled={!firstRuleCandidate}>
              <BoltIcon />
              {copy.addRule}
            </button>
          )}
          <button className="primary-button" type="submit" disabled={saving || !name.trim()}>
            {saving ? copy.saving : selectedCategory ? copy.updateCategory : copy.saveCategory}
          </button>
        </div>
      </form>
    );
  };

  return (
    <div className="page-stack category-page">
      <div className="category-breadcrumb">{copy.breadcrumb(monthLabel).toLocaleUpperCase()}</div>
      <PageHeader
        eyebrow={t("categories.eyebrow")}
        title={t("categories.title")}
        description={t("categories.description")}
        actions={(
          <div className="category-page-actions">
            <ActionMenu label={copy.actionMenuLabel} panelClassName="category-page-action-menu">
              <button className="action-menu-item" type="button" onClick={exportCategories} disabled={categoryRows.length === 0}>
                <DownloadIcon />
                {copy.export}
              </button>
              <button className="action-menu-item" type="button" onClick={() => setActiveFilter("attention")}>
                <SparklesIcon />
                {copy.suggestions}
              </button>
            </ActionMenu>
            <button className="primary-button" type="button" onClick={startNewCategory}>
              <PlusIcon />
              {copy.newCategory}
            </button>
          </div>
        )}
      />

      {auth?.accessToken && <CategoryOrganization token={auth.accessToken} categories={categories} onChanged={refresh} />}

      <section className="category-overview-strip" aria-label={copy.totalCategories}>
        <div className="category-overview-cell">
          <span>{copy.totalCategories}</span>
          <strong>{categoryRows.length}</strong>
          <p>{copy.inGroups(usedGroupCount)}</p>
        </div>
        <div className="category-overview-cell category-overview-ratio">
          <span>{copy.expenseVsIncome}</span>
          <div>
            <strong>{expenseCount}</strong>
            <small>{copy.expenseShort}</small>
            <b>{incomeCount}</b>
            <small>{copy.incomeShort}</small>
          </div>
          <div className="category-ratio-track" aria-hidden="true">
            <i style={{ width: `${expenseRatio}%` }} />
          </div>
        </div>
        <div className="category-overview-cell category-overview-featured">
          <span>{copy.mostUsed}</span>
          {mostUsedRow ? (
            <div>
              <div className="category-featured-icon" style={{ "--category-color": mostUsedRow.color } as CSSProperties}>
                {(() => {
                  const Icon = getIconByKey(mostUsedRow.iconKey);
                  return <Icon />;
                })()}
              </div>
              <p>
                <b>{mostUsedRow.category.name}</b>
                {copy.operations(mostUsedRow.monthlyOperations)} · {formatCurrency(mostUsedRow.amount, currencyCode, languageCode)}
              </p>
            </div>
          ) : (
            <p>{copy.noActivity}</p>
          )}
        </div>
        <div className="category-overview-cell">
          <span>{copy.needsReview}</span>
          <strong className={attentionCount > 0 ? "is-warning" : undefined}>{attentionCount}</strong>
          <p>{copy.reviewSummary(unusedCount, withoutRulesCount)}</p>
        </div>
      </section>

      {(error || formError || notice) ? (
        <div className={`category-notice ${error || formError ? "is-error" : "is-success"}`}>
          {error || formError || notice}
        </div>
      ) : null}

      <div className="category-workspace">
        <section className="category-list-panel">
          <div className="category-list-heading">
            <div>
              <h2>{copy.yourCategories}</h2>
            </div>
            <label className="category-search">
              <SearchIcon />
              <span className="sr-only">{copy.searchPlaceholder}</span>
              <input value={query} onChange={(event) => setQuery(event.target.value)} placeholder={copy.searchPlaceholder} />
            </label>
            <div className="category-filter-tabs" aria-label={copy.yourCategories}>
              {(["all", "expense", "income", "attention"] as CategoryFilter[]).map((filter) => (
                <button
                  key={filter}
                  className={activeFilter === filter ? "is-active" : undefined}
                  type="button"
                  onClick={() => setActiveFilter(filter)}
                >
                  <span>{copy.filters[filter]}</span>
                  <small>
                    {filter === "all"
                      ? categoryRows.length
                      : filter === "expense"
                        ? expenseCount
                        : filter === "income"
                          ? incomeCount
                          : attentionCount}
                  </small>
                </button>
              ))}
            </div>
          </div>

          {isCompactCategoryLayout && compactEditorOpen && !selectedCategory ? (
            <section className="category-inline-editor category-inline-editor-create">
              <div className="category-side-heading">
                <h2>{copy.newCategory}</h2>
                <button className="category-link-button" type="button" onClick={() => setCompactEditorOpen(false)}>
                  {copy.close}
                </button>
              </div>
              {renderCategoryEditorForm("inline")}
            </section>
          ) : null}

          {loading ? (
            <div className="category-empty-state">{t("common.loading")}</div>
          ) : groupedRows.length === 0 ? (
            <div className="category-empty-state">
              <strong>{copy.emptyTitle}</strong>
              <p>{copy.emptyBody}</p>
            </div>
          ) : (
            <div className="category-groups">
              {groupedRows.map((groupItem) => {
                const groupAmount = groupItem.rows.reduce((sum, row) => sum + row.amount, 0);
                return (
                  <section className="category-group" key={groupItem.id}>
                    <div className="category-group-heading">
                      <div>
                        <span>
                          <CategoryIcon />
                          {copy.groupLabels[groupItem.id]}
                        </span>
                        <p>{copy.groupDescriptions[groupItem.id]}</p>
                      </div>
                      <strong>{groupItem.rows.length} · {formatCurrency(groupAmount, currencyCode, languageCode)}</strong>
                    </div>

                    <div className="category-manager-list">
                      {groupItem.rows.map((row) => {
                        const Icon = getIconByKey(row.iconKey);
                        return (
                          <article
                            className={`category-manager-row ${selectedCategoryId === row.category.id ? "is-selected" : ""}`}
                            key={row.category.id}
                            style={{ "--category-color": row.color } as CSSProperties}
                          >
                            <span className="category-row-grip" aria-hidden="true">
                              <GripIcon />
                            </span>
                            <button className="category-row-identity" type="button" onClick={() => selectCategory(row)}>
                              <span className="category-row-icon">
                                <Icon />
                              </span>
                              <span>
                                <strong>{row.category.name}</strong>
                                <small>
                                  {getCategoryKindLabel(row.kind, t)} · {row.category.isSystem ? copy.system : copy.custom} · {row.ruleCount > 0 ? copy.rules(row.ruleCount) : copy.noRules}
                                </small>
                              </span>
                            </button>
                            <div className="category-row-sparkline" aria-hidden="true">
                              <svg viewBox="0 0 116 34" preserveAspectRatio="none">
                                <path d={buildSparklinePath(row.sparklineValues)} />
                              </svg>
                              <span>{row.share}%</span>
                            </div>
                            <div className="category-row-amount">
                              <strong>{formatCurrency(row.amount, currencyCode, languageCode)}</strong>
                              <span>
                                {copy.operations(row.monthlyOperations)} · {formatActivityLabel(row.lastActivityUtc, languageCode, copy)}
                              </span>
                            </div>
                            <div className="category-row-actions">
                              <ActionMenu label={copy.rowActionMenuLabel(row.category.name)} panelClassName="category-row-action-menu">
                                <button className="action-menu-item" type="button" aria-label={`${copy.edit} ${row.category.name}`} onClick={() => selectCategory(row)}>
                                  <EditIcon />
                                  {copy.edit}
                                </button>
                                <button className="action-menu-item" type="button" aria-label={`${copy.duplicate} ${row.category.name}`} onClick={() => duplicateCategory(row)}>
                                  <DuplicateIcon />
                                  {copy.duplicate}
                                </button>
                                <button
                                  className="action-menu-item"
                                  type="button"
                                  aria-label={row.canArchive ? `${copy.archive} ${row.category.name}` : copy.archiveUnavailableFor(row.category.name)}
                                  onClick={() => archiveCategory(row)}
                                  disabled={!row.canArchive || saving}
                                >
                                  <ArchiveIcon />
                                  {row.canArchive ? copy.archive : copy.archiveUnavailable}
                                </button>
                              </ActionMenu>
                            </div>
                            {isCompactCategoryLayout && compactEditorOpen && selectedCategoryId === row.category.id ? (
                              <div className="category-inline-editor">
                                <div className="category-side-heading">
                                  <h2>{copy.edit}</h2>
                                  <button className="category-link-button" type="button" onClick={() => setCompactEditorOpen(false)}>
                                    {copy.close}
                                  </button>
                                </div>
                                {renderCategoryEditorForm("inline")}
                              </div>
                            ) : null}
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

        <aside className="category-side-stack">
          {!isCompactCategoryLayout ? (
            <section className="category-editor-panel" aria-label={copy.categoryEditor}>
              <div className="category-side-heading">
                <h2>{selectedCategory ? copy.edit : copy.newCategory}</h2>
                <button className="category-link-button" type="button" onClick={startNewCategory}>
                  {copy.newCategory}
                </button>
              </div>

              {renderCategoryEditorForm("side")}
            </section>
          ) : null}

          <section className="category-suggestions-panel">
            <div className="category-side-heading">
              <h2>{copy.systemSuggestions}</h2>
              <span>{attentionCount}</span>
            </div>
            <div className="category-suggestions-list">
              {similarPair ? (
                <article>
                  <SparklesIcon />
                  <div>
                    <strong>{copy.mergeTitle(similarPair[0].category.name, similarPair[1].category.name)}</strong>
                    <p>{copy.mergeBody}</p>
                  </div>
                  <button type="button" onClick={() => setQuery(similarPair[0].category.name)}>{copy.review}</button>
                </article>
              ) : null}
              {firstRuleCandidate ? (
                <article>
                  <BoltIcon />
                  <div>
                    <strong>{copy.ruleTitle(firstRuleCandidate.category.name)}</strong>
                    <p>{copy.ruleBody}</p>
                  </div>
                  <button type="button" onClick={() => selectCategory(firstRuleCandidate)}>{copy.review}</button>
                </article>
              ) : null}
              {archivableRows.length > 0 ? (
                <article>
                  <ArchiveIcon />
                  <div>
                    <strong>{copy.archiveTitle(archivableRows.length)}</strong>
                    <p>{copy.archiveBody}</p>
                  </div>
                  <button type="button" onClick={() => archiveCategory(archivableRows[0])} disabled={saving}>{copy.archive}</button>
                </article>
              ) : null}
              {archivableRows.length === 0 && unusedCount > 0 ? (
                <article>
                  <ArchiveIcon />
                  <div>
                    <strong>{copy.unusedTitle(unusedCount)}</strong>
                    <p>{copy.unusedBody}</p>
                  </div>
                  <button type="button" onClick={() => setActiveFilter("attention")}>{copy.review}</button>
                </article>
              ) : null}
              {!similarPair && !firstRuleCandidate && archivableRows.length === 0 && unusedCount === 0 ? (
                <div className="category-empty-state">
                  <strong>{copy.emptyTitle}</strong>
                  <p>{copy.emptyBody}</p>
                </div>
              ) : null}
            </div>
          </section>
        </aside>
      </div>
    </div>
  );
}
