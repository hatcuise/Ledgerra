import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, test, vi } from "vitest";
import { MemoryRouter } from "react-router-dom";
import { DashboardPage } from "./DashboardPage";
import { MonthProvider } from "../state/MonthContext";
import type { Account, BudgetSummary, DashboardSummary, Transaction } from "../types";

const mocks = vi.hoisted(() => ({
  createCategory: vi.fn(),
  createTransaction: vi.fn(),
  data: {
    refresh: vi.fn(),
    accounts: [] as Account[],
    categories: [
      { id: "category-1", name: "Groceries", kind: "Expense", isSystem: true },
      { id: "category-2", name: "Salary", kind: "Income", isSystem: true }
    ],
    dashboard: {
      income: 0,
      expenses: 0,
      net: 0,
      budgetRemaining: 0,
      topCategories: [],
      accounts: [],
      trends: {
        spendingDeltaAmount: 0,
        spendingDeltaPercent: null,
        spendingSparkline: []
      },
      currencyCode: "USD",
      warnings: []
    } as DashboardSummary,
    budget: {
      totalPlanned: 0,
      totalSpent: 0,
      totalRemaining: 0,
      categories: []
    } as BudgetSummary,
    loading: false,
    error: null as string | null,
    profile: { email: "owner@ledgerra.local", preferredCurrencyCode: "USD", preferredLanguageCode: "en" },
    transactions: [] as Transaction[]
  }
}));

vi.mock("../state/AuthContext", () => ({
  useAuth: () => ({ auth: { accessToken: "token" } })
}));

vi.mock("../api/client", () => ({
  apiClient: {
    bookkeeping: vi.fn().mockResolvedValue([]),
    createCategory: mocks.createCategory,
    createTransaction: mocks.createTransaction
  }
}));

vi.mock("../hooks/useLedgerraData", () => ({
  useLedgerraData: () => mocks.data
}));

function dashboardTree(initialMonth = "2026-05") {
  return (
    <MemoryRouter>
      <MonthProvider initialMonth={initialMonth}>
        <DashboardPage />
      </MonthProvider>
    </MemoryRouter>
  );
}

function renderDashboardPage(initialMonth?: string) {
  return render(dashboardTree(initialMonth));
}

describe("DashboardPage", () => {
  beforeEach(() => {
    cleanup();
    vi.clearAllMocks();
    localStorage.clear();
    mocks.createTransaction.mockResolvedValue({ id: "transaction-new" });
    mocks.createCategory.mockResolvedValue({
      id: "category-new",
      name: "Pets",
      kind: "Expense",
      color: "#ff8800",
      isSystem: false
    });
    mocks.data.refresh.mockResolvedValue(undefined);
    mocks.data.accounts = [];
    mocks.data.dashboard = {
      income: 0,
      expenses: 0,
      net: 0,
      budgetRemaining: 0,
      topCategories: [],
      accounts: [],
      trends: {
        spendingDeltaAmount: 0,
        spendingDeltaPercent: null,
        spendingSparkline: []
      },
      currencyCode: "USD",
      warnings: []
    } as DashboardSummary;
    mocks.data.budget = {
      totalPlanned: 0,
      totalSpent: 0,
      totalRemaining: 0,
      categories: []
    } as BudgetSummary;
    mocks.data.transactions = [];
    mocks.data.profile = { email: "owner@ledgerra.local", preferredCurrencyCode: "USD", preferredLanguageCode: "en" };
  });


  test("surfaces FX conversion warnings from dashboard totals", () => {
    mocks.data.dashboard = {
      ...mocks.data.dashboard,
      currencyCode: "USD",
      warnings: [
        { code: "MissingFxRate", message: "Missing FX rate for EUR to USD in 2026-05." }
      ]
    } as DashboardSummary;

    renderDashboardPage();

    expect(screen.getByText("FX conversion warning")).toBeInTheDocument();
    expect(screen.getByText("Missing FX rate for EUR to USD in 2026-05.")).toBeInTheDocument();
  });

  test("shows first-run checklist progress from current ledger data", () => {
    mocks.data.accounts = [
      {
        id: "account-1",
        name: "Main checking",
        type: "Checking",
        currencyCode: "USD",
        openingBalance: 500,
        currentBalance: 500,
        isActive: true,
        iconKind: "Bank"
      }
    ];
    mocks.data.budget = {
      totalPlanned: 300,
      totalSpent: 0,
      totalRemaining: 300,
      categories: [
        {
          categoryId: "category-1",
          categoryName: "Groceries",
          planned: 300,
          carryForward: 0,
          available: 300,
          carryOverUnspent: false,
          spent: 0,
          remaining: 300
        }
      ]
    };

    renderDashboardPage();

    expect(screen.getByText("First run checklist")).toBeInTheDocument();
    expect(screen.getByText("2 of 5 complete")).toBeInTheDocument();
    expect(screen.getByText("Main checking")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Add first transaction" })).toHaveAttribute("href", "/transactions");
  });

  test("lets users acknowledge currency and default categories without resetting on currency changes", async () => {
    const user = userEvent.setup();

    const { rerender } = renderDashboardPage();

    await user.click(screen.getByRole("button", { name: "Confirm USD" }));
    expect(screen.getByRole("link", { name: "Open categories" })).toHaveAttribute("href", "/categories");
    await user.click(screen.getByRole("button", { name: "Reviewed" }));

    expect(screen.getByText("2 of 5 complete")).toBeInTheDocument();
    expect(screen.getByText("Currency confirmed")).toBeInTheDocument();
    expect(screen.getByText("Categories reviewed")).toBeInTheDocument();

    mocks.data.profile = { email: "owner@ledgerra.local", preferredCurrencyCode: "EUR", preferredLanguageCode: "en" };
    rerender(dashboardTree());

    expect(screen.getByText("2 of 5 complete")).toBeInTheDocument();
    expect(screen.getByText("Currency confirmed")).toBeInTheDocument();
    expect(screen.getByText("Categories reviewed")).toBeInTheDocument();
  });

  test("lets users close the first-run checklist without completing steps", async () => {
    const user = userEvent.setup();

    const { rerender } = renderDashboardPage();

    expect(screen.getByLabelText("First run checklist")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Close checklist" }));

    expect(screen.queryByLabelText("First run checklist")).not.toBeInTheDocument();
    expect(localStorage.getItem("ledgerra:onboarding-dismissed:owner@ledgerra.local")).toBe("true");
    expect(JSON.parse(localStorage.getItem("ledgerra:onboarding:owner@ledgerra.local") ?? "{}")).toEqual({
      currency: false,
      categories: false
    });

    rerender(dashboardTree());

    expect(screen.queryByLabelText("First run checklist")).not.toBeInTheDocument();
  });

  test("hides onboarding checklist after every step is complete", () => {
    localStorage.setItem(
      "ledgerra:onboarding:owner@ledgerra.local",
      JSON.stringify({ currency: true, categories: true })
    );
    mocks.data.accounts = [
      {
        id: "account-1",
        name: "Main checking",
        type: "Checking",
        currencyCode: "USD",
        openingBalance: 500,
        currentBalance: 500,
        isActive: true,
        iconKind: "Bank"
      }
    ];
    mocks.data.budget = {
      totalPlanned: 300,
      totalSpent: 0,
      totalRemaining: 300,
      categories: [
        {
          categoryId: "category-1",
          categoryName: "Groceries",
          planned: 300,
          carryForward: 0,
          available: 300,
          carryOverUnspent: false,
          spent: 0,
          remaining: 300
        }
      ]
    };
    mocks.data.transactions = [
      {
        id: "transaction-1",
        accountId: "account-1",
        categoryId: "category-1",
        amount: 12,
        type: "Expense",
        occurredOnUtc: "2026-04-30T10:00:00Z",
        note: "Coffee"
      }
    ];

    renderDashboardPage();

    expect(screen.queryByLabelText("Onboarding checklist")).not.toBeInTheDocument();
  });

  test("shows actionable insights for budget pressure and uncategorized transactions", () => {
    mocks.data.dashboard = {
      income: 4000,
      expenses: 1240,
      net: 2760,
      budgetRemaining: 260,
      topCategories: [],
      accounts: [],
      trends: {
        spendingDeltaAmount: 60,
        spendingDeltaPercent: 12.5,
        spendingSparkline: [
          { month: "2026-03", amount: 480 },
          { month: "2026-04", amount: 540 }
        ]
      },
      currencyCode: "USD",
      warnings: []
    } as DashboardSummary;
    mocks.data.budget = {
      totalPlanned: 1500,
      totalSpent: 1240,
      totalRemaining: 260,
      categories: [
        {
          categoryId: "category-1",
          categoryName: "Dining",
          planned: 500,
          carryForward: 0,
          available: 500,
          carryOverUnspent: false,
          spent: 410,
          remaining: 90
        },
        {
          categoryId: "category-3",
          categoryName: "Subscriptions",
          planned: 100,
          carryForward: 0,
          available: 100,
          carryOverUnspent: false,
          spent: 105,
          remaining: -5
        }
      ]
    };
    mocks.data.transactions = [
      {
        id: "transaction-1",
        accountId: "account-1",
        categoryId: null,
        amount: 12,
        type: "Expense",
        occurredOnUtc: "2026-04-30T10:00:00Z",
        note: "Coffee"
      },
      {
        id: "transaction-2",
        accountId: "account-1",
        amount: 20,
        type: "Expense",
        occurredOnUtc: "2026-04-29T10:00:00Z",
        note: "Lunch"
      },
      {
        id: "transaction-3",
        accountId: "account-1",
        categoryId: "category-1",
        amount: 30,
        type: "Expense",
        occurredOnUtc: "2026-04-28T10:00:00Z",
        note: "Dinner"
      }
    ];

    renderDashboardPage();

    expect(screen.getByRole("heading", { name: "Insights" })).toBeInTheDocument();
    expect(screen.getByText("Dining is 82% of its budget.")).toBeInTheDocument();
    expect(screen.getByText("Subscriptions is over budget by $5.00.")).toBeInTheDocument();
    expect(screen.getByText("You have 2 uncategorized expense transactions.")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Review transactions" })).toHaveAttribute("href", "/transactions?view=uncategorized&from=2026-05-01&to=2026-05-31");
  });

  test("prompts users to set a budget when transactions exist without planned limits", () => {
    mocks.data.budget = {
      totalPlanned: 0,
      totalSpent: 0,
      totalRemaining: 0,
      categories: []
    };
    mocks.data.transactions = [
      {
        id: "transaction-1",
        accountId: "account-1",
        categoryId: "category-1",
        amount: 45,
        type: "Expense",
        occurredOnUtc: "2026-04-30T10:00:00Z",
        note: "Market"
      }
    ];

    renderDashboardPage();

    expect(screen.getByText("Set a monthly budget to turn spending into progress alerts.")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Open budgets" })).toHaveAttribute("href", "/budgets");
  });

  test("shows a compact trends teaser linking to reports", () => {
    mocks.data.dashboard = {
      income: 4000,
      expenses: 540,
      net: 3460,
      budgetRemaining: 260,
      topCategories: [],
      accounts: [],
      trends: {
        spendingDeltaAmount: 60,
        spendingDeltaPercent: 12.5,
        spendingSparkline: [
          { month: "2026-01", amount: 300 },
          { month: "2026-02", amount: 420 },
          { month: "2026-03", amount: 480 },
          { month: "2026-04", amount: 540 }
        ]
      },
      currencyCode: "USD",
      warnings: []
    } as DashboardSummary;

    renderDashboardPage();

    expect(screen.getByText("Reports preview")).toBeInTheDocument();
    expect(screen.getByText("Spending is up $60.00 vs prior month.")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Open reports" })).toHaveAttribute("href", "/reports");
  });

  test("customizes dashboard widgets and persists order/visibility per user", async () => {
    const user = userEvent.setup();
    mocks.data.dashboard = {
      income: 4000,
      expenses: 540,
      net: 3460,
      budgetRemaining: 260,
      topCategories: [{ categoryId: "category-1", categoryName: "Groceries", amount: 100 }],
      accounts: [{ accountId: "account-1", name: "Main checking", balance: 900 }],
      trends: {
        spendingDeltaAmount: 60,
        spendingDeltaPercent: 12.5,
        spendingSparkline: [{ month: "2026-04", amount: 540 }]
      },
      currencyCode: "USD",
      warnings: []
    } as DashboardSummary;

    const { rerender } = renderDashboardPage();

    expect(screen.getByRole("heading", { name: "Top categories" })).toBeInTheDocument();
    await user.click(screen.getByRole("checkbox", { name: "Top categories" }));
    expect(screen.queryByRole("heading", { name: "Top categories" })).not.toBeInTheDocument();

    await user.click(within(screen.getByText("Summary metrics").closest("article") as HTMLElement).getByRole("button", { name: "Move down" }));
    expect(screen.getByText("Reports preview").compareDocumentPosition(screen.getByText("Income")) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();

    rerender(dashboardTree());

    expect(screen.getByText("Reports preview").compareDocumentPosition(screen.getByText("Income")) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    const stored = localStorage.getItem("ledgerra:dashboard-widgets:owner@ledgerra.local");
    expect(stored).toContain("\"id\":\"metrics\"");
    expect(stored?.indexOf("\"id\":\"metrics\"")).toBeGreaterThan(stored?.indexOf("\"id\":\"trends\"") ?? -1);
  });

  test("closes and reopens dashboard widget customization without changing widget preferences", async () => {
    const user = userEvent.setup();

    const { rerender } = renderDashboardPage();

    const customizationPanel = screen.getByLabelText("Customize widgets");
    await user.click(within(customizationPanel).getByRole("button", { name: "Close" }));

    expect(screen.queryByLabelText("Customize widgets")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Customize widgets" })).toBeInTheDocument();
    expect(localStorage.getItem("ledgerra:dashboard-widget-customization-closed:owner@ledgerra.local")).toBe("true");
    expect(localStorage.getItem("ledgerra:dashboard-widgets:owner@ledgerra.local")).toBeNull();

    rerender(dashboardTree());

    expect(screen.queryByLabelText("Customize widgets")).not.toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Customize widgets" }));

    expect(screen.getByLabelText("Customize widgets")).toBeInTheDocument();
    expect(localStorage.getItem("ledgerra:dashboard-widget-customization-closed:owner@ledgerra.local")).toBe("false");
  });

  
  test("builds month-scoped drilldown links for top categories and accounts", () => {
    mocks.data.dashboard = {
      income: 4000,
      expenses: 540,
      net: 3460,
      budgetRemaining: 260,
      topCategories: [{ categoryId: "category-1", categoryName: "Groceries", amount: 100 }],
      accounts: [{ accountId: "account-1", name: "Main checking", balance: 900 }],
      trends: {
        spendingDeltaAmount: 60,
        spendingDeltaPercent: 12.5,
        spendingSparkline: []
      },
      currencyCode: "USD",
      warnings: []
    } as DashboardSummary;

    renderDashboardPage("2026-02");

    expect(screen.getByRole("link", { name: "Groceries" })).toHaveAttribute("href", "/transactions?type=Expense&categoryId=category-1&from=2026-02-01&to=2026-02-28");
    expect(screen.getByRole("link", { name: "Main checking" })).toHaveAttribute("href", "/transactions?accountId=account-1&from=2026-02-01&to=2026-02-28");
  });
  test("opens quick transaction dialog and saves a transaction from the dashboard", async () => {
    const user = userEvent.setup();
    mocks.data.accounts = [
      {
        id: "account-1",
        name: "Main checking",
        type: "Checking",
        currencyCode: "USD",
        openingBalance: 500,
        currentBalance: 500,
        isActive: true,
        iconKind: "Bank"
      }
    ];

    renderDashboardPage();

    await user.click(screen.getByRole("button", { name: "Add transaction" }));
    expect(screen.getByRole("dialog", { name: "Add transaction" })).toBeInTheDocument();

    await user.selectOptions(screen.getByLabelText("Account"), "account-1");
    await user.selectOptions(screen.getByLabelText("Category"), "category-1");
    fireEvent.change(screen.getByLabelText("Amount"), { target: { value: "18.75" } });
    fireEvent.change(screen.getByLabelText("Note"), { target: { value: "Lunch" } });
    await user.click(screen.getByRole("button", { name: "Save transaction" }));

    await waitFor(() => {
      expect(mocks.createTransaction).toHaveBeenCalledWith(
        "token",
        expect.objectContaining({
          accountId: "account-1",
          categoryId: "category-1",
          amount: 18.75,
          type: "Expense",
          note: "Lunch"
        })
      );
    });
    expect(mocks.data.refresh).toHaveBeenCalled();
    expect(screen.queryByRole("dialog", { name: "Add transaction" })).not.toBeInTheDocument();
  });

  test("creates a missing category from the dashboard quick transaction dialog", async () => {
    const user = userEvent.setup();
    mocks.data.accounts = [
      {
        id: "account-1",
        name: "Main checking",
        type: "Checking",
        currencyCode: "USD",
        openingBalance: 500,
        currentBalance: 500,
        isActive: true,
        iconKind: "Bank"
      }
    ];

    renderDashboardPage();

    await user.click(screen.getByRole("button", { name: "Add transaction" }));
    await user.selectOptions(screen.getByLabelText("Account"), "account-1");
    await user.selectOptions(screen.getByLabelText("Category"), "__create_new__");
    await user.type(screen.getByLabelText("New category name"), "Pets");
    fireEvent.change(screen.getByLabelText("New category color"), { target: { value: "#ff8800" } });
    fireEvent.change(screen.getByLabelText("Amount"), { target: { value: "31" } });
    await user.click(screen.getByRole("button", { name: "Save transaction" }));

    await waitFor(() => {
      expect(mocks.createCategory).toHaveBeenCalledWith("token", {
        name: "Pets",
        kind: "Expense",
        color: "#ff8800"
      });
    });
    expect(mocks.createTransaction).toHaveBeenCalledWith(
      "token",
      expect.objectContaining({
        categoryId: "category-new",
        amount: 31
      })
    );
  });
});
