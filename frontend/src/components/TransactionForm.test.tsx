import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, test, vi } from "vitest";
import { apiClient } from "../api/client";
import { TransactionForm, toDateTimeLocal } from "./TransactionForm";

vi.mock("../api/client", () => ({
  apiClient: {
    bookkeeping: vi.fn().mockResolvedValue([]),
    createTransaction: vi.fn(),
    updateTransaction: vi.fn(),
    createCategory: vi.fn()
  }
}));

function formatLocalDateTime(value: Date) {
  const year = value.getFullYear();
  const month = String(value.getMonth() + 1).padStart(2, "0");
  const day = String(value.getDate()).padStart(2, "0");
  const hours = String(value.getHours()).padStart(2, "0");
  const minutes = String(value.getMinutes()).padStart(2, "0");

  return `${year}-${month}-${day}T${hours}:${minutes}`;
}

describe("TransactionForm date values", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(apiClient.bookkeeping).mockImplementation(async (_token, path) => (path.endsWith("/details") ? { metadata: { payeeId: null, reference: "bank-ref", tags: "home", paymentMethod: "Card", status: "Cleared" }, splitLines: [{ categoryId: "food", amount: 10 }, { categoryId: "home", amount: 20 }], destinationAccountId: null } : []) as never);
  });
  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

  test("formats existing timestamps for datetime-local inputs using local time", () => {
    const timestamp = "2026-04-10T12:00:00Z";

    expect(toDateTimeLocal(timestamp)).toBe(formatLocalDateTime(new Date(timestamp)));
  });

  test("uses local time for the default datetime-local value", () => {
    const now = new Date("2026-04-10T12:00:00Z");
    vi.useFakeTimers();
    vi.setSystemTime(now);

    render(
      <TransactionForm
        token="token"
        accounts={[]}
        categories={[]}
        mode="create"
        onSaved={() => undefined}
      />
    );

    expect(screen.getByLabelText("Date and time")).toHaveValue(formatLocalDateTime(now));
  });

  test("submits transfer with selected savings goal id", async () => {
    vi.mocked(apiClient.createTransaction).mockResolvedValue({
      id: "t1",
      accountId: "a1",
      amount: 20,
      type: "TransferOut",
      occurredOnUtc: new Date().toISOString()
    });
    render(
      <TransactionForm
        token="token"
        accounts={[
          { id: "a1", name: "Main", type: "Checking", currencyCode: "USD", openingBalance: 0, currentBalance: 0, isActive: true, iconKind: "Bank" },
          { id: "a2", name: "Savings", type: "Savings", currencyCode: "USD", openingBalance: 0, currentBalance: 0, isActive: true, iconKind: "Piggy" }
        ]}
        categories={[]}
        savingsGoals={[{ id: "g1", name: "Trip", targetAmount: 1000, savedAmount: 0, progressPercent: 0 }]}
        mode="create"
        initialValues={{ type: "Transfer", accountId: "a1", destinationAccountId: "a2", amount: "20", occurredOnUtc: "2026-05-10T12:00", savingsGoalId: "g1" }}
        onSaved={() => undefined}
      />
    );
    fireEvent.submit(screen.getByRole("button", { name: "Save transaction" }));
    expect(apiClient.createTransaction).toHaveBeenCalledWith("token", expect.objectContaining({ savingsGoalId: "g1" }));
  });
  test("blocks unbalanced splits and keeps loaded allocations and metadata on edit", async () => {
    vi.mocked(apiClient.updateTransaction).mockResolvedValue({ id: "new", accountId: "a", type: "Expense", amount: 30, occurredOnUtc: "2026-04-10T12:00:00Z" });
    render(<TransactionForm token="token" accounts={[]} categories={[{ id: "food", name: "Food", kind: "Expense", isSystem: false }, { id: "home", name: "Home", kind: "Expense", isSystem: false }]} mode="edit" transactionId="old" initialValues={{ accountId: "a", amount: "30" }} onSaved={() => {}} />);
    await screen.findByLabelText("Split amount 1");
    expect(screen.getByLabelText("Reference")).toHaveValue("bank-ref");
    fireEvent.change(screen.getByLabelText("Split amount 1"), { target: { value: "11" } });
    expect(screen.getByRole("button", { name: "Save changes" })).toBeDisabled();
    fireEvent.change(screen.getByLabelText("Split amount 2"), { target: { value: "19" } });
    fireEvent.submit(screen.getByRole("button", { name: "Save changes" }));
    await waitFor(() => expect(apiClient.updateTransaction).toHaveBeenCalledWith("token", "old", expect.objectContaining({ reference: "bank-ref", tags: "home", status: "Cleared", paymentMethod: "Card", splitLines: [{ categoryId: "food", amount: 11 }, { categoryId: "home", amount: 19 }] })));
  });

  test("does not flatten a split when details cannot be loaded", async () => {
    vi.mocked(apiClient.bookkeeping).mockRejectedValue(new Error("Details unavailable"));
    render(<TransactionForm token="token" accounts={[]} categories={[]} mode="edit" transactionId="old" initialValues={{ accountId: "a", amount: "30" }} onSaved={() => {}} />);
    await screen.findByRole("alert");
    expect(screen.getByRole("button", { name: "Save changes" })).toBeDisabled();
    fireEvent.submit(screen.getByRole("button", { name: "Save changes" }));
    expect(apiClient.updateTransaction).not.toHaveBeenCalled();
  });

});
