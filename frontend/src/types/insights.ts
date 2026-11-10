export type InsightPreferences = {
  userId?: string;
  budgetAlerts: boolean;
  spendingChangeAlerts: boolean;
  largePurchaseAlerts: boolean;
  unusualPurchaseAlerts: boolean;
  budgetWarningPercent: number;
  spendingIncreasePercent: number;
  minimumSpendingIncrease: number;
  largePurchaseThreshold: number;
  unusualPurchaseMinimum: number;
  unusualPurchaseMultiplier: number;
};
export type SpendingAlert = {
  key: string; kind: string; severity: string; title: string; description: string;
  amount: number; baseline: number | null; percentChange: number | null; projected: number | null;
  categoryId: string | null; transactionId: string | null; sampleCount: number; dismissed: boolean;
};
export type SpendingInsights = {
  timeZone?: string;
  month: string; throughDate: string | null; currencyCode: string; isCurrentMonth: boolean;
  elapsedDays: number; daysInMonth: number; dataComplete: boolean; excludedEntries: number; historicalMonths: number;
  income: number; netSpending: number; cashFlow: number; previousMonthSpending: number | null;
  historicalAverage: number | null; change: number | null; percentChange: number | null; projectedMonthEnd: number | null;
  categories: Array<{ categoryId: string | null; name: string; spent: number; historicalAverage: number | null; change: number | null; percentChange: number | null; budgetAvailable: number | null; budgetRemaining: number | null }>;
  payees: Array<{ payeeId: string | null; name: string; spent: number; transactions: number }>;
  daily: Array<{ day: number; current: number | null; historicalAverage: number | null; projected: number | null }>;
  alerts: SpendingAlert[];
  warnings: string[];
};
