import type { CsvOptions, CsvProfile } from "../types/csv";
import type {
  Account,
  AiSettings,
  AuthPayload,
  BudgetSummary,
  Category,
  DashboardSummary,
  ImportRule,
  MonthlyReportAnalysis,
  MonthlyReportAnalysisJob,
  MonthlyReportDraftTransaction,
  Profile,
  ReportingOverview,
  ReportingRangePreset,
  RecurringGenerationResult,
  RecurringTransactionTemplate,
  RecurringTransactionTemplatePayload,
  BackupArchive,
  Transaction,
  SavingsGoal,
  PersonalAccessToken,
  CreatePersonalAccessTokenResponse,
  ExchangeRate
} from "../types";

const API_BASE_URL = import.meta.env.VITE_API_BASE_URL ?? "";
const ANALYSIS_JOB_POLL_INTERVAL_MS = 1500;

export function resolveApiUrl(baseUrl: string, path: string): string {
  const normalizedBaseUrl = baseUrl.trim().replace(/\/+$/, "");
  const normalizedPath = path.startsWith("/") ? path : `/${path}`;

  if (!normalizedBaseUrl) {
    return normalizedPath;
  }

  if (normalizedBaseUrl.endsWith("/api") && (normalizedPath === "/api" || normalizedPath.startsWith("/api/"))) {
    return `${normalizedBaseUrl}${normalizedPath.slice(4)}`;
  }

  return `${normalizedBaseUrl}${normalizedPath}`;
}

type RequestOptions = {
  method?: string;
  body?: unknown;
  token?: string | null;
  suppressNotification?: boolean;
  parseResponse?: (response: Response) => Promise<unknown>;
};

type UnauthorizedListener = () => void;
type AuthResolver = () => AuthPayload | null;
type AuthPersister = (payload: AuthPayload | null) => void;

const unauthorizedListeners = new Set<UnauthorizedListener>();
let resolveAuth: AuthResolver | null = null;
let persistAuth: AuthPersister | null = null;
let refreshInFlight: Promise<AuthPayload | null> | null = null;

function notifyUnauthorized() {
  unauthorizedListeners.forEach((listener) => listener());
}

async function request<T>(path: string, options: RequestOptions = {}, allowRefresh = true): Promise<T> {
  const isFormData = typeof FormData !== "undefined" && options.body instanceof FormData;
  const hasJsonBody = options.body !== undefined && options.body !== null && !isFormData;
  const response = await fetch(resolveApiUrl(API_BASE_URL, path), {
    method: options.method ?? "GET",
    headers: {
      ...(hasJsonBody ? { "Content-Type": "application/json" } : {}),
      ...(options.token ? { Authorization: `Bearer ${options.token}` } : {})
    },
    body: options.body == null
      ? undefined
      : isFormData
        ? options.body as FormData
        : JSON.stringify(options.body)
  });

  if (!response.ok) {
    if (response.status === 401 && allowRefresh && options.token && resolveAuth && persistAuth) {
      const currentAuth = resolveAuth();
      if (currentAuth && currentAuth.accessToken === options.token) {
        const refreshed = await refreshSession();
        if (refreshed?.accessToken && refreshed.accessToken !== options.token) {
          return request<T>(path, { ...options, token: refreshed.accessToken }, false);
        }
      }
    }

    if (response.status === 401 && !options.suppressNotification) {
      notifyUnauthorized();
    }

    throw new Error(await readErrorMessage(response));
  }

  if (response.status === 204) {
    return undefined as T;
  }

  if (options.parseResponse) {
    return options.parseResponse(response) as Promise<T>;
  }

  return response.json() as Promise<T>;
}

async function refreshSession(): Promise<AuthPayload | null> {
  if (!resolveAuth || !persistAuth) {
    return null;
  }

  if (!refreshInFlight) {
    const auth = resolveAuth();
    if (!auth?.refreshToken) {
      return null;
    }

    refreshInFlight = request<AuthPayload>("/api/auth/refresh", {
      method: "POST",
      body: { refreshToken: auth.refreshToken },
      suppressNotification: true
    }, false)
      .then((payload) => {
        persistAuth?.(payload);
        return payload;
      })
      .catch(() => {
        persistAuth?.(null);
        return null;
      })
      .finally(() => {
        refreshInFlight = null;
      });
  }

  return refreshInFlight;
}

async function readErrorMessage(response: Response): Promise<string> {
  const fallback = `Request failed with status ${response.status}`;

  try {
    const responseBody = await response.clone().json();
    if (responseBody && typeof responseBody === "object") {
      const problem = responseBody as { title?: unknown; detail?: unknown };
      if (typeof problem.title === "string" && problem.title.trim()) {
        return problem.title;
      }

      if (typeof problem.detail === "string" && problem.detail.trim()) {
        return problem.detail;
      }
    }

    const serialized = JSON.stringify(responseBody);
    return serialized || fallback;
  } catch {
    const text = await response.text();
    return text || fallback;
  }
}

function wait(ms: number) {
  return new Promise((resolve) => window.setTimeout(resolve, ms));
}

async function fetchMonthlyReportAnalysisJob(token: string, jobId: string): Promise<MonthlyReportAnalysisJob> {
  return request<MonthlyReportAnalysisJob>(`/api/imports/monthly-report/analyze/${jobId}`, { token });
}

async function waitForMonthlyReportAnalysisJob(
  token: string,
  initialJob: MonthlyReportAnalysisJob,
  onJobUpdate?: (job: MonthlyReportAnalysisJob) => void
): Promise<MonthlyReportAnalysis> {
  let job = initialJob;
  onJobUpdate?.(job);

  while (job.status === "running") {
    await wait(ANALYSIS_JOB_POLL_INTERVAL_MS);
    job = await fetchMonthlyReportAnalysisJob(token, job.jobId);
    onJobUpdate?.(job);
  }

  return readMonthlyReportAnalysisResult(job);
}

function readMonthlyReportAnalysisResult(job: MonthlyReportAnalysisJob): MonthlyReportAnalysis {
  if (job.status === "failed") {
    throw new Error(job.error || "AI analysis failed.");
  }

  if (!job.analysis) {
    throw new Error("AI analysis finished without results.");
  }

  return job.analysis;
}

function readContentDispositionFilename(contentDisposition: string | null, fallback: string) {
  if (!contentDisposition) {
    return fallback;
  }

  const utf8Match = /filename\*=UTF-8''([^;]+)/i.exec(contentDisposition);
  if (utf8Match?.[1]) {
    const encodedFilename = utf8Match[1].replace(/^"|"$/g, "");
    try {
      return decodeURIComponent(encodedFilename);
    } catch {
      const filenameMatch = /filename="?([^";]+)"?/i.exec(contentDisposition);
      return filenameMatch?.[1] || encodedFilename || fallback;
    }
  }

  const filenameMatch = /filename="?([^";]+)"?/i.exec(contentDisposition);
  return filenameMatch?.[1] || fallback;
}

export const apiClient = {
  setAuthHandlers(nextResolveAuth: AuthResolver | null, nextPersistAuth: AuthPersister | null) {
    resolveAuth = nextResolveAuth;
    persistAuth = nextPersistAuth;
  },
  onUnauthorized(listener: UnauthorizedListener) {
    unauthorizedListeners.add(listener);
    return () => {
      unauthorizedListeners.delete(listener);
    };
  },
  register(login: string, password: string, email?: string) {
    return request<AuthPayload>("/api/auth/register", {
      method: "POST",
      body: { login, password, ...(email ? { email } : {}) }
    });
  },
  login(login: string, password: string) {
    return request<AuthPayload>("/api/auth/login", {
      method: "POST",
      body: { login, password }
    });
  },
  refresh(refreshToken: string) {
    return request<AuthPayload>("/api/auth/refresh", {
      method: "POST",
      body: { refreshToken }
    });
  },
  getDashboard(token: string, month: string) {
    return request<DashboardSummary>(`/api/dashboard/summary?month=${month}`, { token });
  },
  getReportingOverview(token: string, filters: { rangePreset: ReportingRangePreset; accountId?: string }) {
    const params = new URLSearchParams({ range: filters.rangePreset });
    if (filters.accountId) {
      params.set("accountId", filters.accountId);
    }

    return request<ReportingOverview>(`/api/reports/overview?${params.toString()}`, { token });
  },
  getAccounts(token: string) {
    return request<Account[]>("/api/accounts", { token });
  },
  createAccount(
    token: string,
    payload: Pick<
      Account,
      "name" | "type" | "currencyCode" | "openingBalance" | "excludeFromBudget" | "excludeFromNetWorth" | "institutionName" | "accountNumberMasked" | "iconKind"
    >
  ) {
    return request<Account>("/api/accounts", {
      method: "POST",
      token,
      body: payload
    });
  },
  updateAccount(token: string, account: Account) {
    return request<Account>(`/api/accounts/${account.id}`, {
      method: "PUT",
      token,
      body: {
        name: account.name,
        type: account.type,
        currencyCode: account.currencyCode,
        openingBalance: account.openingBalance,
        isActive: account.isActive,
        excludeFromBudget: account.excludeFromBudget ?? false,
        excludeFromNetWorth: account.excludeFromNetWorth ?? false,
        institutionName: account.institutionName ?? null,
        accountNumberMasked: account.accountNumberMasked ?? null,
        iconKind: account.iconKind
      }
    });
  },
  getProfile(token: string) {
    return request<Profile>("/api/settings/profile", { token });
  },
  updateProfile(token: string, preferredCurrencyCode: string, preferredLanguageCode: string) {
    return request<Profile>("/api/settings/profile", {
      method: "PUT",
      token,
      body: { preferredCurrencyCode, preferredLanguageCode }
    });
  },
  getExchangeRates(token: string) {
    return request<ExchangeRate[]>("/api/settings/exchange-rates", { token });
  },
  upsertExchangeRate(token: string, payload: Pick<ExchangeRate, "fromCurrencyCode" | "toCurrencyCode" | "month" | "rate">) {
    return request<ExchangeRate>("/api/settings/exchange-rates", {
      method: "PUT",
      token,
      body: payload
    });
  },
  deleteExchangeRate(token: string, rateId: string) {
    return request<void>(`/api/settings/exchange-rates/${rateId}`, {
      method: "DELETE",
      token
    });
  },
  clearAccountData(token: string) {
    return request<void>("/api/settings/account-data", {
      method: "DELETE",
      token
    });
  },
  deleteAccount(token: string) {
    return request<void>("/api/settings/account", {
      method: "DELETE",
      token
    });
  },
  getAiSettings(token: string) {
    return request<AiSettings>("/api/settings/ai", { token });
  },
  saveAiProviderKey(token: string, provider: string, apiKey: string, options: { baseUrl?: string; model?: string } = {}) {
    return request<AiSettings>(`/api/settings/ai/${provider}`, {
      method: "PUT",
      token,
      body: { apiKey, ...options }
    });
  },
  updateAiProviderModel(token: string, provider: string, model: string) {
    return request<AiSettings>(`/api/settings/ai/${provider}/model`, {
      method: "PUT",
      token,
      body: { model }
    });
  },
  getAiProviderModels(token: string, provider: string) {
    return request<{ models: string[] }>(`/api/settings/ai/${provider}/models`, { token });
  },
  removeAiProviderKey(token: string, provider: string) {
    return request<AiSettings>(`/api/settings/ai/${provider}`, {
      method: "DELETE",
      token
    });
  },
  updateDefaultAiProvider(token: string, provider: string) {
    return request<AiSettings>("/api/settings/ai/default-provider", {
      method: "PUT",
      token,
      body: { provider }
    });
  },

  getPersonalAccessTokens(token: string) {
    return request<PersonalAccessToken[]>("/api/settings/personal-access-tokens", { token });
  },
  createPersonalAccessToken(token: string, name: string) {
    return request<CreatePersonalAccessTokenResponse>("/api/settings/personal-access-tokens", {
      method: "POST",
      token,
      body: { name }
    });
  },
  revokePersonalAccessToken(token: string, id: string) {
    return request<void>(`/api/settings/personal-access-tokens/${id}`, {
      method: "DELETE",
      token
    });
  },

  getCategories(token: string) {
    return request<Category[]>("/api/categories", { token });
  },
  getImportRules(token: string) {
    return request<ImportRule[]>("/api/import-rules", { token });
  },
  createImportRule(token: string, payload: Omit<ImportRule, "id" | "createdAtUtc" | "updatedAtUtc">) {
    return request<ImportRule>("/api/import-rules", {
      method: "POST",
      token,
      body: payload
    });
  },
  updateImportRule(token: string, rule: ImportRule) {
    return request<ImportRule>(`/api/import-rules/${rule.id}`, {
      method: "PUT",
      token,
      body: {
        name: rule.name,
        matchField: rule.matchField,
        matchOperator: rule.matchOperator,
        matchValue: rule.matchValue,
        ...(rule.conditions ? { conditions: rule.conditions } : {}),
        assignCategoryId: rule.assignCategoryId,
        assignTransactionType: rule.assignTransactionType,
        priority: rule.priority,
        isActive: rule.isActive
      }
    });
  },
  deleteImportRule(token: string, ruleId: string) {
    return request<void>(`/api/import-rules/${ruleId}`, {
      method: "DELETE",
      token
    });
  },
  createCategory(token: string, payload: Pick<Category, "name" | "kind" | "color">) {
    return request<Category>("/api/categories", {
      method: "POST",
      token,
      body: payload
    });
  },
  updateCategory(token: string, category: Pick<Category, "id" | "name" | "kind" | "color" | "isSystem">) {
    return request<Category>(`/api/categories/${category.id}`, {
      method: "PUT",
      token,
      body: {
        name: category.name,
        kind: category.kind,
        color: category.color,
        isSystem: category.isSystem
      }
    });
  },
  deleteCategory(token: string, categoryId: string) {
    return request<void>(`/api/categories/${categoryId}`, {
      method: "DELETE",
      token
    });
  },

  getSavingsGoals(token: string) {
    return request<SavingsGoal[]>("/api/savings-goals", { token });
  },
  createSavingsGoal(token: string, payload: { name: string; targetAmount: number; deadlineUtc?: string | null }) {
    return request<SavingsGoal>("/api/savings-goals", { method: "POST", token, body: payload });
  },
  updateSavingsGoal(token: string, goalId: string, payload: { name: string; targetAmount: number; deadlineUtc?: string | null }) {
    return request<SavingsGoal>(`/api/savings-goals/${goalId}`, { method: "PUT", token, body: payload });
  },

  getRecurringTransactions(token: string) {
    return request<RecurringTransactionTemplate[]>("/api/recurring-transactions", { token });
  },
  createRecurringTransaction(token: string, payload: RecurringTransactionTemplatePayload) {
    return request<RecurringTransactionTemplate>("/api/recurring-transactions", {
      method: "POST",
      token,
      body: payload
    });
  },
  updateRecurringTransaction(token: string, templateId: string, payload: RecurringTransactionTemplatePayload) {
    return request<RecurringTransactionTemplate>(`/api/recurring-transactions/${templateId}`, {
      method: "PUT",
      token,
      body: payload
    });
  },
  updateRecurringTransactionStatus(token: string, templateId: string, isActive: boolean) {
    return request<RecurringTransactionTemplate>(`/api/recurring-transactions/${templateId}/status`, {
      method: "PATCH",
      token,
      body: { isActive }
    });
  },
  deleteRecurringTransaction(token: string, templateId: string) {
    return request<void>(`/api/recurring-transactions/${templateId}`, {
      method: "DELETE",
      token
    });
  },
  generateDueRecurringTransactions(token: string) {
    return request<RecurringGenerationResult>("/api/recurring-transactions/generate", {
      method: "POST",
      token
    });
  },

  getTransactions(token: string, query = "") {
    return request<Transaction[]>(`/api/transactions${query}`, { token });
  },
  createTransaction(
    token: string,
    payload: {
      accountId: string;
      categoryId?: string;
      destinationAccountId?: string;
      savingsGoalId?: string;
      amount: number;
      type: string;
      occurredOnUtc: string;
      note?: string;
      splitLines?: Array<{ categoryId: string; amount: number }>;
      updateMetadata?: boolean;
      payeeId?: string;
      reference?: string;
      tags?: string;
      paymentMethod?: string;
      status?: string;
    }
  ) {
    return request<Transaction>("/api/transactions", {
      method: "POST",
      token,
      body: payload
    });
  },
  updateTransaction(
    token: string,
    transactionId: string,
    payload: {
      categoryId?: string;
      destinationAccountId?: string;
      savingsGoalId?: string;
      amount: number;
      type: string;
      occurredOnUtc: string;
      note?: string;
      splitLines?: Array<{ categoryId: string; amount: number }>;
      updateMetadata?: boolean;
      payeeId?: string;
      reference?: string;
      tags?: string;
      paymentMethod?: string;
      status?: string;
    }
  ) {
    return request<Transaction>(`/api/transactions/${transactionId}`, {
      method: "PUT",
      token,
      body: payload
    });
  },
  deleteTransaction(token: string, transactionId: string) {
    return request<void>(`/api/transactions/${transactionId}`, {
      method: "DELETE",
      token
    });
  },
  moveTransactionAccount(token: string, transactionId: string, destinationAccountId: string) {
    return request<Transaction>(`/api/transactions/${transactionId}/move-account`, {
      method: "POST",
      token,
      body: { destinationAccountId }
    });
  },
  analyzeMonthlyReport(
    token: string,
    payload: { accountId: string; month: string; provider: string; file: File },
    onJobUpdate?: (job: MonthlyReportAnalysisJob) => void
  ) {
    const body = new FormData();
    body.append("accountId", payload.accountId);
    body.append("month", payload.month);
    body.append("provider", payload.provider);
    body.append("file", payload.file);

    return request<MonthlyReportAnalysisJob>("/api/imports/monthly-report/analyze", {
      method: "POST",
      token,
      body
    }).then((job) => waitForMonthlyReportAnalysisJob(token, job, onJobUpdate));
  },

  retryMonthlyReportAnalysisParse(token: string, jobId: string, onJobUpdate?: (job: MonthlyReportAnalysisJob) => void) {
    return request<MonthlyReportAnalysisJob>(`/api/imports/monthly-report/analyze/${jobId}/retry-parse`, {
      method: "POST",
      token
    }).then((job) => {
      onJobUpdate?.(job);
      if (job.status === "running") {
        return null;
      }

      return readMonthlyReportAnalysisResult(job);
    });
  },

  downloadMonthlyReportAnalysisRawOutput(token: string, jobId: string) {
    return request<{ blob: Blob; filename: string }>(`/api/imports/monthly-report/analyze/${jobId}/raw-output`, {
      token,
      parseResponse: async (response) => ({
        blob: await response.blob(),
        filename: readContentDispositionFilename(
          response.headers.get("content-disposition"),
          `ledgerra-ai-output-${jobId}.json`
        )
      })
    });
  },

  nativeImport<T>(token: string, step: "inspect" | "preview", body: FormData) {
    return request<T>(`/api/imports/native/${step}`, { token, method: "POST", body });
  },

  getImportProfiles(token: string) { return request<CsvProfile[]>("/api/bookkeeping/profiles", { token }); },
  saveImportProfile(token: string, payload: unknown) { return request<CsvProfile>("/api/bookkeeping/profiles", { token, method: "POST", body: payload }); },
  banks<T>(token: string, path: string, method = "GET", payload?: unknown) {
    return request<T>(`/api/banks/${path}`, { token, method, ...(payload === undefined ? {} : { body: payload }) });
  },
  bookkeeping<T>(token: string, path: string, method = "GET", payload?: unknown) {
    return request<T>(`/api/bookkeeping/${path}`, { token, method, ...(payload === undefined ? {} : { body: payload }) });
  },

  previewCsvBankImport(token: string, payload: { accountId: string; file: File; dateColumn: string; amountColumn: string; descriptionColumn?: string; invertAmounts?: boolean } & Partial<CsvOptions>) {
    const body = new FormData();
    body.append("accountId", payload.accountId);
    body.append("file", payload.file);
    body.append("dateColumn", payload.dateColumn);
    body.append("amountColumn", payload.amountColumn);
    body.append("invertAmounts", String(payload.invertAmounts ?? false));
    for (const key of ["delimiter", "cultureName", "dateFormat", "debitColumn", "creditColumn", "typeColumn", "payeeColumn", "categoryColumn", "referenceColumn", "tagsColumn", "positiveType", "negativeType"] as const) {
      if (payload[key]) body.append(key, payload[key]!);
    }
    if (payload.descriptionColumn) {
      body.append("descriptionColumn", payload.descriptionColumn);
    }

    return request<MonthlyReportAnalysis>("/api/imports/monthly-report/csv-preview", {
      method: "POST",
      token,
      body
    });
  },

  commitMonthlyReportDrafts(token: string, transactions: MonthlyReportDraftTransaction[], acceptedDuplicateSourceIds: string[] = []) {
    return request<{ created: Transaction[] }>("/api/imports/monthly-report/commit", {
      method: "POST",
      token,
      body: { transactions, acceptedDuplicateSourceIds }
    });
  },
  getBudget(token: string, year: number, month: number) {
    return request<BudgetSummary>(`/api/budgets/${year}/${month}`, { token });
  },
  updateBudget(
    token: string,
    year: number,
    month: number,
    categoryLimits: Array<{ categoryId: string; plannedAmount: number; carryOverUnspent: boolean }>
  ) {
    return request<BudgetSummary>(`/api/budgets/${year}/${month}`, {
      method: "PUT",
      token,
      body: { categoryLimits }
    });
  },
  exportBackup(token: string) {
    return request<BackupArchive>("/api/backup/export", { token });
  },
  restoreBackup(token: string, archive: BackupArchive) {
    return request<void>("/api/backup/restore", {
      method: "POST",
      token,
      body: archive
    });
  }
};
