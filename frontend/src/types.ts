export type AuthPayload = {
  userId: string;
  login: string;
  email: string;
  accessToken: string;
  refreshToken: string;
  expiresAtUtc: string;
};

export type AccountIconKind = "Bank" | "Piggy" | "Card" | "Cash" | "Chart" | "Users";

export type Account = {
  id: string;
  name: string;
  type: string;
  currencyCode: string;
  openingBalance: number;
  currentBalance: number;
  isActive: boolean;
  excludeFromBudget?: boolean;
  excludeFromNetWorth?: boolean;
  institutionName?: string | null;
  accountNumberMasked?: string | null;
  iconKind: AccountIconKind;
};

export type Profile = {
  email: string;
  preferredCurrencyCode: string;
  preferredLanguageCode: string;
};

export type AiProviderStatus = {
  isConfigured: boolean;
  maskedKey?: string | null;
  baseUrl?: string | null;
  model?: string | null;
};

export type ExchangeRate = {
  id: string;
  fromCurrencyCode: string;
  toCurrencyCode: string;
  month: string;
  rate: number;
  updatedAtUtc: string;
};

export type AiSettings = {
  providers: {
    openAi: AiProviderStatus;
    anthropic: AiProviderStatus;
    openAiCompatible?: AiProviderStatus;
  };
  defaultProvider?: string | null;
};

export type Category = {
  parentCategoryId?: string | null;
  id: string;
  name: string;
  kind: string;
  color?: string | null;
  isSystem: boolean;
};

export type Transaction = {
  parentTransactionId?: string | null;
  splitGroupId?: string | null;
  id: string;
  accountId: string;
  categoryId?: string | null;
  amount: number;
  type: string;
  occurredOnUtc: string;
  note?: string | null;
  transferGroupId?: string | null;
  savingsGoalId?: string | null;
};


export type RecurringTransactionTemplate = {
  destinationAccountId?: string | null;
  id: string;
  accountId: string;
  categoryId?: string | null;
  amount: number;
  type: string;
  interval: string;
  startOnUtc: string;
  lastGeneratedOnUtc?: string | null;
  isActive: boolean;
  note?: string | null;
};

export type RecurringTransactionTemplatePayload = {
  destinationAccountId?: string | null;
  accountId: string;
  categoryId?: string | null;
  amount: number;
  type: string;
  interval: string;
  startOnUtc: string;
  isActive?: boolean;
  note?: string | null;
};

export type RecurringGenerationResult = {
  generated: number;
};

export type MonthlyReportDraftTransaction = {
  externalKey?: string | null;
  payeeName?: string | null;
  reference?: string | null;
  tags?: string | null;
  sourceId: string;
  accountId: string;
  categoryId?: string | null;
  amount: number;
  type: string;
  occurredOnUtc: string;
  note?: string | null;
  confidence: number;
  warnings: string[];
  appliedRuleId?: string | null;
  appliedRuleName?: string | null;
  isLikelyDuplicate?: boolean;
  duplicateTransactionId?: string | null;
  duplicateReason?: string | null;
  isSelectedByDefault?: boolean;
};

export type MonthlyReportAnalysis = {
  transactions: MonthlyReportDraftTransaction[];
  warnings: string[];
};

export type MonthlyReportAnalysisJob = {
  jobId: string;
  status: "running" | "completed" | "failed";
  statusMessage?: string | null;
  generatedOutputCharacters?: number | null;
  usage?: {
    promptTokens: number;
    completionTokens: number;
    totalTokens: number;
  } | null;
  analysis?: MonthlyReportAnalysis | null;
  error?: string | null;
  hasRawAiOutput?: boolean;
  createdAtUtc: string;
  updatedAtUtc: string;
};

export type ImportRule = {
  conditions?: Array<{ field: string; operator: string; value: string }>;
  id: string;
  name: string;
  matchField: string;
  matchOperator: string;
  matchValue: string;
  assignCategoryId: string;
  assignTransactionType: string;
  priority: number;
  isActive: boolean;
  createdAtUtc: string;
  updatedAtUtc: string;
};

export type BudgetCategory = {
  categoryId: string;
  categoryName: string;
  planned: number;
  carryForward: number;
  available: number;
  carryOverUnspent: boolean;
  spent: number;
  remaining: number;
};

export type BudgetSummary = {
  totalPlanned: number;
  totalSpent: number;
  totalRemaining: number;
  categories: BudgetCategory[];
};

export type DashboardSummary = {
  income: number;
  expenses: number;
  net: number;
  budgetRemaining: number;
  topCategories: Array<{
    categoryId: string;
    categoryName: string;
    amount: number;
  }>;
  accounts: Array<{
    accountId: string;
    name: string;
    balance: number;
  }>;
  trends: {
    spendingDeltaAmount: number;
    spendingDeltaPercent?: number | null;
    spendingSparkline: Array<{
      month: string;
      amount: number;
    }>;
  };
  currencyCode: string;
  warnings: Array<{
    code: string;
    message: string;
  }>;
};


export type SavingsGoal = {
  id: string;
  name: string;
  targetAmount: number;
  savedAmount: number;
  progressPercent: number;
  deadlineUtc?: string | null;
};

export type ReportingRangePreset = "3M" | "6M" | "12M" | "YTD";

export type ReportingOverview = {
  rangePreset: ReportingRangePreset;
  startMonth: string;
  endMonth: string;
  currencyCode: string;
  summary: {
    incomeTotal: number;
    expenseTotal: number;
    netCashFlow: number;
    spendingDeltaAmount: number;
    spendingDeltaPercent?: number | null;
    netWorthDelta: number;
  };
  monthlySpendingTrend: Array<{
    month: string;
    amount: number;
  }>;
  incomeVsExpense: Array<{
    month: string;
    income: number;
    expenses: number;
    net: number;
  }>;
  categoryBreakdown: Array<{
    categoryId: string;
    categoryName: string;
    amount: number;
    percentage: number;
  }>;
  netWorthHistory: Array<{
    month: string;
    netWorth: number;
    currencyCode: string;
  }>;
  warnings: Array<{
    code: string;
    message: string;
  }>;
};

export type BackupAccount = {
  id: string;
  name: string;
  type: string;
  currencyCode: string;
  openingBalance: number;
  excludeFromBudget?: boolean;
  excludeFromNetWorth?: boolean;
  isActive: boolean;
  institutionName?: string | null;
  accountNumberMasked?: string | null;
  iconKind?: string;
};

export type BackupCategory = {
  id: string;
  name: string;
  kind: string;
  color?: string | null;
};

export type BackupTransaction = {
  id: string;
  accountId: string;
  categoryId?: string | null;
  amount: number;
  type: string;
  occurredOnUtc: string;
  note?: string | null;
  transferGroupId?: string | null;
  savingsGoalId?: string | null;
};

export type BackupArchive = {
  version: number;
  exportedAtUtc: string;
  accounts: BackupAccount[];
  categories: BackupCategory[];
  transactions: BackupTransaction[];
  budgetPeriods: Array<{
    id: string;
    year: number;
    month: number;
    categoryLimits: Array<{ id: string; categoryId: string; plannedAmount: number; carryOverUnspent?: boolean }>;
  }>;
  savingsGoals?: Array<{
    id: string;
    name: string;
    targetAmount: number;
    deadlineUtc?: string | null;
    createdAtUtc?: string | null;
    updatedAtUtc?: string | null;
  }>;
};


export type PersonalAccessToken = {
  id: string;
  name: string;
  tokenPrefix: string;
  createdAtUtc: string;
  lastUsedAtUtc?: string | null;
  revokedAtUtc?: string | null;
};

export type CreatePersonalAccessTokenResponse = {
  token: PersonalAccessToken;
  plainTextToken: string;
};
