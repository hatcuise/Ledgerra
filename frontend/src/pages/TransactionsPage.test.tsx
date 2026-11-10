import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, test, vi } from "vitest";
import { TransactionsPage } from "./TransactionsPage";

const mocks = vi.hoisted(() => ({
  createCategory: vi.fn(),
  createTransaction: vi.fn(),
  updateTransaction: vi.fn(),
  deleteTransaction: vi.fn(),
  moveTransactionAccount: vi.fn(),
  getTransactions: vi.fn(),
  getSavingsGoals: vi.fn(),
  refresh: vi.fn(),
  accounts: [
    { id: "account-1", name: "Checking", type: "Checking", currencyCode: "USD", openingBalance: 0, currentBalance: 0, isActive: true },
    { id: "account-2", name: "Savings", type: "Savings", currencyCode: "USD", openingBalance: 0, currentBalance: 0, isActive: true }
  ],
  categories: [
    { id: "category-1", name: "Groceries", kind: "Expense", isSystem: false },
    { id: "category-2", name: "Salary", kind: "Income", isSystem: false },
    { id: "category-3", name: "Dining", kind: "Expense", isSystem: false }
  ],
  transactions: [
    {
      id: "transaction-1",
      accountId: "account-1",
      categoryId: "category-1",
      amount: 42.17,
      type: "Expense",
      occurredOnUtc: "2026-04-10T12:00:00Z",
      note: "Market"
    },
    {
      id: "transaction-2",
      accountId: "account-2",
      categoryId: "category-2",
      amount: 3000,
      type: "Income",
      occurredOnUtc: "2026-04-01T08:00:00Z",
      note: "Payroll"
    }
  ]
}));

vi.mock("../state/AuthContext", () => ({
  useAuth: () => ({ auth: { accessToken: "token" } })
}));

vi.mock("../api/client", () => ({
  apiClient: {
    bookkeeping: vi.fn((_token: string, path: string) => Promise.resolve(path.endsWith("/details") ? { metadata: { payeeId: null, reference: "", tags: "", status: "Uncleared", paymentMethod: "" }, splitLines: [], destinationAccountId: null } : [])),
    createCategory: mocks.createCategory,
    createTransaction: mocks.createTransaction,
    updateTransaction: mocks.updateTransaction,
    deleteTransaction: mocks.deleteTransaction,
    moveTransactionAccount: mocks.moveTransactionAccount,
    getTransactions: mocks.getTransactions,
    getSavingsGoals: mocks.getSavingsGoals
  }
}));

vi.mock("../hooks/useLedgerraData", () => ({
  useLedgerraData: () => ({
    accounts: mocks.accounts,
    categories: mocks.categories,
    transactions: mocks.transactions,
    budget: {
      totalPlanned: 300,
      totalSpent: 42.17,
      totalRemaining: 257.83,
      categories: [{ categoryId: "category-1", categoryName: "Groceries", planned: 300, spent: 42.17, remaining: 257.83 }]
    },
    refresh: mocks.refresh
  })
}));

describe("TransactionsPage", () => {
  beforeEach(() => {
    cleanup();
    window.history.replaceState(null, "", "/transactions");
    vi.clearAllMocks();
    mocks.getTransactions.mockResolvedValue([
      {
        id: "transaction-1",
        accountId: "account-1",
        categoryId: "category-1",
        amount: 42.17,
        type: "Expense",
        occurredOnUtc: "2026-04-10T12:00:00Z",
        note: "Market"
      },
      {
        id: "transaction-2",
        accountId: "account-2",
        categoryId: "category-2",
        amount: 3000,
        type: "Income",
        occurredOnUtc: "2026-04-01T08:00:00Z",
        note: "Payroll"
      }
    ]);
    mocks.getSavingsGoals.mockResolvedValue([]);
    mocks.createCategory.mockResolvedValue({
      id: "category-new",
      name: "Pets",
      kind: "Expense",
      color: "#ff8800",
      isSystem: false
    });
    mocks.createTransaction.mockResolvedValue({ id: "transaction-3" });
    mocks.updateTransaction.mockResolvedValue({ id: "transaction-4" });
    mocks.deleteTransaction.mockResolvedValue(undefined);
    mocks.moveTransactionAccount.mockResolvedValue({ id: "transaction-1", accountId: "account-2" });
    Object.defineProperty(URL, "createObjectURL", { configurable: true, value: vi.fn() });
    Object.defineProperty(URL, "revokeObjectURL", { configurable: true, value: vi.fn() });
    vi.spyOn(URL, "createObjectURL").mockReturnValue("blob:test");
    vi.spyOn(URL, "revokeObjectURL").mockImplementation(() => {});
  });

  test("loads transactions with filters and searches visible notes", async () => {
    const user = userEvent.setup();

    render(<TransactionsPage />);

    await user.selectOptions(screen.getByLabelText("Filter by account"), ["account-1"]);
    await user.click(screen.getByLabelText("Groceries"));
    await user.click(screen.getByRole("button", { name: "Expense" }));
    fireEvent.change(screen.getByLabelText("From date"), { target: { value: "2026-04-01" } });
    fireEvent.change(screen.getByLabelText("To date"), { target: { value: "2026-04-30" } });
    fireEvent.change(screen.getByLabelText("Min amount"), { target: { value: "10" } });
    fireEvent.change(screen.getByLabelText("Max amount"), { target: { value: "100" } });
    await user.type(screen.getByLabelText("Search notes"), "market");

    await waitFor(() => {
      expect(mocks.getTransactions).toHaveBeenLastCalledWith(
        "token",
        "?accountId=account-1&categoryId=category-1&type=Expense&from=2026-04-01&to=2026-04-30&minAmount=10&maxAmount=100&q=market"
      );
    });

    expect(screen.getByText("Market")).toBeInTheDocument();
    expect(screen.queryByText("Payroll")).not.toBeInTheDocument();
    expect(window.location.search).toContain("accountId=account-1");
    expect(window.location.search).toContain("categoryId=category-1");
    expect(window.location.search).toContain("minAmount=10");
    expect(window.location.search).toContain("maxAmount=100");
    expect(window.location.search).toContain("q=market");
  });

  test("edits a transaction from the ledger", async () => {
    const user = userEvent.setup();

    render(<TransactionsPage />);

    const row = await screen.findByLabelText("Transaction Market");
    expect(within(row).queryByRole("button", { name: "Edit Market" })).not.toBeInTheDocument();
    await user.click(within(row).getByRole("button", { name: "Transaction actions for Market" }));
    await user.click(screen.getByRole("button", { name: "Edit Market" }));

    expect(screen.getByRole("heading", { name: "Edit transaction" })).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText("Amount"), { target: { value: "50.25" } });
    fireEvent.change(screen.getByLabelText("Note"), { target: { value: "Market and pharmacy" } });
    await user.click(screen.getByRole("button", { name: "Save changes" }));

    await waitFor(() => {
      expect(mocks.updateTransaction).toHaveBeenCalledWith(
        "token",
        "transaction-1",
        expect.objectContaining({
          amount: 50.25,
          categoryId: "category-1",
          note: "Market and pharmacy",
          type: "Expense"
        })
      );
    });
  });

  test("deletes and duplicates transactions from row actions", async () => {
    const user = userEvent.setup();

    render(<TransactionsPage />);

    const row = await screen.findByLabelText("Transaction Market");
    expect(within(row).queryByRole("button", { name: "Duplicate Market" })).not.toBeInTheDocument();
    await user.click(within(row).getByRole("button", { name: "Transaction actions for Market" }));
    await user.click(screen.getByRole("button", { name: "Duplicate Market" }));
    await screen.findByText("Review the copy, then save it. Bank reference and cleared state have been reset.");
    expect(mocks.createTransaction).not.toHaveBeenCalled();
    await user.click(screen.getByRole("button", { name: "Save transaction" }));

    await waitFor(() => {
      expect(mocks.createTransaction).toHaveBeenCalledWith(
        "token",
        expect.objectContaining({
          accountId: "account-1",
          categoryId: "category-1",
          amount: 42.17,
          note: "Market",
          type: "Expense"
        })
      );
    });

    await user.click(within(row).getByRole("button", { name: "Transaction actions for Market" }));
    await user.click(screen.getByRole("button", { name: "Delete Market" }));

    await waitFor(() => {
      expect(mocks.deleteTransaction).toHaveBeenCalledWith("token", "transaction-1");
    });
  });

  test("filters uncategorized expenses and assigns a category from the workflow", async () => {
    const user = userEvent.setup();
    mocks.getTransactions.mockResolvedValue([
      {
        id: "transaction-3",
        accountId: "account-1",
        categoryId: null,
        amount: 18.5,
        type: "Expense",
        occurredOnUtc: "2026-04-12T12:00:00Z",
        note: "Cafe"
      },
      {
        id: "transaction-4",
        accountId: "account-1",
        categoryId: "category-1",
        amount: 42.17,
        type: "Expense",
        occurredOnUtc: "2026-04-10T12:00:00Z",
        note: "Market"
      }
    ]);

    render(<TransactionsPage />);

    await user.click(await screen.findByLabelText("Needs category"));

    expect(screen.getByText("Cafe")).toBeInTheDocument();
    expect(screen.queryByText("Market")).not.toBeInTheDocument();
    expect(screen.getByText(/1 uncategorized expense transaction needs review\./)).toBeInTheDocument();

    const row = screen.getByLabelText("Transaction Cafe");
    await user.selectOptions(within(row).getByLabelText("Assign category to Cafe"), "category-3");

    await waitFor(() => {
      expect(mocks.updateTransaction).toHaveBeenCalledWith(
        "token",
        "transaction-3",
        expect.objectContaining({
          categoryId: "category-3",
          amount: 18.5,
          note: "Cafe",
          type: "Expense"
        })
      );
    });
    expect(await screen.findByText("Cafe categorized as Dining.")).toBeInTheDocument();
  });

  test("creates a missing category while saving a transaction", async () => {
    const user = userEvent.setup();

    render(<TransactionsPage />);

    await user.click(screen.getByRole("button", { name: "Add transaction" }));
    await user.selectOptions(screen.getByLabelText("Type"), "Expense");
    await user.selectOptions(screen.getByLabelText("Account"), "account-1");
    await user.selectOptions(screen.getByLabelText("Category"), "__create_new__");
    await user.type(screen.getByLabelText("New category name"), "Pets");
    fireEvent.change(screen.getByLabelText("New category color"), { target: { value: "#ff8800" } });
    fireEvent.change(screen.getByLabelText("Amount"), { target: { value: "24.5" } });
    fireEvent.change(screen.getByLabelText("Note"), { target: { value: "Cat food" } });
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
        accountId: "account-1",
        categoryId: "category-new",
        amount: 24.5,
        type: "Expense",
        note: "Cat food"
      })
    );
  });

  test("hides category creation for transfers", async () => {
    const user = userEvent.setup();

    render(<TransactionsPage />);

    await user.click(screen.getByRole("button", { name: "Add transaction" }));
    await user.selectOptions(screen.getByLabelText("Type"), "Transfer");

    expect(screen.queryByLabelText("Category")).not.toBeInTheDocument();
    expect(screen.queryByLabelText("New category name")).not.toBeInTheDocument();
    expect(screen.getByLabelText("Destination account")).toBeInTheDocument();
  });

  test("explains why category chips are unavailable for transfer filters", async () => {
    const user = userEvent.setup();

    render(<TransactionsPage />);

    await user.click(screen.getByRole("button", { name: "Transfer" }));

    expect(screen.getByText("Transfers use linked account entries, so categories are not applied independently.")).toBeInTheDocument();
    const categoryChips = within(screen.getByLabelText("Filter by category")).getAllByRole("checkbox");
    expect(categoryChips.length).toBeGreaterThan(0);
    categoryChips.forEach((chip) => expect(chip).toBeDisabled());
  });

  test("supports select-all bulk delete, category assignment, and account move", async () => {
    const user = userEvent.setup();
    render(<TransactionsPage />);

    await user.click(await screen.findByLabelText("Select all in current filtered view"));
    expect(screen.queryByRole("button", { name: "Bulk delete" })).not.toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Bulk actions" }));
    await user.click(screen.getByRole("button", { name: "Bulk delete" }));
    await waitFor(() => expect(mocks.deleteTransaction).toHaveBeenCalledTimes(2));

    await user.click(screen.getByLabelText("Select Market"));
    await user.click(screen.getByRole("button", { name: "Bulk actions" }));
    await user.selectOptions(screen.getByLabelText("Bulk category"), "category-3");
    await user.click(screen.getByRole("button", { name: "Apply category" }));
    await waitFor(() => expect(mocks.updateTransaction).toHaveBeenCalledWith("token", "transaction-1", expect.objectContaining({ categoryId: "category-3" })));

    await user.click(screen.getByLabelText("Select Market"));
    await user.click(screen.getByRole("button", { name: "Bulk actions" }));
    await user.selectOptions(screen.getByLabelText("Move to account"), "account-2");
    await user.click(screen.getByRole("button", { name: "Move transactions" }));
    await waitFor(() => expect(mocks.moveTransactionAccount).toHaveBeenCalledWith("token", "transaction-1", "account-2"));
  });

  test("deduplicates transfer group deletes in bulk actions", async () => {
    const user = userEvent.setup();
    mocks.getTransactions.mockResolvedValue([
      {
        id: "transfer-out",
        accountId: "account-1",
        categoryId: null,
        amount: 100,
        type: "TransferOut",
        occurredOnUtc: "2026-04-15T12:00:00Z",
        note: "Savings move",
        transferGroupId: "transfer-group-1"
      },
      {
        id: "transfer-in",
        accountId: "account-2",
        categoryId: null,
        amount: 100,
        type: "TransferIn",
        occurredOnUtc: "2026-04-15T12:00:00Z",
        note: "Savings move",
        transferGroupId: "transfer-group-1"
      }
    ]);

    render(<TransactionsPage />);

    await user.click(await screen.findByLabelText("Select all in current filtered view"));
    await user.click(screen.getByRole("button", { name: "Bulk actions" }));
    await user.click(screen.getByRole("button", { name: "Bulk delete" }));

    await waitFor(() => expect(mocks.deleteTransaction).toHaveBeenCalledTimes(1));
    expect(mocks.deleteTransaction).toHaveBeenCalledWith("token", "transfer-out");
  });

  test("limits bulk category assignment to one transaction kind", async () => {
    const user = userEvent.setup();
    render(<TransactionsPage />);

    await user.click(await screen.findByLabelText("Select all in current filtered view"));
    await user.click(screen.getByRole("button", { name: "Bulk actions" }));

    expect(screen.getByRole("button", { name: "Apply category" })).toBeDisabled();
    expect(screen.getByLabelText("Bulk category")).toBeDisabled();

    await user.click(screen.getByLabelText("Select Market"));
    await user.click(screen.getByRole("button", { name: "Bulk actions" }));
    await user.selectOptions(screen.getByLabelText("Bulk category"), "category-2");
    await user.click(screen.getByRole("button", { name: "Apply category" }));

    await waitFor(() => {
      expect(mocks.updateTransaction).toHaveBeenCalledWith("token", "transaction-2", expect.objectContaining({ categoryId: "category-2" }));
    });
    expect(mocks.updateTransaction).not.toHaveBeenCalledWith("token", "transaction-1", expect.anything());
  });

  test("exports filtered transactions and categories/budget csv files", async () => {
    const user = userEvent.setup();
    const appendSpy = vi.spyOn(document.body, "append");
    const clickSpy = vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(() => {});

    render(<TransactionsPage />);

    expect(screen.queryByRole("button", { name: "Export CSV" })).not.toBeInTheDocument();
    await user.click(await screen.findByRole("button", { name: "Export and views" }));
    await user.click(screen.getByRole("button", { name: "Export CSV" }));
    await user.click(screen.getByRole("button", { name: "Export categories & budget" }));

    expect(URL.createObjectURL).toHaveBeenCalledTimes(3);
    expect(clickSpy).toHaveBeenCalledTimes(3);
    expect(appendSpy).toHaveBeenCalledTimes(3);
    expect(await screen.findByText("Exported categories and budget data.")).toBeInTheDocument();
  });
  test("opens the exact transaction linked by a spending alert and can show the full view", async () => {
    window.history.replaceState(null, "", "/transactions?entry=transaction-1"); render(<TransactionsPage />);
    expect(await screen.findByLabelText("Transaction Market")).toBeInTheDocument(); expect(screen.queryByLabelText("Transaction Payroll")).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Show all matching transactions" })); expect(await screen.findByLabelText("Transaction Payroll")).toBeInTheDocument();
  });

});
