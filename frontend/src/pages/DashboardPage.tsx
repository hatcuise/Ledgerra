import { SpendingInsightSummary } from "../components/SpendingInsightSummary";
import { useCallback, useEffect, useMemo, useState } from "react";
import { Link } from "react-router-dom";
import { useMonthSelection } from "../state/MonthContext";
import { TransactionForm } from "../components/TransactionForm";
import { useLedgerraData } from "../hooks/useLedgerraData";
import { useAuth } from "../state/AuthContext";
import { useI18n } from "../state/I18nContext";
import { PageHeader } from "../ui/PageHeader";
import { MetricCard } from "../ui/MetricCard";
import { SectionCard } from "../ui/SectionCard";
import { EmptyState } from "../ui/EmptyState";
import { formatCurrency } from "../utils/format";
import { getMonthRangeParams } from "../utils/monthRange";
import type { BudgetSummary, Transaction } from "../types";

type ChecklistAction =
  | { kind: "button"; label: string; onClick: () => void }
  | { kind: "link"; label: string; to: string }
  | { kind: "done"; label: string };

type ChecklistItem = {
  title: string;
  detail: string;
  complete: boolean;
  actions: ChecklistAction[];
};

type DashboardInsight = {
  id: string;
  title: string;
  detail: string;
  tone: "attention" | "warning" | "neutral";
  action?: {
    label: string;
    to: string;
  };
};

type OnboardingAcknowledgements = {
  currency: boolean;
  categories: boolean;
};
type DashboardWidgetId = "metrics" | "trends" | "insights" | "topCategories" | "accountBalances";
type DashboardWidgetPreference = { id: DashboardWidgetId; visible: boolean };

const acknowledgementDefaults: OnboardingAcknowledgements = {
  currency: false,
  categories: false
};
const dashboardWidgetDefaults: DashboardWidgetPreference[] = [
  { id: "metrics", visible: true },
  { id: "trends", visible: true },
  { id: "insights", visible: true },
  { id: "topCategories", visible: true },
  { id: "accountBalances", visible: true }
];

function normalizeDashboardWidgetPreferences(stored: string | null): DashboardWidgetPreference[] {
  const defaultsById = new Map(dashboardWidgetDefaults.map((widget) => [widget.id, widget]));
  const copyDefaults = () => dashboardWidgetDefaults.map((widget) => ({ ...widget }));

  if (!stored) {
    return copyDefaults();
  }

  try {
    const parsed = JSON.parse(stored) as Array<Partial<DashboardWidgetPreference>>;
    if (!Array.isArray(parsed)) {
      return copyDefaults();
    }

    const seen = new Set<DashboardWidgetId>();
    const storedPreferences = parsed.flatMap((item) => {
      if (!item || typeof item !== "object") {
        return [];
      }

      const defaultWidget = defaultsById.get(item.id as DashboardWidgetId);
      if (!defaultWidget || seen.has(defaultWidget.id)) {
        return [];
      }

      seen.add(defaultWidget.id);
      return [{ ...defaultWidget, visible: typeof item.visible === "boolean" ? item.visible : defaultWidget.visible }];
    });
    const missingDefaults = dashboardWidgetDefaults
      .filter((widget) => !seen.has(widget.id))
      .map((widget) => ({ ...widget }));

    return [...storedPreferences, ...missingDefaults];
  } catch {
    return copyDefaults();
  }
}

function readAcknowledgements(storageKey: string, legacyStorageKey?: string): OnboardingAcknowledgements {
  try {
    const stored = window.localStorage.getItem(storageKey) ?? (legacyStorageKey ? window.localStorage.getItem(legacyStorageKey) : null);
    return stored ? { ...acknowledgementDefaults, ...JSON.parse(stored) } : acknowledgementDefaults;
  } catch {
    return acknowledgementDefaults;
  }
}

function describeSpendingDelta(amount: number, currencyCode: string, t: ReturnType<typeof useI18n>["t"]) {
  if (amount === 0) {
    return t("dashboard.spendingFlat");
  }

  return t("dashboard.spendingDelta", {
    direction: amount > 0 ? t("common.up") : t("common.down"),
    amount: formatCurrency(Math.abs(amount), currencyCode)
  });
}

function MiniSparkline({ points, ariaLabel }: { points: Array<{ month: string; amount: number }>; ariaLabel: string }) {
  if (points.length === 0) {
    return null;
  }

  const width = 180;
  const height = 56;
  const max = Math.max(...points.map((point) => point.amount), 1);
  const step = points.length === 1 ? width : width / (points.length - 1);
  const path = points
    .map((point, index) => {
      const x = index * step;
      const y = height - (point.amount / max) * height;
      return `${index === 0 ? "M" : "L"} ${x.toFixed(2)} ${y.toFixed(2)}`;
    })
    .join(" ");

  return (
    <svg className="mini-sparkline" viewBox={`0 0 ${width} ${height}`} role="img" aria-label={ariaLabel}>
      <path d={path} />
    </svg>
  );
}

function buildDashboardInsights(
  budget: BudgetSummary | null,
  transactions: Transaction[],
  currencyCode: string,
  monthRangeParams: string,
  t: ReturnType<typeof useI18n>["t"]
): DashboardInsight[] {
  const insights: DashboardInsight[] = [];
  const budgetCategories = budget?.categories ?? [];
  const hasBudget = (budget?.totalPlanned ?? 0) > 0 || budgetCategories.some((category) => category.planned > 0);
  const expenseTransactions = transactions.filter((transaction) => (transaction.type === "Expense" || transaction.type === "Refund") && !transaction.parentTransactionId && !transaction.splitGroupId);
  const uncategorizedExpenseCount = expenseTransactions.filter((transaction) => !transaction.categoryId).length;

  if (uncategorizedExpenseCount > 0) {
    insights.push({
      id: "uncategorized-expenses",
      title: t("dashboard.categorizeRecentSpendingTitle"),
      detail: t("dashboard.categorizeRecentSpendingDetail", { count: uncategorizedExpenseCount }),
      tone: "attention",
      action: { label: t("dashboard.reviewTransactions"), to: `/transactions?view=uncategorized&${monthRangeParams}` }
    });
  }

  if (!hasBudget && transactions.length > 0) {
    insights.push({
      id: "set-budget",
      title: t("dashboard.addBudgetGuardrailsTitle"),
      detail: t("dashboard.addBudgetGuardrailsDetail"),
      tone: "neutral",
      action: { label: t("dashboard.openBudgets"), to: "/budgets" }
    });
  }

  return insights.slice(0, 4);
}

export function DashboardPage() {
  const { auth } = useAuth();
  const { t } = useI18n();
  const { selectedMonth } = useMonthSelection();
  const { accounts, categories, dashboard, budget, loading, error, profile, transactions, refresh } = useLedgerraData({
    accounts: true,
    categories: true,
    dashboard: true,
    budget: true,
    profile: true,
    transactions: true
  });
  const mainCurrencyCode = dashboard?.currencyCode ?? profile?.preferredCurrencyCode ?? "USD";
  const acknowledgementStorageKey = `ledgerra:onboarding:${profile?.email ?? "anonymous"}`;
  const checklistDismissalStorageKey = `ledgerra:onboarding-dismissed:${profile?.email ?? "anonymous"}`;
  const widgetPreferenceStorageKey = `ledgerra:dashboard-widgets:${profile?.email ?? "anonymous"}`;
  const widgetCustomizationStorageKey = `ledgerra:dashboard-widget-customization-closed:${profile?.email ?? "anonymous"}`;
  const legacyAcknowledgementStorageKey = `${acknowledgementStorageKey}:${mainCurrencyCode}`;
  const [acknowledgements, setAcknowledgements] = useState(() =>
    readAcknowledgements(acknowledgementStorageKey, legacyAcknowledgementStorageKey)
  );
  const [isChecklistDismissed, setIsChecklistDismissed] = useState(() => window.localStorage.getItem(checklistDismissalStorageKey) === "true");
  const [isWidgetCustomizationClosed, setIsWidgetCustomizationClosed] = useState(() => window.localStorage.getItem(widgetCustomizationStorageKey) === "true");
  const [isQuickAddOpen, setIsQuickAddOpen] = useState(false);
  const [widgetPreferences, setWidgetPreferences] = useState<DashboardWidgetPreference[]>(() =>
    normalizeDashboardWidgetPreferences(window.localStorage.getItem(widgetPreferenceStorageKey))
  );
  const [quickAddError, setQuickAddError] = useState("");
  const [quickAddStatus, setQuickAddStatus] = useState("");
  const hasBudget = (budget?.totalPlanned ?? 0) > 0 || Boolean(budget?.categories.some((category) => category.planned > 0));
  const monthRangeParams = getMonthRangeParams(selectedMonth);

  const markAcknowledgement = useCallback((key: keyof typeof acknowledgementDefaults) => {
    setAcknowledgements((current) => {
      const next = { ...current, [key]: true };
      window.localStorage.setItem(acknowledgementStorageKey, JSON.stringify(next));
      return next;
    });
  }, [acknowledgementStorageKey]);
  const dismissChecklist = useCallback(() => {
    window.localStorage.setItem(checklistDismissalStorageKey, "true");
    setIsChecklistDismissed(true);
  }, [checklistDismissalStorageKey]);
  const setWidgetCustomizationClosed = useCallback((closed: boolean) => {
    window.localStorage.setItem(widgetCustomizationStorageKey, String(closed));
    setIsWidgetCustomizationClosed(closed);
  }, [widgetCustomizationStorageKey]);
  const updateWidgetPreferences = useCallback((updater: (current: DashboardWidgetPreference[]) => DashboardWidgetPreference[]) => {
    setWidgetPreferences((current) => {
      const next = updater(current);
      window.localStorage.setItem(widgetPreferenceStorageKey, JSON.stringify(next));
      return next;
    });
  }, [widgetPreferenceStorageKey]);
  const toggleWidgetVisibility = useCallback((id: DashboardWidgetId) => {
    updateWidgetPreferences((current) => current.map((widget) => (widget.id === id ? { ...widget, visible: !widget.visible } : widget)));
  }, [updateWidgetPreferences]);
  const moveWidget = useCallback((id: DashboardWidgetId, direction: "up" | "down") => {
    updateWidgetPreferences((current) => {
      const index = current.findIndex((widget) => widget.id === id);
      const nextIndex = direction === "up" ? index - 1 : index + 1;
      if (index < 0 || nextIndex < 0 || nextIndex >= current.length) {
        return current;
      }
      const next = [...current];
      const [item] = next.splice(index, 1);
      next.splice(nextIndex, 0, item);
      return next;
    });
  }, [updateWidgetPreferences]);

  const checklistItems: ChecklistItem[] = useMemo(
    () => [
      {
        title: t("dashboard.checklistCreateFirstAccount"),
        detail: accounts[0] ? accounts[0].name : t("dashboard.checklistCreateFirstAccountDetail"),
        complete: accounts.length > 0,
        actions: accounts.length > 0 ? [{ kind: "done", label: t("dashboard.accountReady") }] : [{ kind: "link", label: t("dashboard.addAccount"), to: "/accounts" }]
      },
      {
        title: t("dashboard.checklistConfirmCurrency"),
        detail: t("dashboard.checklistConfirmCurrencyDetail", { currencyCode: mainCurrencyCode }),
        complete: acknowledgements.currency,
        actions: acknowledgements.currency
          ? [{ kind: "done", label: t("dashboard.currencyConfirmed") }]
          : [{ kind: "button", label: t("dashboard.confirmCurrency", { currencyCode: mainCurrencyCode }), onClick: () => markAcknowledgement("currency") }]
      },
      {
        title: t("dashboard.checklistReviewCategories"),
        detail: t("dashboard.checklistReviewCategoriesDetail"),
        complete: acknowledgements.categories,
        actions: acknowledgements.categories
          ? [{ kind: "done", label: t("dashboard.categoriesReviewed") }]
          : [
              { kind: "link", label: t("dashboard.openCategories"), to: "/categories" },
              { kind: "button", label: t("dashboard.reviewed"), onClick: () => markAcknowledgement("categories") }
            ]
      },
      {
        title: t("dashboard.checklistSetBudget"),
        detail: t("dashboard.checklistSetBudgetDetail"),
        complete: hasBudget,
        actions: hasBudget ? [{ kind: "done", label: t("dashboard.budgetSet") }] : [{ kind: "link", label: t("dashboard.setBudget"), to: "/budgets" }]
      },
      {
        title: t("dashboard.checklistAddTransaction"),
        detail: t("dashboard.checklistAddTransactionDetail"),
        complete: transactions.length > 0,
        actions: transactions.length > 0
          ? [{ kind: "done", label: t("dashboard.transactionAdded") }]
          : [{ kind: "link", label: t("dashboard.addFirstTransaction"), to: "/transactions" }]
      }
    ],
    [accounts, acknowledgements, hasBudget, mainCurrencyCode, markAcknowledgement, t, transactions.length]
  );
  const completedChecklistItems = checklistItems.filter((item) => item.complete).length;
  const isChecklistComplete = completedChecklistItems === checklistItems.length;
  const insights = useMemo(
    () => buildDashboardInsights(budget, transactions, mainCurrencyCode, monthRangeParams, t),
    [budget, mainCurrencyCode, monthRangeParams, t, transactions]
  );

  useEffect(() => {
    const nextAcknowledgements = readAcknowledgements(acknowledgementStorageKey, legacyAcknowledgementStorageKey);
    setAcknowledgements(nextAcknowledgements);
    window.localStorage.setItem(acknowledgementStorageKey, JSON.stringify(nextAcknowledgements));
  }, [acknowledgementStorageKey, legacyAcknowledgementStorageKey]);
  useEffect(() => {
    setIsChecklistDismissed(window.localStorage.getItem(checklistDismissalStorageKey) === "true");
  }, [checklistDismissalStorageKey]);
  useEffect(() => {
    setWidgetPreferences(normalizeDashboardWidgetPreferences(window.localStorage.getItem(widgetPreferenceStorageKey)));
  }, [widgetPreferenceStorageKey]);
  useEffect(() => {
    setIsWidgetCustomizationClosed(window.localStorage.getItem(widgetCustomizationStorageKey) === "true");
  }, [widgetCustomizationStorageKey]);

  const visibleWidgets = useMemo(() => widgetPreferences.filter((widget) => widget.visible), [widgetPreferences]);
  const widgetLabels: Record<DashboardWidgetId, string> = {
    metrics: t("dashboard.widgetMetrics"),
    trends: t("dashboard.widgetTrends"),
    insights: t("dashboard.widgetInsights"),
    topCategories: t("dashboard.widgetTopCategories"),
    accountBalances: t("dashboard.widgetAccountBalances")
  };

  return (
    <div className="page-stack">
      <PageHeader
        eyebrow={t("dashboard.eyebrow")}
        title={t("dashboard.title")}
        description={t("dashboard.description")}
        actions={
          <div className="dashboard-header-actions">
            <button
              className="primary-button"
              type="button"
              onClick={() => {
                setQuickAddError("");
                setQuickAddStatus("");
                setIsQuickAddOpen(true);
              }}
            >
              {t("dashboard.addTransaction")}
            </button>
          </div>
        }
      />

      {error ? <p className="error-banner">{error}</p> : null}
      {dashboard?.warnings.length ? (
        <div className="warning-banner" role="status">
          <strong>FX conversion warning</strong>
          <ul>
            {dashboard.warnings.map((warning) => (
              <li key={`${warning.code}-${warning.message}`}>{warning.message}</li>
            ))}
          </ul>
        </div>
      ) : null}

      {isQuickAddOpen && auth?.accessToken ? (
        <div className="modal-backdrop" role="presentation">
          <section className="modal-panel" role="dialog" aria-modal="true" aria-label={t("dashboard.addTransactionDialog")}>
            <div className="modal-header">
              <div>
                <span className="eyebrow">{t("dashboard.quickAdd")}</span>
                <h2>{t("dashboard.addTransactionDialog")}</h2>
              </div>
              <button className="ghost-button compact-button" type="button" onClick={() => setIsQuickAddOpen(false)}>
                {t("common.close")}
              </button>
            </div>
            {quickAddError ? <p className="error-banner">{quickAddError}</p> : null}
            {quickAddStatus ? <p className="success-banner">{quickAddStatus}</p> : null}
            <TransactionForm
              key={isQuickAddOpen ? "dashboard-quick-add-open" : "dashboard-quick-add-closed"}
              token={auth.accessToken}
              accounts={accounts}
              categories={categories}
              mode="create"
              onError={setQuickAddError}
              onStatus={setQuickAddStatus}
              onCancel={() => setIsQuickAddOpen(false)}
              onSaved={async (_, keepDetails) => {
                await refresh();
                if (!keepDetails) setIsQuickAddOpen(false);
              }}
            />
          </section>
        </div>
      ) : null}

      {!isChecklistComplete && !isChecklistDismissed ? (
        <section className="onboarding-checklist" aria-label={t("dashboard.firstRunChecklist")}>
          <div className="onboarding-checklist-header">
            <div>
              <span className="eyebrow">{t("dashboard.nextSteps")}</span>
              <h2>{t("dashboard.firstRunChecklist")}</h2>
              <p>{t("dashboard.firstRunChecklistDescription")}</p>
            </div>
            <div className="onboarding-checklist-header-actions">
              <strong>{t("dashboard.checklistComplete", { completed: completedChecklistItems, total: checklistItems.length })}</strong>
              <button className="ghost-button compact-button" type="button" onClick={dismissChecklist}>
                {t("dashboard.dismissChecklist")}
              </button>
            </div>
          </div>
          <div className="onboarding-checklist-items">
            {checklistItems.map((item) => (
              <article className={`onboarding-checklist-item${item.complete ? " is-complete" : ""}`} key={item.title}>
                <span className="checkmark" aria-hidden="true">
                  {item.complete ? "✓" : ""}
                </span>
                <div>
                  <strong>{item.title}</strong>
                  <p>{item.detail}</p>
                </div>
                <div className="onboarding-checklist-actions">
                  {item.actions.map((action) =>
                    action.kind === "link" ? (
                      <Link className="ghost-button compact-button" key={action.label} to={action.to}>
                        {action.label}
                      </Link>
                    ) : action.kind === "button" ? (
                      <button className="ghost-button compact-button" key={action.label} onClick={action.onClick} type="button">
                        {action.label}
                      </button>
                    ) : (
                      <span className="status-badge success" key={action.label}>
                        {action.label}
                      </span>
                    )
                  )}
                </div>
              </article>
            ))}
          </div>
        </section>
      ) : null}
      {isWidgetCustomizationClosed ? (
        <div className="widget-customization-toggle">
          <button className="ghost-button compact-button" type="button" onClick={() => setWidgetCustomizationClosed(false)}>
            {t("dashboard.widgetCustomize")}
          </button>
        </div>
      ) : (
        <section className="widget-customization" aria-label={t("dashboard.widgetCustomize")}>
          <div className="section-header">
            <h2>{t("dashboard.widgetCustomize")}</h2>
            <button className="ghost-button compact-button" type="button" onClick={() => setWidgetCustomizationClosed(true)}>
              {t("common.close")}
            </button>
          </div>
          <div className="widget-customization-list">
            {widgetPreferences.map((widget, index) => (
              <article className="widget-customization-row" key={widget.id}>
                <label>
                  <input
                    checked={widget.visible}
                    onChange={() => toggleWidgetVisibility(widget.id)}
                    type="checkbox"
                  />
                  <span>{widgetLabels[widget.id]}</span>
                </label>
                <div className="widget-customization-actions">
                  <button className="ghost-button compact-button" type="button" onClick={() => moveWidget(widget.id, "up")} disabled={index === 0}>
                    {t("dashboard.moveUp")}
                  </button>
                  <button className="ghost-button compact-button" type="button" onClick={() => moveWidget(widget.id, "down")} disabled={index === widgetPreferences.length - 1}>
                    {t("dashboard.moveDown")}
                  </button>
                </div>
              </article>
            ))}
          </div>
        </section>
      )}

      {visibleWidgets.map((widget) => {
        if (widget.id === "metrics") {
          return (
            <div className="metric-grid" key={widget.id}>
              <MetricCard
                label={t("dashboard.income")}
                value={formatCurrency(dashboard?.income ?? 0, mainCurrencyCode)}
                tone="positive"
                detail={t("dashboard.allMonthlyInflows")}
              />
              <MetricCard
                label={t("dashboard.expenses")}
                value={formatCurrency(dashboard?.expenses ?? 0, mainCurrencyCode)}
                tone="negative"
                detail={t("dashboard.transfersExcluded")}
              />
              <MetricCard
                label={t("dashboard.net")}
                value={formatCurrency(dashboard?.net ?? 0, mainCurrencyCode)}
                tone={(dashboard?.net ?? 0) >= 0 ? "positive" : "negative"}
                detail={t("dashboard.incomeMinusExpenses")}
              />
              <MetricCard
                label={t("dashboard.budgetRemaining")}
                value={formatCurrency(budget?.totalRemaining ?? dashboard?.budgetRemaining ?? 0, mainCurrencyCode)}
                detail={t("dashboard.acrossTrackedBudgetCategories")}
              />
            </div>
          );
        }

        if (widget.id === "trends") {
          return dashboard?.trends.spendingSparkline.length ? (
            <section className="reports-preview" aria-label={t("dashboard.reportsPreview")} key={widget.id}>
              <div>
                <span className="eyebrow">{t("dashboard.trends")}</span>
                <h2>{t("dashboard.reportsPreview")}</h2>
                <p>{describeSpendingDelta(dashboard.trends.spendingDeltaAmount, mainCurrencyCode, t)}</p>
              </div>
              <MiniSparkline points={dashboard.trends.spendingSparkline} ariaLabel={t("dashboard.sparklineAria")} />
              <Link className="ghost-button compact-button" to="/reports">
                {t("dashboard.openReports")}
              </Link>
            </section>
          ) : null;
        }

        if (widget.id === "insights") {
          return <div key={widget.id}><SpendingInsightSummary month={selectedMonth} revision={`${loading}:${dashboard?.expenses}:${budget?.totalRemaining}:${transactions.length}`} />{insights.length > 0 ? (
            <section className="insights-panel" aria-label={t("dashboard.insightsAria")} key={widget.id}>
              <div className="section-header">
                <h2>{t("dashboard.insights")}</h2>
              </div>
              <div className="insight-list">
                {insights.map((insight) => (
                  <article className={`insight-row insight-row--${insight.tone}`} key={insight.id}>
                    <div>
                      <strong>{insight.title}</strong>
                      <p>{insight.detail}</p>
                    </div>
                    {insight.action ? (
                      <Link className="ghost-button compact-button" to={insight.action.to}>
                        {insight.action.label}
                      </Link>
                    ) : null}
                  </article>
                ))}
              </div>
            </section>
          ) : null}</div>;
        }

        if (widget.id === "topCategories") {
          return (
            <SectionCard title={t("dashboard.topCategories")} key={widget.id}>
              {!dashboard?.topCategories.length ? (
                <EmptyState
                  title={loading ? t("dashboard.loadingSpending") : t("dashboard.noSpendingYet")}
                  body={t("dashboard.noSpendingBody")}
                />
              ) : (
                <div className="bar-list">
                  {dashboard.topCategories.map((category) => {
                    const largest = dashboard.topCategories[0]?.amount ?? 1;
                    const width = `${Math.max((category.amount / largest) * 100, 12)}%`;

                    return (
                      <div className="bar-item" key={category.categoryId}>
                        <div>
                          <Link to={`/transactions?type=Expense&categoryId=${category.categoryId}&${monthRangeParams}`}><strong>{category.categoryName}</strong></Link>
                          <span>{formatCurrency(category.amount, mainCurrencyCode)}</span>
                        </div>
                        <div className="bar-track">
                          <div className="bar-fill" style={{ width }} />
                        </div>
                      </div>
                    );
                  })}
                </div>
              )}
            </SectionCard>
          );
        }

        return (
          <SectionCard title={t("dashboard.accountBalances")} key={widget.id}>
            {!dashboard?.accounts.length ? (
              <EmptyState
                title={loading ? t("dashboard.loadingAccounts") : t("dashboard.noAccountsYet")}
                body={t("dashboard.noAccountsBody")}
              />
            ) : (
              <div className="account-balance-list">
                {dashboard.accounts.map((account) => (
                  <article key={account.accountId} className="balance-row">
                    <div>
                      <Link to={`/transactions?accountId=${account.accountId}&${monthRangeParams}`}><strong>{account.name}</strong></Link>
                      <p>{t("dashboard.liveBalance")}</p>
                    </div>
                    <strong>{formatCurrency(account.balance, mainCurrencyCode)}</strong>
                  </article>
                ))}
              </div>
            )}
          </SectionCard>
        );
      })}
    </div>
  );
}
