import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { beforeEach, describe, expect, test, vi } from "vitest";
import { ImportAnalysisProvider } from "../state/ImportAnalysisContext";
import { ImportsPage } from "./ImportsPage";

const mocks = vi.hoisted(() => ({
  analyzeMonthlyReport: vi.fn(),
  retryMonthlyReportAnalysisParse: vi.fn(),
  downloadMonthlyReportAnalysisRawOutput: vi.fn(),
  commitMonthlyReportDrafts: vi.fn(),
  createImportRule: vi.fn(),
  refresh: vi.fn()
}));

vi.mock("../state/AuthContext", () => ({
  useAuth: () => ({ auth: { accessToken: "token" } })
}));

vi.mock("../api/client", () => ({
  apiClient: {
    analyzeMonthlyReport: mocks.analyzeMonthlyReport,
    retryMonthlyReportAnalysisParse: mocks.retryMonthlyReportAnalysisParse,
    downloadMonthlyReportAnalysisRawOutput: mocks.downloadMonthlyReportAnalysisRawOutput,
    getImportProfiles: vi.fn().mockResolvedValue([]),
    previewCsvBankImport: mocks.analyzeMonthlyReport,
    commitMonthlyReportDrafts: mocks.commitMonthlyReportDrafts,
    createImportRule: mocks.createImportRule
  }
}));

vi.mock("../hooks/useLedgerraData", () => ({
  useLedgerraData: () => ({
    accounts: [{ id: "account-1", name: "Checking", type: "Checking", currencyCode: "USD", openingBalance: 0, currentBalance: 0, isActive: true }],
    categories: [
      { id: "category-1", name: "Groceries", kind: "Expense", isSystem: false },
      { id: "category-2", name: "Dining", kind: "Expense", isSystem: false }
    ],
    aiSettings: {
      providers: {
        openAi: { isConfigured: true, maskedKey: "...3456" },
        anthropic: { isConfigured: true, maskedKey: "...abcd" },
        openAiCompatible: { isConfigured: true, maskedKey: "...cdef", baseUrl: "https://api.synthetic.example/v1", model: "synthetic-finance-1" }
      },
      defaultProvider: "OpenAi"
    },
    refresh: mocks.refresh
  })
}));

function renderImportsPage() {
  return render(
    <MemoryRouter><ImportAnalysisProvider>
      <ImportsPage />
    </ImportAnalysisProvider></MemoryRouter>
  );
}

describe("ImportsPage", () => {
  beforeEach(() => {
    cleanup();
    vi.clearAllMocks();
  });

  test("renders monthly report import controls", () => {
    renderImportsPage();

    expect(screen.getByText("Monthly report import")).toBeInTheDocument();
    expect(screen.getByLabelText("Account")).toBeInTheDocument();
    expect(screen.getByLabelText("Report file")).toBeInTheDocument();
  });

  test("surfaces analysis errors and re-enables the submit button", async () => {
    let rejectAnalysis: (error: Error) => void = () => undefined;
    mocks.analyzeMonthlyReport.mockReturnValue(new Promise((_, reject) => {
      rejectAnalysis = reject;
    }));

    renderImportsPage();

    fireEvent.change(screen.getByLabelText("Account"), { target: { value: "account-1" } });
    fireEvent.change(screen.getByLabelText("Report file"), {
      target: { files: [new File(["report"], "report.pdf", { type: "application/pdf" })] }
    });
    fireEvent.submit(screen.getByRole("button", { name: "Analyze report" }).closest("form")!);

    expect(screen.getByRole("button", { name: "Analyzing..." })).toBeDisabled();
    rejectAnalysis(new Error("Provider key is invalid"));
    expect(await screen.findByText("Provider key is invalid")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Analyze report" })).toBeEnabled();
  });

  test("shows elapsed waiting status while AI analysis is still running", async () => {
    vi.useFakeTimers();
    let restoredTimers = false;
    try {
      let rejectAnalysis: (error: Error) => void = () => undefined;
      mocks.analyzeMonthlyReport.mockReturnValue(new Promise((_, reject) => {
        rejectAnalysis = reject;
      }));

      renderImportsPage();

      fireEvent.change(screen.getByLabelText("Account"), { target: { value: "account-1" } });
      fireEvent.change(screen.getByLabelText("Report file"), {
        target: { files: [new File(["report"], "report.pdf", { type: "application/pdf" })] }
      });
      fireEvent.submit(screen.getByRole("button", { name: "Analyze report" }).closest("form")!);

      await vi.advanceTimersByTimeAsync(15_000);

      expect(screen.getByText(/Still waiting for the AI provider/)).toBeInTheDocument();
      expect(screen.getByText(/Elapsed: 15s/)).toBeInTheDocument();

      rejectAnalysis(new Error("Provider timed out"));
      vi.useRealTimers();
      restoredTimers = true;
      await screen.findByText("Provider timed out");
    } finally {
      if (!restoredTimers) {
        vi.useRealTimers();
      }
    }
  });

  test("shows AI provider progress details while analysis is running", async () => {
    vi.useFakeTimers();
    let restoredTimers = false;
    try {
      let rejectAnalysis: (error: Error) => void = () => undefined;
      mocks.analyzeMonthlyReport.mockImplementation((_token, _payload, onJobUpdate) => {
        onJobUpdate?.({
          jobId: "job-1",
          status: "running",
          statusMessage: "AI provider is streaming JSON output.",
          generatedOutputCharacters: 128,
          usage: { promptTokens: 100, completionTokens: 30, totalTokens: 130 },
          analysis: null,
          error: null,
          createdAtUtc: "2026-05-23T00:00:00Z",
          updatedAtUtc: "2026-05-23T00:00:01Z"
        });
        return new Promise((_, reject) => {
          rejectAnalysis = reject;
        });
      });

      renderImportsPage();

      fireEvent.change(screen.getByLabelText("Account"), { target: { value: "account-1" } });
      fireEvent.change(screen.getByLabelText("Report file"), {
        target: { files: [new File(["report"], "report.pdf", { type: "application/pdf" })] }
      });
      fireEvent.submit(screen.getByRole("button", { name: "Analyze report" }).closest("form")!);

      await vi.advanceTimersByTimeAsync(10_000);

      expect(screen.getByText(/AI provider is streaming JSON output/)).toBeInTheDocument();
      expect(screen.getByText(/Generated output: 128 chars/)).toBeInTheDocument();
      expect(screen.getByText(/Tokens: 130 total/)).toBeInTheDocument();

      rejectAnalysis(new Error("Provider timed out"));
      vi.useRealTimers();
      restoredTimers = true;
      await screen.findByText("Provider timed out");
    } finally {
      if (!restoredTimers) {
        vi.useRealTimers();
      }
    }
  });

  test("keeps AI analysis status and completed drafts visible across view changes", async () => {
    const user = userEvent.setup();
    let resolveAnalysis: (analysis: {
      warnings: string[];
      transactions: Array<{
        sourceId: string;
        accountId: string;
        categoryId: string;
        amount: number;
        type: string;
        occurredOnUtc: string;
        note: string;
        confidence: number;
        warnings: string[];
        isSelectedByDefault: boolean;
      }>;
    }) => void = () => undefined;
    mocks.analyzeMonthlyReport.mockImplementation((_token, _payload, onJobUpdate) => {
      onJobUpdate?.({
        jobId: "job-1",
        status: "running",
        statusMessage: "AI provider is streaming JSON output.",
        generatedOutputCharacters: 128,
        usage: null,
        analysis: null,
        error: null,
        createdAtUtc: "2026-05-23T00:00:00Z",
        updatedAtUtc: "2026-05-23T00:00:01Z"
      });

      return new Promise((resolve) => {
        resolveAnalysis = resolve;
      });
    });

    function Harness() {
      const [showImports, setShowImports] = useState(true);

      return (
        <ImportAnalysisProvider>
          <button type="button" onClick={() => setShowImports((current) => !current)}>
            Change view
          </button>
          {showImports ? <ImportsPage /> : <div>Dashboard view</div>}
        </ImportAnalysisProvider>
      );
    }

    render(<MemoryRouter><Harness /></MemoryRouter>);

    fireEvent.change(screen.getByLabelText("Account"), { target: { value: "account-1" } });
    fireEvent.change(screen.getByLabelText("Report file"), {
      target: { files: [new File(["report"], "report.pdf", { type: "application/pdf" })] }
    });
    fireEvent.submit(screen.getByRole("button", { name: "Analyze report" }).closest("form")!);

    expect(await screen.findByText(/AI provider is streaming JSON output/)).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Change view" }));
    expect(screen.getByText("Dashboard view")).toBeInTheDocument();

    resolveAnalysis({
      warnings: [],
      transactions: [
        {
          sourceId: "row-1",
          accountId: "account-1",
          categoryId: "category-1",
          amount: 42.17,
          type: "Expense",
          occurredOnUtc: "2026-04-10T12:00:00Z",
          note: "Market",
          confidence: 0.9,
          warnings: [],
          isSelectedByDefault: true
        }
      ]
    });

    await user.click(screen.getByRole("button", { name: "Change view" }));

    expect(await screen.findByText(/AI provider is streaming JSON output/)).toBeInTheDocument();
    expect(await screen.findByDisplayValue("Market")).toBeInTheDocument();
    expect(screen.getByLabelText("Select row-1")).toBeChecked();
  });

  test("retries saved AI output after a parse failure", async () => {
    const user = userEvent.setup();
    mocks.analyzeMonthlyReport.mockImplementation((_token, _payload, onJobUpdate) => {
      onJobUpdate?.({
        jobId: "job-1",
        status: "failed",
        statusMessage: "Analysis failed while parsing AI output.",
        generatedOutputCharacters: 300,
        usage: { promptTokens: 100, completionTokens: 80, totalTokens: 180 },
        analysis: null,
        error: "AI returned analysis JSON that could not be parsed.",
        hasRawAiOutput: true,
        createdAtUtc: "2026-05-23T00:00:00Z",
        updatedAtUtc: "2026-05-23T00:00:01Z"
      });
      return Promise.reject(new Error("AI returned analysis JSON that could not be parsed."));
    });
    mocks.retryMonthlyReportAnalysisParse.mockResolvedValue({
      warnings: [],
      transactions: [
        {
          sourceId: "statement-2026-004-001",
          accountId: "account-1",
          categoryId: "category-1",
          amount: 10,
          type: "Expense",
          occurredOnUtc: "2026-04-10T12:00:00Z",
          note: "Recovered row",
          confidence: 0.9,
          warnings: [],
          isLikelyDuplicate: false,
          isSelectedByDefault: true
        }
      ]
    });

    renderImportsPage();

    fireEvent.change(screen.getByLabelText("Account"), { target: { value: "account-1" } });
    fireEvent.change(screen.getByLabelText("Report file"), {
      target: { files: [new File(["report"], "report.pdf", { type: "application/pdf" })] }
    });
    fireEvent.submit(screen.getByRole("button", { name: "Analyze report" }).closest("form")!);

    expect(await screen.findByRole("button", { name: "Retry saved AI output" })).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Retry saved AI output" }));

    expect(mocks.retryMonthlyReportAnalysisParse).toHaveBeenCalledWith("token", "job-1", expect.any(Function));
    expect(await screen.findByDisplayValue("Recovered row")).toBeInTheDocument();
  });

  test("downloads saved AI output after a parse failure", async () => {
    const user = userEvent.setup();
    const click = vi.fn();
    const appendChild = vi.spyOn(document.body, "appendChild");
    const removeChild = vi.spyOn(document.body, "removeChild");
    const originalCreateElement = document.createElement.bind(document);
    const createElement = vi.spyOn(document, "createElement");
    const originalCreateObjectURL = URL.createObjectURL;
    const originalRevokeObjectURL = URL.revokeObjectURL;
    Object.defineProperty(URL, "createObjectURL", { configurable: true, value: vi.fn(() => "blob:raw-output") });
    Object.defineProperty(URL, "revokeObjectURL", { configurable: true, value: vi.fn() });
    try {
      createElement.mockImplementation(((tagName: string, options?: ElementCreationOptions) => {
        const element = originalCreateElement(tagName, options);
        if (tagName.toLowerCase() === "a") {
          Object.defineProperty(element, "click", { value: click });
        }

        return element;
      }) as typeof document.createElement);
      mocks.analyzeMonthlyReport.mockImplementation((_token, _payload, onJobUpdate) => {
        onJobUpdate?.({
          jobId: "job-1",
          status: "failed",
          statusMessage: "Analysis failed while parsing AI output.",
          generatedOutputCharacters: 300,
          usage: { promptTokens: 100, completionTokens: 80, totalTokens: 180 },
          analysis: null,
          error: "AI returned analysis JSON that could not be parsed.",
          hasRawAiOutput: true,
          createdAtUtc: "2026-05-23T00:00:00Z",
          updatedAtUtc: "2026-05-23T00:00:01Z"
        });
        return Promise.reject(new Error("AI returned analysis JSON that could not be parsed."));
      });
      mocks.downloadMonthlyReportAnalysisRawOutput.mockResolvedValue({
        blob: new Blob(['{"transactions":[]}'], { type: "application/json" }),
        filename: "ledgerra-ai-output-job-1.json"
      });

      renderImportsPage();

      fireEvent.change(screen.getByLabelText("Account"), { target: { value: "account-1" } });
      fireEvent.change(screen.getByLabelText("Report file"), {
        target: { files: [new File(["report"], "report.pdf", { type: "application/pdf" })] }
      });
      fireEvent.submit(screen.getByRole("button", { name: "Analyze report" }).closest("form")!);

      expect(await screen.findByRole("button", { name: "Download saved AI output" })).toBeInTheDocument();
      await user.click(screen.getByRole("button", { name: "Download saved AI output" }));

      expect(mocks.downloadMonthlyReportAnalysisRawOutput).toHaveBeenCalledWith("token", "job-1");
      expect(URL.createObjectURL).toHaveBeenCalledWith(expect.any(Blob));
      expect(appendChild).toHaveBeenCalled();
      expect(click).toHaveBeenCalled();
      expect(removeChild).toHaveBeenCalled();
      expect(URL.revokeObjectURL).toHaveBeenCalledWith("blob:raw-output");
    } finally {
      appendChild.mockRestore();
      removeChild.mockRestore();
      createElement.mockRestore();
      Object.defineProperty(URL, "createObjectURL", { configurable: true, value: originalCreateObjectURL });
      Object.defineProperty(URL, "revokeObjectURL", { configurable: true, value: originalRevokeObjectURL });
    }
  });

  test("sends the selected CSV amount inversion and resets it for a new file", async () => {
    renderImportsPage();
    fireEvent.change(screen.getByLabelText("Account"), { target: { value: "account-1" } });
    fireEvent.change(screen.getByLabelText("Report file"), {
      target: { files: [new File(["date,amount\n3/7/2026,133"], "card.csv", { type: "text/csv" })] }
    });
    const invert = await screen.findByLabelText("Invert amounts");
    expect(invert).not.toBeChecked();
    fireEvent.click(invert);
    fireEvent.submit(screen.getByRole("button", { name: "Analyze report" }).closest("form")!);
    await waitFor(() => expect(mocks.analyzeMonthlyReport).toHaveBeenCalledWith("token", expect.objectContaining({ invertAmounts: true })));
    fireEvent.change(screen.getByLabelText("Report file"), {
      target: { files: [new File(["date,amount\n3/7/2026,25"], "bank.csv", { type: "text/csv" })] }
    });
    expect(await screen.findByLabelText("Invert amounts")).not.toBeChecked();
  });

  test("keeps edited draft date and amount values safe before commit", async () => {
    const user = userEvent.setup();
    mocks.analyzeMonthlyReport.mockResolvedValue({
      warnings: [],
      transactions: [
        {
          sourceId: "row-1",
          accountId: "account-1",
          categoryId: "category-1",
          amount: 42.17,
          type: "Expense",
          occurredOnUtc: "2026-04-10T12:00:00Z",
          note: "Market",
          confidence: 0.9,
          warnings: []
        }
      ]
    });
    mocks.commitMonthlyReportDrafts.mockResolvedValue({ created: [] });

    renderImportsPage();

    fireEvent.change(screen.getByLabelText("Account"), { target: { value: "account-1" } });
    fireEvent.change(screen.getByLabelText("Report file"), {
      target: { files: [new File(["a,b"], "report.csv", { type: "text/csv" })] }
    });
    fireEvent.submit(screen.getByRole("button", { name: "Analyze report" }).closest("form")!);

    await screen.findByText("90%");

    expect(screen.getByLabelText("Date for row-1")).toHaveValue("10-04-2026");
    expect(screen.getByLabelText("Amount for row-1")).toHaveValue(42.17);

    fireEvent.change(screen.getByLabelText("Date for row-1"), { target: { value: "11-04-2026" } });
    fireEvent.change(screen.getByLabelText("Amount for row-1"), { target: { value: "abc" } });
    await user.click(screen.getByRole("button", { name: "Save selected drafts" }));

    await waitFor(() => {
      expect(mocks.commitMonthlyReportDrafts).toHaveBeenCalled();
    });

    const [, submittedDrafts] = mocks.commitMonthlyReportDrafts.mock.calls[0];
    expect(Number.isFinite(submittedDrafts[0].amount)).toBe(true);
    expect(submittedDrafts[0].occurredOnUtc).toBe("2026-04-11T00:00:00.000Z");
  });

  test("renders duplicate and rule metadata with duplicate rows unselected by default", async () => {
    mocks.analyzeMonthlyReport.mockResolvedValue({
      warnings: [],
      transactions: [
        {
          sourceId: "row-1",
          accountId: "account-1",
          categoryId: "category-1",
          amount: 42.17,
          type: "Expense",
          occurredOnUtc: "2026-04-10T12:00:00Z",
          note: "Imported: Market",
          confidence: 0.92,
          warnings: [],
          appliedRuleId: "rule-1",
          appliedRuleName: "Market groceries",
          isLikelyDuplicate: true,
          duplicateTransactionId: "transaction-1",
          duplicateReason: "Matches an existing transaction with the same account, date, type, amount, and note.",
          isSelectedByDefault: false
        }
      ]
    });

    renderImportsPage();

    fireEvent.change(screen.getByLabelText("Account"), { target: { value: "account-1" } });
    fireEvent.change(screen.getByLabelText("Report file"), {
      target: { files: [new File(["a,b"], "report.csv", { type: "text/csv" })] }
    });
    fireEvent.submit(screen.getByRole("button", { name: "Analyze report" }).closest("form")!);

    expect(await screen.findByText("Market groceries")).toBeInTheDocument();
    expect(screen.getAllByText(/duplicate/i).length).toBeGreaterThan(0);
    expect(screen.getByLabelText("Select row-1")).not.toBeChecked();
  });

  test("sends accepted duplicate source ids for duplicate rows selected by the user", async () => {
    const user = userEvent.setup();
    mocks.analyzeMonthlyReport.mockResolvedValue({
      warnings: [],
      transactions: [
        {
          sourceId: "row-1",
          accountId: "account-1",
          categoryId: "category-1",
          amount: 42.17,
          type: "Expense",
          occurredOnUtc: "2026-04-10T12:00:00Z",
          note: "Imported: Market",
          confidence: 0.92,
          warnings: [],
          appliedRuleId: null,
          appliedRuleName: null,
          isLikelyDuplicate: true,
          duplicateTransactionId: "transaction-1",
          duplicateReason: "Matches an existing transaction.",
          isSelectedByDefault: false
        }
      ]
    });
    mocks.commitMonthlyReportDrafts.mockResolvedValue({ created: [] });

    renderImportsPage();

    fireEvent.change(screen.getByLabelText("Account"), { target: { value: "account-1" } });
    fireEvent.change(screen.getByLabelText("Report file"), {
      target: { files: [new File(["a,b"], "report.csv", { type: "text/csv" })] }
    });
    fireEvent.submit(screen.getByRole("button", { name: "Analyze report" }).closest("form")!);

    await screen.findByText("92%");
    await user.click(screen.getByLabelText("Select row-1"));
    await user.click(screen.getByRole("button", { name: "Save selected drafts" }));

    await waitFor(() => {
      expect(mocks.commitMonthlyReportDrafts).toHaveBeenCalledWith("token", expect.any(Array), ["row-1"]);
    });
  });

  test("sends accepted duplicate source ids for duplicate rows selected by default", async () => {
    const user = userEvent.setup();
    mocks.analyzeMonthlyReport.mockResolvedValue({
      warnings: [],
      transactions: [
        {
          sourceId: "row-1",
          accountId: "account-1",
          categoryId: "category-1",
          amount: 42.17,
          type: "Expense",
          occurredOnUtc: "2026-04-10T12:00:00Z",
          note: "Imported: Market",
          confidence: 0.92,
          warnings: [],
          isLikelyDuplicate: true,
          duplicateTransactionId: "transaction-1",
          duplicateReason: "Matches an existing transaction.",
          isSelectedByDefault: true
        }
      ]
    });
    mocks.commitMonthlyReportDrafts.mockResolvedValue({ created: [] });

    renderImportsPage();

    fireEvent.change(screen.getByLabelText("Account"), { target: { value: "account-1" } });
    fireEvent.change(screen.getByLabelText("Report file"), {
      target: { files: [new File(["a,b"], "report.csv", { type: "text/csv" })] }
    });
    fireEvent.submit(screen.getByRole("button", { name: "Analyze report" }).closest("form")!);

    await screen.findByText("92%");
    expect(screen.getByLabelText("Select row-1")).toBeChecked();
    await user.click(screen.getByRole("button", { name: "Save selected drafts" }));

    await waitFor(() => {
      expect(mocks.commitMonthlyReportDrafts).toHaveBeenCalledWith("token", expect.any(Array), ["row-1"]);
    });
  });

  test("does not commit when no drafts are selected", async () => {
    const user = userEvent.setup();
    mocks.analyzeMonthlyReport.mockResolvedValue({
      warnings: [],
      transactions: [
        {
          sourceId: "row-1",
          accountId: "account-1",
          categoryId: "category-1",
          amount: 42.17,
          type: "Expense",
          occurredOnUtc: "2026-04-10T12:00:00Z",
          note: "Imported: Market",
          confidence: 0.92,
          warnings: [],
          isLikelyDuplicate: true,
          duplicateTransactionId: "transaction-1",
          duplicateReason: "Matches an existing transaction.",
          isSelectedByDefault: false
        }
      ]
    });

    renderImportsPage();

    fireEvent.change(screen.getByLabelText("Account"), { target: { value: "account-1" } });
    fireEvent.change(screen.getByLabelText("Report file"), {
      target: { files: [new File(["a,b"], "report.csv", { type: "text/csv" })] }
    });
    fireEvent.submit(screen.getByRole("button", { name: "Analyze report" }).closest("form")!);

    await screen.findByText("92%");
    await user.click(screen.getByRole("button", { name: "Save selected drafts" }));

    expect(mocks.commitMonthlyReportDrafts).not.toHaveBeenCalled();
    expect(await screen.findByText("Select at least one draft to save.")).toBeInTheDocument();
  });

  test("surfaces commit errors without clearing drafts", async () => {
    const user = userEvent.setup();
    mocks.analyzeMonthlyReport.mockResolvedValue({
      warnings: [],
      transactions: [
        {
          sourceId: "row-1",
          accountId: "account-1",
          categoryId: "category-1",
          amount: 42.17,
          type: "Expense",
          occurredOnUtc: "2026-04-10T12:00:00Z",
          note: "Imported: Market",
          confidence: 0.92,
          warnings: []
        }
      ]
    });
    mocks.commitMonthlyReportDrafts.mockRejectedValue(new Error("Draft row-1 appears to duplicate an existing transaction."));

    renderImportsPage();

    fireEvent.change(screen.getByLabelText("Account"), { target: { value: "account-1" } });
    fireEvent.change(screen.getByLabelText("Report file"), {
      target: { files: [new File(["a,b"], "report.csv", { type: "text/csv" })] }
    });
    fireEvent.submit(screen.getByRole("button", { name: "Analyze report" }).closest("form")!);

    await screen.findByText("92%");
    await user.click(screen.getByRole("button", { name: "Save selected drafts" }));

    expect(await screen.findByText("Draft row-1 appears to duplicate an existing transaction.")).toBeInTheDocument();
    expect(screen.getByLabelText("Select row-1")).toBeInTheDocument();
  });

  test("creates an import rule from a reviewed draft", async () => {
    const user = userEvent.setup();
    mocks.analyzeMonthlyReport.mockResolvedValue({
      warnings: [],
      transactions: [
        {
          sourceId: "row-1",
          accountId: "account-1",
          categoryId: "category-1",
          amount: 42.17,
          type: "Expense",
          occurredOnUtc: "2026-04-10T12:00:00Z",
          note: "Imported: Market",
          confidence: 0.92,
          warnings: []
        }
      ]
    });
    mocks.createImportRule.mockResolvedValue({ id: "rule-1" });

    renderImportsPage();

    fireEvent.change(screen.getByLabelText("Account"), { target: { value: "account-1" } });
    fireEvent.change(screen.getByLabelText("Report file"), {
      target: { files: [new File(["a,b"], "report.csv", { type: "text/csv" })] }
    });
    fireEvent.submit(screen.getByRole("button", { name: "Analyze report" }).closest("form")!);

    await user.click(await screen.findByRole("button", { name: "Remember this" }));

    expect(mocks.createImportRule).toHaveBeenCalledWith("token", {
      name: "Imported: Market -> Groceries",
      matchField: "Note",
      matchOperator: "Contains",
      matchValue: "Imported: Market",
      assignCategoryId: "category-1",
      assignTransactionType: "Expense",
      priority: 100,
      isActive: true
    });
    expect(mocks.refresh).toHaveBeenCalledTimes(1);
    expect(await screen.findByText("Import rule saved.")).toBeInTheDocument();
  });

  test("surfaces import rule creation errors", async () => {
    const user = userEvent.setup();
    mocks.analyzeMonthlyReport.mockResolvedValue({
      warnings: [],
      transactions: [
        {
          sourceId: "row-1",
          accountId: "account-1",
          categoryId: "category-1",
          amount: 42.17,
          type: "Expense",
          occurredOnUtc: "2026-04-10T12:00:00Z",
          note: "Imported: Market",
          confidence: 0.92,
          warnings: []
        }
      ]
    });
    mocks.createImportRule.mockRejectedValue(new Error("Rule name already exists."));

    renderImportsPage();

    fireEvent.change(screen.getByLabelText("Account"), { target: { value: "account-1" } });
    fireEvent.change(screen.getByLabelText("Report file"), {
      target: { files: [new File(["a,b"], "report.csv", { type: "text/csv" })] }
    });
    fireEvent.submit(screen.getByRole("button", { name: "Analyze report" }).closest("form")!);

    await user.click(await screen.findByRole("button", { name: "Remember this" }));

    expect(await screen.findByText("Rule name already exists.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Remember this" })).toBeEnabled();
  });

  test("bulk-selects safe drafts, hides duplicates, and applies a category to selected rows", async () => {
    const user = userEvent.setup();
    mocks.analyzeMonthlyReport.mockResolvedValue({
      warnings: [],
      transactions: [
        {
          sourceId: "row-1",
          accountId: "account-1",
          categoryId: null,
          amount: 18.5,
          type: "Expense",
          occurredOnUtc: "2026-04-11T12:00:00Z",
          note: "Cafe",
          confidence: 0.91,
          warnings: []
        },
        {
          sourceId: "row-2",
          accountId: "account-1",
          categoryId: null,
          amount: 42.17,
          type: "Expense",
          occurredOnUtc: "2026-04-10T12:00:00Z",
          note: "Existing market",
          confidence: 0.95,
          warnings: [],
          isLikelyDuplicate: true,
          duplicateTransactionId: "transaction-1",
          duplicateReason: "Matches an existing transaction.",
          isSelectedByDefault: false
        }
      ]
    });
    mocks.commitMonthlyReportDrafts.mockResolvedValue({ created: [] });

    renderImportsPage();

    fireEvent.change(screen.getByLabelText("Account"), { target: { value: "account-1" } });
    fireEvent.change(screen.getByLabelText("Report file"), {
      target: { files: [new File(["a,b"], "report.csv", { type: "text/csv" })] }
    });
    fireEvent.submit(screen.getByRole("button", { name: "Analyze report" }).closest("form")!);

    await screen.findByDisplayValue("Cafe");
    expect(screen.queryByRole("button", { name: "Select safe drafts" })).not.toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Review actions" }));
    await user.click(screen.getByRole("button", { name: "Select safe drafts" }));
    expect(screen.getByLabelText("Select row-1")).toBeChecked();
    expect(screen.getByLabelText("Select row-2")).not.toBeChecked();

    await user.selectOptions(screen.getByLabelText("Bulk category"), "category-2");
    await user.click(screen.getByRole("button", { name: "Apply to selected" }));
    expect(await screen.findByText("Applied Dining to 1 selected draft.")).toBeInTheDocument();

    await user.click(screen.getByLabelText("Hide duplicates"));
    expect(screen.queryByText("Existing market")).not.toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Save selected drafts" }));

    await waitFor(() => {
      expect(mocks.commitMonthlyReportDrafts).toHaveBeenCalled();
    });
    const [, submittedDrafts] = mocks.commitMonthlyReportDrafts.mock.calls[0];
    expect(submittedDrafts).toHaveLength(1);
    expect(submittedDrafts[0]).toEqual(expect.objectContaining({ sourceId: "row-1", categoryId: "category-2" }));
  });

  test("creates import rules for selected reviewed drafts", async () => {
    const user = userEvent.setup();
    mocks.analyzeMonthlyReport.mockResolvedValue({
      warnings: [],
      transactions: [
        {
          sourceId: "row-1",
          accountId: "account-1",
          categoryId: "category-2",
          amount: 18.5,
          type: "Expense",
          occurredOnUtc: "2026-04-11T12:00:00Z",
          note: "Cafe",
          confidence: 0.91,
          warnings: []
        }
      ]
    });
    mocks.createImportRule.mockResolvedValue({ id: "rule-1" });

    renderImportsPage();

    fireEvent.change(screen.getByLabelText("Account"), { target: { value: "account-1" } });
    fireEvent.change(screen.getByLabelText("Report file"), {
      target: { files: [new File(["a,b"], "report.csv", { type: "text/csv" })] }
    });
    fireEvent.submit(screen.getByRole("button", { name: "Analyze report" }).closest("form")!);

    await screen.findByDisplayValue("Cafe");
    await user.click(screen.getByRole("button", { name: "Review actions" }));
    await user.click(screen.getByRole("button", { name: "Remember selected rules" }));

    await waitFor(() => {
      expect(mocks.createImportRule).toHaveBeenCalledWith("token", {
        name: "Cafe -> Dining",
        matchField: "Note",
        matchOperator: "Contains",
        matchValue: "Cafe",
        assignCategoryId: "category-2",
        assignTransactionType: "Expense",
        priority: 100,
        isActive: true
      });
    });
    expect(mocks.refresh).toHaveBeenCalledTimes(1);
    expect(await screen.findByText("Saved 1 import rule.")).toBeInTheDocument();
  });
});
