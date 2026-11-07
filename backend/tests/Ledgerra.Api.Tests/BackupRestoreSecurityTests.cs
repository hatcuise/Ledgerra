using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Ledgerra.Api.Tests;

public sealed class BackupRestoreSecurityTests : IClassFixture<LedgerraApiFactory>
{
    private readonly LedgerraApiFactory _factory;

    public BackupRestoreSecurityTests(LedgerraApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Restore_RejectsTransactionReferencingForeignAccountId()
    {
        using var client = _factory.CreateClient();
        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth);

        var archiveAccountId = Guid.NewGuid();
        var foreignAccountId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();

        var archive = new
        {
            version = 2,
            exportedAtUtc = "2025-01-01T00:00:00Z",
            accounts = new[] { new { id = archiveAccountId, name = "My Account", type = "Checking", currencyCode = "USD", openingBalance = 0m, isActive = true } },
            categories = new[] { new { id = categoryId, name = "Food", kind = "Expense", color = (string?)null } },
            transactions = new[]
            {
                new
                {
                    id = Guid.NewGuid(), accountId = foreignAccountId, categoryId = (Guid?)categoryId,
                    amount = 100m, type = "Expense", occurredOnUtc = "2025-01-15T00:00:00Z",
                    note = (string?)null, transferGroupId = (Guid?)null, splitGroupId = (Guid?)null, parentTransactionId = (Guid?)null
                }
            },
            budgetPeriods = Array.Empty<object>()
        };

        var response = await client.PostAsJsonAsync("/api/backup/restore", archive);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Restore_RejectsTransactionReferencingForeignCategoryId()
    {
        using var client = _factory.CreateClient();
        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth);

        var accountId = Guid.NewGuid();
        var archiveCategoryId = Guid.NewGuid();
        var foreignCategoryId = Guid.NewGuid();

        var archive = new
        {
            version = 2,
            exportedAtUtc = "2025-01-01T00:00:00Z",
            accounts = new[] { new { id = accountId, name = "My Account", type = "Checking", currencyCode = "USD", openingBalance = 0m, isActive = true } },
            categories = new[] { new { id = archiveCategoryId, name = "Food", kind = "Expense", color = (string?)null } },
            transactions = new[]
            {
                new
                {
                    id = Guid.NewGuid(), accountId, categoryId = (Guid?)foreignCategoryId,
                    amount = 50m, type = "Expense", occurredOnUtc = "2025-02-01T00:00:00Z",
                    note = (string?)null, transferGroupId = (Guid?)null, splitGroupId = (Guid?)null, parentTransactionId = (Guid?)null
                }
            },
            budgetPeriods = Array.Empty<object>()
        };

        var response = await client.PostAsJsonAsync("/api/backup/restore", archive);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Restore_RejectsBudgetLimitReferencingForeignCategoryId()
    {
        using var client = _factory.CreateClient();
        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth);

        var accountId = Guid.NewGuid();
        var archiveCategoryId = Guid.NewGuid();
        var foreignCategoryId = Guid.NewGuid();
        var budgetPeriodId = Guid.NewGuid();

        var archive = new
        {
            version = 2,
            exportedAtUtc = "2025-01-01T00:00:00Z",
            accounts = new[] { new { id = accountId, name = "My Account", type = "Checking", currencyCode = "USD", openingBalance = 0m, isActive = true } },
            categories = new[] { new { id = archiveCategoryId, name = "Food", kind = "Expense", color = (string?)null } },
            transactions = Array.Empty<object>(),
            budgetPeriods = new[]
            {
                new
                {
                    id = budgetPeriodId, year = 2025, month = 3,
                    categoryLimits = new[] { new { id = Guid.NewGuid(), categoryId = foreignCategoryId, plannedAmount = 500m } }
                }
            }
        };

        var response = await client.PostAsJsonAsync("/api/backup/restore", archive);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Restore_RejectsMalformedEnumValuesAsBadRequest()
    {
        using var client = _factory.CreateClient();
        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth);

        var accountId = Guid.NewGuid();

        var archive = new
        {
            version = 3,
            exportedAtUtc = "2025-01-01T00:00:00Z",
            accounts = new[]
            {
                new
                {
                    id = accountId,
                    name = "My Account",
                    type = "NotARealAccountType",
                    currencyCode = "USD",
                    openingBalance = 0m,
                    isActive = true,
                    iconKind = "Bank"
                }
            },
            categories = Array.Empty<object>(),
            transactions = Array.Empty<object>(),
            budgetPeriods = Array.Empty<object>(),
            savingsGoals = Array.Empty<object>()
        };

        var response = await client.PostAsJsonAsync("/api/backup/restore", archive);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Restore_RejectsTransactionReferencingForeignParentTransactionId()
    {
        using var client = _factory.CreateClient();
        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth);

        var accountId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var foreignParentId = Guid.NewGuid();

        var archive = new
        {
            version = 2,
            exportedAtUtc = "2025-01-01T00:00:00Z",
            accounts = new[] { new { id = accountId, name = "My Account", type = "Checking", currencyCode = "USD", openingBalance = 0m, isActive = true } },
            categories = new[] { new { id = categoryId, name = "Food", kind = "Expense", color = (string?)null } },
            transactions = new[]
            {
                new
                {
                    id = Guid.NewGuid(), accountId, categoryId = (Guid?)categoryId,
                    amount = 30m, type = "Expense", occurredOnUtc = "2025-01-20T00:00:00Z",
                    note = (string?)null, transferGroupId = (Guid?)null, splitGroupId = (Guid?)null, parentTransactionId = (Guid?)foreignParentId
                }
            },
            budgetPeriods = Array.Empty<object>()
        };

        var response = await client.PostAsJsonAsync("/api/backup/restore", archive);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }


    [Fact]
    public async Task Restore_RejectsTransactionReferencingForeignSavingsGoalId()
    {
        using var client = _factory.CreateClient();
        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth);

        var accountId = Guid.NewGuid();
        var foreignSavingsGoalId = Guid.NewGuid();

        var archive = new
        {
            version = 3,
            exportedAtUtc = "2025-01-01T00:00:00Z",
            accounts = new[] { new { id = accountId, name = "My Account", type = "Checking", currencyCode = "USD", openingBalance = 0m, isActive = true } },
            categories = Array.Empty<object>(),
            transactions = new[]
            {
                new
                {
                    id = Guid.NewGuid(), accountId, categoryId = (Guid?)null,
                    amount = 50m, type = "Income", occurredOnUtc = "2025-02-01T00:00:00Z",
                    note = (string?)null, transferGroupId = (Guid?)null, splitGroupId = (Guid?)null, parentTransactionId = (Guid?)null, savingsGoalId = (Guid?)foreignSavingsGoalId
                }
            },
            budgetPeriods = Array.Empty<object>(),
            savingsGoals = Array.Empty<object>()
        };

        var response = await client.PostAsJsonAsync("/api/backup/restore", archive);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Restore_AcceptsValidSelfContainedArchive()
    {
        using var client = _factory.CreateClient();
        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth);

        var accountId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var budgetPeriodId = Guid.NewGuid();

        var archive = new
        {
            version = 2,
            exportedAtUtc = "2025-01-01T00:00:00Z",
            accounts = new[] { new { id = accountId, name = "My Account", type = "Checking", currencyCode = "USD", openingBalance = 0m, isActive = true } },
            categories = new[] { new { id = categoryId, name = "Food", kind = "Expense", color = (string?)null } },
            transactions = new[]
            {
                new
                {
                    id = Guid.NewGuid(), accountId, categoryId = (Guid?)categoryId,
                    amount = 25m, type = "Expense", occurredOnUtc = "2025-01-10T00:00:00Z",
                    note = (string?)null, transferGroupId = (Guid?)null, splitGroupId = (Guid?)null, parentTransactionId = (Guid?)null
                }
            },
            budgetPeriods = new[]
            {
                new
                {
                    id = budgetPeriodId, year = 2025, month = 1,
                    categoryLimits = new[] { new { id = Guid.NewGuid(), categoryId, plannedAmount = 200m } }
                }
            }
        };

        var response = await client.PostAsJsonAsync("/api/backup/restore", archive);

        if (response.StatusCode != HttpStatusCode.NoContent)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException($"Expected 204 but got {(int)response.StatusCode}: {body}");
        }
    }

    [Fact]
    public async Task ExportAndRestore_PreservesBudgetRolloverSetting()
    {
        using var client = _factory.CreateClient();
        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth);

        var categoryResponse = await client.PostAsJsonAsync("/api/categories", new
        {
            name = "Food",
            kind = "Expense"
        });
        Assert.Equal(HttpStatusCode.Created, categoryResponse.StatusCode);
        var categoryPayload = await categoryResponse.Content.ReadFromJsonAsync<JsonElement>();
        var categoryId = categoryPayload.GetProperty("id").GetGuid();

        var budgetResponse = await client.PutAsJsonAsync("/api/budgets/2025/1", new
        {
            categoryLimits = new[]
            {
                new
                {
                    categoryId,
                    plannedAmount = 200m,
                    carryOverUnspent = true
                }
            }
        });
        Assert.Equal(HttpStatusCode.OK, budgetResponse.StatusCode);

        var exportResponse = await client.GetAsync("/api/backup/export");
        Assert.Equal(HttpStatusCode.OK, exportResponse.StatusCode);
        var archive = await exportResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(archive.GetProperty("budgetPeriods")[0].GetProperty("categoryLimits")[0].GetProperty("carryOverUnspent").GetBoolean());

        var restoreResponse = await client.PostAsJsonAsync("/api/backup/restore", archive);
        Assert.Equal(HttpStatusCode.NoContent, restoreResponse.StatusCode);

        var restoredBudgetResponse = await client.GetAsync("/api/budgets/2025/1");
        Assert.Equal(HttpStatusCode.OK, restoredBudgetResponse.StatusCode);
        var restoredBudget = await restoredBudgetResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(restoredBudget.GetProperty("categories")[0].GetProperty("carryOverUnspent").GetBoolean());
    }

    private static async Task<string> RegisterAndAuthenticateAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            login = $"user-{Guid.NewGuid():N}".Substring(0, 20),
            email = $"user-{Guid.NewGuid():N}@ledgerra.local",
            password = "P@ssw0rd123!"
        });

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException(
                $"/api/auth/register failed with {(int)response.StatusCode}: {body}");
        }

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return payload.GetProperty("accessToken").GetString()!;
    }
    [Fact]
    public async Task ExportAndRestore_PreservesSavingsGoalsAndAccountMetadata()
    {
        using var client = _factory.CreateClient();
        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth);

        var accountResponse = await client.PostAsJsonAsync("/api/accounts", new
        {
            name = "Primary",
            type = "Checking",
            currencyCode = "USD",
            openingBalance = 12m,
            institutionName = "Credit Union",
            accountNumberMasked = "****1234",
            iconKind = "Cash"
        });
        Assert.Equal(HttpStatusCode.Created, accountResponse.StatusCode);

        var goalResponse = await client.PostAsJsonAsync("/api/savings-goals", new
        {
            name = "Emergency",
            targetAmount = 1000m,
            deadlineUtc = "2026-12-31T00:00:00Z"
        });
        Assert.Equal(HttpStatusCode.Created, goalResponse.StatusCode);
        var goalPayload = await goalResponse.Content.ReadFromJsonAsync<JsonElement>();
        var goalId = goalPayload.GetProperty("id").GetGuid();

        var accountPayload = await accountResponse.Content.ReadFromJsonAsync<JsonElement>();
        var accountId = accountPayload.GetProperty("id").GetGuid();

        var txResponse = await client.PostAsJsonAsync("/api/transactions", new
        {
            accountId,
            amount = 100m,
            type = "Income",
            occurredOnUtc = "2026-01-01T00:00:00Z",
            savingsGoalId = goalId
        });
        Assert.Equal(HttpStatusCode.Created, txResponse.StatusCode);

        var exportResponse = await client.GetAsync("/api/backup/export");
        Assert.Equal(HttpStatusCode.OK, exportResponse.StatusCode);
        var archive = await exportResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(4, archive.GetProperty("version").GetInt32());
        Assert.Equal("Credit Union", archive.GetProperty("accounts")[0].GetProperty("institutionName").GetString());
        Assert.Equal("Cash", archive.GetProperty("accounts")[0].GetProperty("iconKind").GetString());
        Assert.Equal(goalId, archive.GetProperty("transactions")[0].GetProperty("savingsGoalId").GetGuid());
        Assert.Equal(goalId, archive.GetProperty("savingsGoals")[0].GetProperty("id").GetGuid());

        var restoreResponse = await client.PostAsJsonAsync("/api/backup/restore", archive);
        Assert.Equal(HttpStatusCode.NoContent, restoreResponse.StatusCode);
    }
}
