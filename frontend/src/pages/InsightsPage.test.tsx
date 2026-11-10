import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, test, vi } from "vitest";
import { MemoryRouter } from "react-router-dom";
import { MonthProvider, useMonthSelection } from "../state/MonthContext";
import { InsightsPage } from "./InsightsPage";
import { insightAlert, insightPreferences, spendingInsights } from "../test/insightFixtures";
const mocks = vi.hoisted(() => ({ insights: vi.fn() }));
vi.mock("../api/client", () => ({ apiClient: { insights: mocks.insights } }));
vi.mock("../state/AuthContext", () => ({ useAuth: () => ({ auth: { accessToken: "token" } }) }));
vi.mock("../components/SpendingPaceChart", () => ({ SpendingPaceChart: () => <div>Spending chart</div> }));
function RenderControls() { const { goToNextMonth } = useMonthSelection(); return <><button onClick={goToNextMonth}>Next insight month</button><InsightsPage /></>; }
function page() { return render(<MemoryRouter><MonthProvider initialMonth="2026-04"><RenderControls /></MonthProvider></MemoryRouter>); }

describe("Mint-style insights", () => {
  beforeEach(() => { cleanup(); vi.clearAllMocks(); mocks.insights.mockImplementation(async (_token, path) => path === "/preferences" ? { ...insightPreferences } : spendingInsights()); });
  test("loads month and browser timezone, explains comparisons and links category evidence", async () => {
    mocks.insights.mockImplementation(async (_token, path) => path === "/preferences" ? { ...insightPreferences } : spendingInsights({ alerts: [insightAlert()] }));
    page();
    expect(await screen.findByText("Recorded through 2026-04-30")).toBeInTheDocument(); expect(screen.getByText("Food is over budget")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Review transactions" })).toHaveAttribute("href", "/transactions?from=2026-04-01&to=2026-04-30&timeZone=UTC&categoryId=category-1");
    expect(mocks.insights).toHaveBeenCalledWith("token", expect.stringContaining("month=2026-04&today=")); expect(mocks.insights.mock.calls[0][1]).toContain("timeZone=");
  });
  test("dismisses and restores an alert using account-backed state", async () => {
    let dismissed = false;
    mocks.insights.mockImplementation(async (_token, path, method) => { if (path === "/preferences") return { ...insightPreferences }; if (method === "POST") { dismissed = true; return; } if (method === "DELETE") { dismissed = false; return; } return spendingInsights({ alerts: [insightAlert({ dismissed })] }); });
    page(); const user = userEvent.setup(); await user.click(await screen.findByRole("button", { name: "Dismiss alert" }));
    expect(await screen.findByText("Alert dismissed for 2026-04.")).toBeInTheDocument(); expect(screen.queryByRole("button", { name: "Dismiss alert" })).not.toBeInTheDocument();
    await user.click(screen.getByLabelText("Show dismissed alerts")); await user.click(screen.getByRole("button", { name: "Restore alert" }));
    expect(await screen.findByText("Alert restored.")).toBeInTheDocument(); expect(mocks.insights).toHaveBeenCalledWith("token", `/dismiss/${"A".repeat(64)}`, "DELETE");
  });
  test("saves independent thresholds and alert toggles", async () => {
    let saved = { ...insightPreferences };
    mocks.insights.mockImplementation(async (_token, path, method, payload) => { if (path === "/preferences") { if (method === "PUT") saved = payload; return { ...saved }; } return spendingInsights(); });
    page(); const user = userEvent.setup(); await user.click(await screen.findByLabelText("Budget warnings and pace"));
    fireEvent.change(screen.getByLabelText("Large purchase threshold"), { target: { value: "900" } });
    await user.click(screen.getByRole("button", { name: "Save alert preferences" }));
    expect(await screen.findByText("Alert preferences saved.")).toBeInTheDocument(); expect(saved).toMatchObject({ budgetAlerts: false, largePurchaseThreshold: 900, spendingChangeAlerts: true });
  });
  test("shows incomplete currency coverage without a misleading clean alert state", async () => {
    mocks.insights.mockImplementation(async (_token, path) => path === "/preferences" ? { ...insightPreferences } : spendingInsights({ dataComplete: false, warnings: ["Missing FX rate for EUR to USD."], historicalAverage: null }));
    page(); expect(await screen.findByText("Incomplete currency coverage")).toBeInTheDocument(); expect(screen.getByText("Monetary alerts are paused until the missing exchange rates are supplied.")).toBeInTheDocument(); expect(screen.getByText("Known net spending")).toBeInTheDocument();
  });
  test("recovers from a failed request", async () => {
    let fail = true;
    mocks.insights.mockImplementation(async (_token, path) => { if (path === "/preferences") return { ...insightPreferences }; if (fail) throw new Error("Insights unavailable"); return spendingInsights(); });
    page(); expect(await screen.findByRole("alert")).toHaveTextContent("Insights unavailable"); fail = false; await userEvent.click(screen.getByRole("button", { name: "Retry insights" })); expect(await screen.findByText("Recorded through 2026-04-30")).toBeInTheDocument();
  });
  test("does not replace a newer month's results with an older request", async () => {
    let april: ((value: unknown) => void) | undefined;
    mocks.insights.mockImplementation((_token, path) => path === "/preferences" ? Promise.resolve({ ...insightPreferences }) : path.includes("month=2026-04") ? new Promise(resolve => { april = resolve; }) : Promise.resolve(spendingInsights({ month: "2026-05", throughDate: "2026-05-31" })));
    page(); await userEvent.click(screen.getByRole("button", { name: "Next insight month" })); expect(await screen.findByText("Recorded through 2026-05-31")).toBeInTheDocument();
    await act(async () => { april?.(spendingInsights()); }); expect(screen.queryByText("Recorded through 2026-04-30")).not.toBeInTheDocument();
  });
});
