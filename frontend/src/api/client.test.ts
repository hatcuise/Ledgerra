import { afterEach, describe, expect, it, vi } from "vitest";

import { apiClient, resolveApiUrl } from "./client";

afterEach(() => {
  apiClient.setAuthHandlers(null, null);
  vi.useRealTimers();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe("resolveApiUrl", () => {
  it("keeps same-origin api paths when no base url is configured", () => {
    expect(resolveApiUrl("", "/api/auth/login")).toBe("/api/auth/login");
  });

  it("joins an origin-only base url with api-prefixed request paths", () => {
    expect(resolveApiUrl("http://localhost:5027", "/api/auth/login")).toBe("http://localhost:5027/api/auth/login");
  });

  it("avoids duplicating the api prefix when the base url already ends with /api", () => {
    expect(resolveApiUrl("/api", "/api/auth/login")).toBe("/api/auth/login");
    expect(resolveApiUrl("https://ledgerra.example/api/", "/api/auth/login")).toBe(
      "https://ledgerra.example/api/auth/login"
    );
  });
});

describe("apiClient", () => {
  it("omits email from register requests when no email is provided", async () => {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      status: 201,
      json: async () => ({
        userId: "user-1",
        login: "owner",
        email: "",
        accessToken: "token",
        refreshToken: "refresh",
        expiresAtUtc: "2999-01-01T00:00:00Z"
      })
    });
    vi.stubGlobal("fetch", fetchMock);

    await apiClient.register("owner", "P@ssw0rd123!");

    const [, init] = fetchMock.mock.calls[0];
    expect(JSON.parse(init?.body as string)).toEqual({
      login: "owner",
      password: "P@ssw0rd123!"
    });
  });

  it("refreshes once and retries concurrent unauthorized requests", async () => {
    const persist = vi.fn();
    apiClient.setAuthHandlers(
      () => ({
        userId: "user-1",
        login: "owner",
        email: "owner@test.local",
        accessToken: "expired-token",
        refreshToken: "refresh-token",
        expiresAtUtc: "2999-01-01T00:00:00Z"
      }),
      persist
    );

    const fetchMock = vi.fn()
      .mockResolvedValueOnce({ ok: false, status: 401, clone: () => ({ json: async () => ({ title: "expired" }) }), text: async () => "expired" })
      .mockResolvedValueOnce({ ok: false, status: 401, clone: () => ({ json: async () => ({ title: "expired" }) }), text: async () => "expired" })
      .mockResolvedValueOnce({
        ok: true,
        status: 200,
        json: async () => ({
          userId: "user-1", login: "owner", email: "owner@test.local", accessToken: "fresh-token", refreshToken: "rotated", expiresAtUtc: "2999-01-02T00:00:00Z"
        })
      })
      .mockResolvedValue({ ok: true, status: 200, json: async () => ({ value: 1 }) });
    vi.stubGlobal("fetch", fetchMock);

    await Promise.all([
      apiClient.getDashboard("expired-token", "2026-05"),
      apiClient.getAccounts("expired-token")
    ]);

    const refreshCalls = fetchMock.mock.calls.filter((call) => (call[0] as string).includes("/api/auth/refresh"));
    expect(refreshCalls).toHaveLength(1);
    expect(persist).toHaveBeenCalledWith(expect.objectContaining({ accessToken: "fresh-token", refreshToken: "rotated" }));
  });

  it("notifies unauthorized and does not retry loop when refresh fails", async () => {
    const persist = vi.fn();
    const onUnauthorized = vi.fn();
    apiClient.setAuthHandlers(
      () => ({
        userId: "user-1",
        login: "owner",
        email: "owner@test.local",
        accessToken: "expired-token",
        refreshToken: "refresh-token",
        expiresAtUtc: "2999-01-01T00:00:00Z"
      }),
      persist
    );
    const unsubscribe = apiClient.onUnauthorized(onUnauthorized);

    const fetchMock = vi.fn()
      .mockResolvedValueOnce({ ok: false, status: 401, clone: () => ({ json: async () => ({ title: "expired" }) }), text: async () => "expired" })
      .mockResolvedValueOnce({ ok: false, status: 401, clone: () => ({ json: async () => ({ title: "invalid refresh" }) }), text: async () => "invalid refresh" });
    vi.stubGlobal("fetch", fetchMock);

    await expect(apiClient.getAccounts("expired-token")).rejects.toThrow();
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(persist).toHaveBeenCalledWith(null);
    expect(onUnauthorized).toHaveBeenCalledTimes(1);
    unsubscribe();
  });

  it("refreshes and retries CSV preview requests through the shared request path", async () => {
    const persist = vi.fn();
    apiClient.setAuthHandlers(
      () => ({
        userId: "user-1",
        login: "owner",
        email: "owner@test.local",
        accessToken: "expired-token",
        refreshToken: "refresh-token",
        expiresAtUtc: "2999-01-01T00:00:00Z"
      }),
      persist
    );

    const fetchMock = vi.fn()
      .mockResolvedValueOnce({ ok: false, status: 401, clone: () => ({ json: async () => ({ title: "expired" }) }), text: async () => "expired" })
      .mockResolvedValueOnce({
        ok: true,
        status: 200,
        json: async () => ({
          userId: "user-1",
          login: "owner",
          email: "owner@test.local",
          accessToken: "fresh-token",
          refreshToken: "rotated",
          expiresAtUtc: "2999-01-02T00:00:00Z"
        })
      })
      .mockResolvedValueOnce({
        ok: true,
        status: 200,
        json: async () => ({ transactions: [], warnings: [] })
      });
    vi.stubGlobal("fetch", fetchMock);

    await expect(apiClient.previewCsvBankImport("expired-token", {
      accountId: "account-1",
      file: new File(["date,amount"], "report.csv", { type: "text/csv" }),
      dateColumn: "date",
      amountColumn: "amount",
      invertAmounts: true
    })).resolves.toEqual({ transactions: [], warnings: [] });

    expect(fetchMock.mock.calls.map((call) => call[0])).toEqual([
      expect.stringContaining("/api/imports/monthly-report/csv-preview"),
      expect.stringContaining("/api/auth/refresh"),
      expect.stringContaining("/api/imports/monthly-report/csv-preview")
    ]);
    expect(fetchMock.mock.calls[2][1]).toMatchObject({
      method: "POST",
      headers: { Authorization: "Bearer fresh-token" }
    });
    expect((fetchMock.mock.calls[2][1].body as FormData).get("invertAmounts")).toBe("true");
    expect(persist).toHaveBeenCalledWith(expect.objectContaining({ accessToken: "fresh-token" }));
  });

  it("submits monthly report analysis as a job and polls until completion", async () => {
    vi.useFakeTimers();
    const onJobUpdate = vi.fn();
    const fetchMock = vi.fn()
      .mockResolvedValueOnce({
        ok: true,
        status: 202,
        json: async () => ({
          jobId: "job-1",
          status: "running",
          statusMessage: "Queued for AI analysis.",
          generatedOutputCharacters: null,
          usage: null,
          analysis: null,
          error: null,
          createdAtUtc: "2026-05-23T00:00:00Z",
          updatedAtUtc: "2026-05-23T00:00:00Z"
        })
      })
      .mockResolvedValueOnce({
        ok: true,
        status: 200,
        json: async () => ({
          jobId: "job-1",
          status: "completed",
          statusMessage: "Analysis completed.",
          generatedOutputCharacters: 42,
          usage: { promptTokens: 10, completionTokens: 5, totalTokens: 15 },
          analysis: {
            transactions: [],
            warnings: []
          },
          error: null,
          createdAtUtc: "2026-05-23T00:00:00Z",
          updatedAtUtc: "2026-05-23T00:00:01Z"
        })
      });
    vi.stubGlobal("fetch", fetchMock);

    const analysisPromise = apiClient.analyzeMonthlyReport("token", {
      accountId: "account-1",
      month: "2026-05",
      provider: "OpenAiCompatible",
      file: new File(["report"], "report.pdf", { type: "application/pdf" })
    }, onJobUpdate);

    await vi.advanceTimersByTimeAsync(1500);

    await expect(analysisPromise).resolves.toEqual({ transactions: [], warnings: [] });
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(fetchMock.mock.calls[1][0]).toContain("/api/imports/monthly-report/analyze/job-1");
    expect(onJobUpdate).toHaveBeenCalledWith(expect.objectContaining({ statusMessage: "Queued for AI analysis." }));
    expect(onJobUpdate).toHaveBeenCalledWith(expect.objectContaining({ generatedOutputCharacters: 42 }));
  });

  it("retries parsing saved monthly report analysis output", async () => {
    const onJobUpdate = vi.fn();
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        jobId: "job-1",
        status: "completed",
        statusMessage: "Saved AI output parsed.",
        generatedOutputCharacters: 300,
        usage: { promptTokens: 20, completionTokens: 40, totalTokens: 60 },
        analysis: {
          transactions: [],
          warnings: ["Recovered from saved output."]
        },
        error: null,
        hasRawAiOutput: true,
        createdAtUtc: "2026-05-23T00:00:00Z",
        updatedAtUtc: "2026-05-23T00:00:01Z"
      })
    });
    vi.stubGlobal("fetch", fetchMock);

    await expect(apiClient.retryMonthlyReportAnalysisParse("token", "job-1", onJobUpdate)).resolves.toEqual({
      transactions: [],
      warnings: ["Recovered from saved output."]
    });
    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining("/api/imports/monthly-report/analyze/job-1/retry-parse"),
      expect.objectContaining({ method: "POST", headers: { Authorization: "Bearer token" } })
    );
    expect(onJobUpdate).toHaveBeenCalledWith(expect.objectContaining({ statusMessage: "Saved AI output parsed." }));
  });

  it("does not treat a running retry-parse job as completed", async () => {
    const onJobUpdate = vi.fn();
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        jobId: "job-1",
        status: "running",
        statusMessage: "Retrying saved AI output parse.",
        generatedOutputCharacters: 300,
        usage: { promptTokens: 20, completionTokens: 40, totalTokens: 60 },
        analysis: null,
        error: null,
        hasRawAiOutput: true,
        createdAtUtc: "2026-05-23T00:00:00Z",
        updatedAtUtc: "2026-05-23T00:00:01Z"
      })
    });
    vi.stubGlobal("fetch", fetchMock);

    await expect(apiClient.retryMonthlyReportAnalysisParse("token", "job-1", onJobUpdate)).resolves.toBeNull();
    expect(onJobUpdate).toHaveBeenCalledWith(expect.objectContaining({ status: "running" }));
  });

  it("downloads saved monthly report analysis raw output", async () => {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      headers: {
        get: (name: string) => name.toLowerCase() === "content-disposition"
          ? 'attachment; filename="ledgerra-ai-output-job-1.json"'
          : null
      },
      blob: async () => new Blob(['{"transactions":[]}'], { type: "application/json" })
    });
    vi.stubGlobal("fetch", fetchMock);

    await expect(apiClient.downloadMonthlyReportAnalysisRawOutput("token", "job-1")).resolves.toMatchObject({
      filename: "ledgerra-ai-output-job-1.json"
    });
    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining("/api/imports/monthly-report/analyze/job-1/raw-output"),
      expect.objectContaining({ headers: { Authorization: "Bearer token" } })
    );
  });

  it("falls back to a safe filename when content-disposition encoding is malformed", async () => {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      headers: {
        get: (name: string) => name.toLowerCase() === "content-disposition"
          ? "attachment; filename*=UTF-8''bad%ZZname.json; filename=\"fallback.json\""
          : null
      },
      blob: async () => new Blob(['{"transactions":[]}'], { type: "application/json" })
    });
    vi.stubGlobal("fetch", fetchMock);

    await expect(apiClient.downloadMonthlyReportAnalysisRawOutput("token", "job-1")).resolves.toMatchObject({
      filename: "fallback.json"
    });
  });
});
