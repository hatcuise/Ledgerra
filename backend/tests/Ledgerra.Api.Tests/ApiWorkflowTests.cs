using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Ledgerra.Api.Tests;

public sealed class ApiWorkflowTests : IClassFixture<LedgerraApiFactory>
{
    private readonly LedgerraApiFactory _factory;

    public ApiWorkflowTests(LedgerraApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task RegisterLoginAndRefresh_ReturnTokens()
    {
        using var client = _factory.CreateClient();

        var registerResponse = await client.PostAsJsonAsync("/api/auth/register", new
        {
            login = "owner",
            email = "owner@ledgerra.local",
            password = "P@ssw0rd123!"
        });

        if (registerResponse.StatusCode != HttpStatusCode.Created)
        {
            var body = await registerResponse.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException($"Expected 201 but got {(int)registerResponse.StatusCode}: {body}");
        }

        var registerPayload = await registerResponse.Content.ReadFromJsonAsync<JsonElement>();
        var refreshToken = registerPayload.GetProperty("refreshToken").GetString();

        Assert.False(string.IsNullOrWhiteSpace(registerPayload.GetProperty("accessToken").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(refreshToken));

        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new
        {
            login = "owner",
            password = "P@ssw0rd123!"
        });

        if (loginResponse.StatusCode != HttpStatusCode.OK)
        {
            var body = await loginResponse.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException($"Login failed with {(int)loginResponse.StatusCode}: {body}");
        }

        var refreshResponse = await client.PostAsJsonAsync("/api/auth/refresh", new
        {
            refreshToken
        });

        if (refreshResponse.StatusCode != HttpStatusCode.OK)
        {
            var body = await refreshResponse.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException($"Refresh failed with {(int)refreshResponse.StatusCode}: {body}");
        }
    }

    [Fact]
    public async Task Register_DoesNotRequireEmail()
    {
        using var client = _factory.CreateClient();

        var login = $"owner-{Guid.NewGuid():N}"[..32];
        var registerResponse = await client.PostAsJsonAsync("/api/auth/register", new
        {
            login,
            password = "P@ssw0rd123!"
        });

        if (registerResponse.StatusCode != HttpStatusCode.Created)
        {
            var body = await registerResponse.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException($"Expected 201 but got {(int)registerResponse.StatusCode}: {body}");
        }

        var registerPayload = await registerResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(login, registerPayload.GetProperty("login").GetString());
        Assert.Equal(string.Empty, registerPayload.GetProperty("email").GetString());
        Assert.False(string.IsNullOrWhiteSpace(registerPayload.GetProperty("accessToken").GetString()));
    }

    [Fact]
    public async Task Register_RejectsLegacyNicknameField()
    {
        using var client = _factory.CreateClient();

        var registerResponse = await client.PostAsJsonAsync("/api/auth/register", new
        {
            nickname = "legacy-owner",
            password = "P@ssw0rd123!"
        });

        Assert.Equal(HttpStatusCode.BadRequest, registerResponse.StatusCode);
    }

    [Fact]
    public async Task Register_AllowsBlankEmail()
    {
        using var client = _factory.CreateClient();

        var login = $"blank-{Guid.NewGuid():N}"[..32];
        var registerResponse = await client.PostAsJsonAsync("/api/auth/register", new
        {
            login,
            email = "",
            password = "P@ssw0rd123!"
        });

        Assert.Equal(HttpStatusCode.Created, registerResponse.StatusCode);
    }

    [Fact]
    public async Task Register_RejectsInvalidNonBlankEmail()
    {
        using var client = _factory.CreateClient();

        var registerResponse = await client.PostAsJsonAsync("/api/auth/register", new
        {
            login = $"bad-email-{Guid.NewGuid():N}"[..32],
            email = "not-an-email",
            password = "P@ssw0rd123!"
        });

        Assert.Equal(HttpStatusCode.BadRequest, registerResponse.StatusCode);
    }

    [Fact]
    public async Task Register_AllowsMultipleUsersWithoutEmail()
    {
        using var client = _factory.CreateClient();

        var firstResponse = await client.PostAsJsonAsync("/api/auth/register", new
        {
            login = $"first-{Guid.NewGuid():N}"[..32],
            password = "P@ssw0rd123!"
        });
        var secondResponse = await client.PostAsJsonAsync("/api/auth/register", new
        {
            login = $"second-{Guid.NewGuid():N}"[..32],
            password = "P@ssw0rd123!"
        });

        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, secondResponse.StatusCode);
    }

    [Fact]
    public async Task AuthenticatedUser_CanCreateAndUsePersonalAccessToken()
    {
        using var client = _factory.CreateClient();

        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var createResponse = await client.PostAsJsonAsync("/api/settings/personal-access-tokens", new
        {
            name = "CLI token"
        });

        if (createResponse.StatusCode != HttpStatusCode.Created)
        {
            var body = await createResponse.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException($"Token creation failed with {(int)createResponse.StatusCode}: {body}");
        }

        var createPayload = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var personalAccessToken = createPayload.GetProperty("plainTextToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(personalAccessToken));
        Assert.Equal("CLI token", createPayload.GetProperty("token").GetProperty("name").GetString());

        using var patClient = _factory.CreateClient();
        patClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", personalAccessToken);

        var profileResponse = await patClient.GetAsync("/api/settings/profile");

        Assert.Equal(HttpStatusCode.OK, profileResponse.StatusCode);

        var revokeResponse = await client.DeleteAsync($"/api/settings/personal-access-tokens/{createPayload.GetProperty("token").GetProperty("id").GetGuid()}");
        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);

        var revokedProfileResponse = await patClient.GetAsync("/api/settings/profile");
        Assert.Equal(HttpStatusCode.Unauthorized, revokedProfileResponse.StatusCode);

        var listResponse = await client.GetAsync("/api/settings/personal-access-tokens");
        var listPayload = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(listPayload.EnumerateArray());
    }

    [Fact]
    public async Task PersonalAccessToken_CanRunAgentImportWorkflow()
    {
        using var client = _factory.CreateClient();

        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var accountId = await CreateAccountAsync(client, "Agent Checking", "Checking", 1000m);
        var groceriesCategoryId = await CreateCategoryAsync(client, "Groceries", "Expense");

        var createPatResponse = await client.PostAsJsonAsync("/api/settings/personal-access-tokens", new
        {
            name = "Agent import token"
        });
        Assert.Equal(HttpStatusCode.Created, createPatResponse.StatusCode);

        var patPayload = await createPatResponse.Content.ReadFromJsonAsync<JsonElement>();
        var personalAccessToken = patPayload.GetProperty("plainTextToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(personalAccessToken));

        using var patClient = _factory.CreateClient();
        patClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", personalAccessToken);

        var accountsResponse = await patClient.GetAsync("/api/accounts");
        Assert.Equal(HttpStatusCode.OK, accountsResponse.StatusCode);
        var accountsPayload = await accountsResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(accountsPayload.EnumerateArray(), entry => entry.GetProperty("id").GetGuid() == accountId);

        var commitResponse = await patClient.PostAsJsonAsync("/api/imports/monthly-report/commit", new
        {
            transactions = new[]
            {
                new
                {
                    sourceId = "agent-row-1",
                    accountId,
                    categoryId = groceriesCategoryId,
                    amount = 23.40m,
                    type = "Expense",
                    occurredOnUtc = "2026-04-13T10:30:00Z",
                    note = "Imported by agent"
                }
            }
        });
        Assert.Equal(HttpStatusCode.Created, commitResponse.StatusCode);

        var transactionsResponse = await patClient.GetAsync($"/api/transactions?accountId={accountId}&from=2026-04-01&to=2026-04-30");
        Assert.Equal(HttpStatusCode.OK, transactionsResponse.StatusCode);
        var transactionsPayload = await transactionsResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(transactionsPayload.EnumerateArray(), entry => entry.GetProperty("note").GetString() == "Imported by agent");
    }

    [Fact]
    public async Task AuthenticatedUser_CanCreateAccountsCategoriesTransactionsBudgetsAndDashboard()
    {
        using var client = _factory.CreateClient();

        var auth = await RegisterAndAuthenticateAsync(client);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var checkingId = await CreateAccountAsync(client, "Personal Checking", "Checking", 1500m);
        var jointId = await CreateAccountAsync(client, "Shared Household", "Joint", 600m);
        var groceriesCategoryId = await CreateCategoryAsync(client, "Groceries", "Expense");
        var salaryCategoryId = await CreateCategoryAsync(client, "Salary", "Income");

        var incomeResponse = await client.PostAsJsonAsync("/api/transactions", new
        {
            accountId = checkingId,
            categoryId = salaryCategoryId,
            amount = 3200m,
            type = "Income",
            occurredOnUtc = "2026-04-01T08:00:00Z",
            note = "Monthly salary"
        });
        if (incomeResponse.StatusCode != HttpStatusCode.Created)
        {
            var body = await incomeResponse.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException($"Income transaction failed with {(int)incomeResponse.StatusCode}: {body}");
        }

        var expenseResponse = await client.PostAsJsonAsync("/api/transactions", new
        {
            accountId = checkingId,
            categoryId = groceriesCategoryId,
            amount = 180m,
            type = "Expense",
            occurredOnUtc = "2026-04-05T08:00:00Z",
            note = "Weekly groceries"
        });
        if (expenseResponse.StatusCode != HttpStatusCode.Created)
        {
            var body = await expenseResponse.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException($"Expense transaction failed with {(int)expenseResponse.StatusCode}: {body}");
        }

        var transferResponse = await client.PostAsJsonAsync("/api/transactions", new
        {
            accountId = checkingId,
            destinationAccountId = jointId,
            amount = 200m,
            type = "Transfer",
            occurredOnUtc = "2026-04-06T08:00:00Z",
            note = "Move to shared account"
        });
        if (transferResponse.StatusCode != HttpStatusCode.Created)
        {
            var body = await transferResponse.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException($"Transfer transaction failed with {(int)transferResponse.StatusCode}: {body}");
        }

        var budgetResponse = await client.PutAsJsonAsync("/api/budgets/2026/4", new
        {
            categoryLimits = new[]
            {
                new
                {
                    categoryId = groceriesCategoryId,
                    plannedAmount = 500m
                }
            }
        });
        if (budgetResponse.StatusCode != HttpStatusCode.OK)
        {
            var body = await budgetResponse.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException($"Budget update failed with {(int)budgetResponse.StatusCode}: {body}");
        }

        var dashboardResponse = await client.GetAsync("/api/dashboard/summary?month=2026-04");
        Assert.Equal(HttpStatusCode.OK, dashboardResponse.StatusCode);

        var dashboard = await dashboardResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3200m, dashboard.GetProperty("income").GetDecimal());
        Assert.Equal(180m, dashboard.GetProperty("expenses").GetDecimal());
        Assert.Equal(3020m, dashboard.GetProperty("net").GetDecimal());

        var accountsResponse = await client.GetAsync("/api/accounts");
        var accountsPayload = await accountsResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, accountsPayload.GetArrayLength());

        var transactionsResponse = await client.GetAsync($"/api/transactions?accountId={checkingId}&from=2026-04-01&to=2026-04-30");
        var transactionsPayload = await transactionsResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, transactionsPayload.GetArrayLength());

        var budgetsSummaryResponse = await client.GetAsync("/api/budgets/2026/4");
        var budgetPayload = await budgetsSummaryResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(500m, budgetPayload.GetProperty("totalPlanned").GetDecimal());
        Assert.Equal(180m, budgetPayload.GetProperty("totalSpent").GetDecimal());
        Assert.Equal(320m, budgetPayload.GetProperty("totalRemaining").GetDecimal());
    }

    [Fact]
    public async Task BudgetRollover_CarriesUnspentAmountIntoBudgetAndDashboard()
    {
        using var client = _factory.CreateClient();

        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var checkingId = await CreateAccountAsync(client, "Personal Checking", "Checking", 1500m);
        var groceriesCategoryId = await CreateCategoryAsync(client, "Groceries", "Expense");

        var previousBudgetResponse = await client.PutAsJsonAsync("/api/budgets/2026/4", new
        {
            categoryLimits = new[]
            {
                new
                {
                    categoryId = groceriesCategoryId,
                    plannedAmount = 500m,
                    carryOverUnspent = true
                }
            }
        });
        Assert.Equal(HttpStatusCode.OK, previousBudgetResponse.StatusCode);

        var expenseResponse = await client.PostAsJsonAsync("/api/transactions", new
        {
            accountId = checkingId,
            categoryId = groceriesCategoryId,
            amount = 180m,
            type = "Expense",
            occurredOnUtc = "2026-04-05T08:00:00Z",
            note = "Weekly groceries"
        });
        Assert.Equal(HttpStatusCode.Created, expenseResponse.StatusCode);

        var currentBudgetResponse = await client.PutAsJsonAsync("/api/budgets/2026/5", new
        {
            categoryLimits = new[]
            {
                new
                {
                    categoryId = groceriesCategoryId,
                    plannedAmount = 400m,
                    carryOverUnspent = true
                }
            }
        });
        Assert.Equal(HttpStatusCode.OK, currentBudgetResponse.StatusCode);

        var budgetPayload = await currentBudgetResponse.Content.ReadFromJsonAsync<JsonElement>();
        var categoryPayload = budgetPayload.GetProperty("categories")[0];
        Assert.Equal(320m, categoryPayload.GetProperty("carryForward").GetDecimal());
        Assert.Equal(720m, categoryPayload.GetProperty("available").GetDecimal());
        Assert.Equal(720m, budgetPayload.GetProperty("totalRemaining").GetDecimal());

        var dashboardResponse = await client.GetAsync("/api/dashboard/summary?month=2026-05");
        Assert.Equal(HttpStatusCode.OK, dashboardResponse.StatusCode);

        var dashboardPayload = await dashboardResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(720m, dashboardPayload.GetProperty("budgetRemaining").GetDecimal());
    }

    [Fact]
    public async Task AuthenticatedUser_CanUpdateProfilePreferences()
    {
        using var client = _factory.CreateClient();

        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var initialResponse = await client.GetAsync("/api/settings/profile");
        Assert.Equal(HttpStatusCode.OK, initialResponse.StatusCode);

        var initialPayload = await initialResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("USD", initialPayload.GetProperty("preferredCurrencyCode").GetString());
        Assert.Equal("en", initialPayload.GetProperty("preferredLanguageCode").GetString());

        var updateResponse = await client.PutAsJsonAsync("/api/settings/profile", new
        {
            preferredCurrencyCode = "eur",
            preferredLanguageCode = "pl"
        });
        if (updateResponse.StatusCode != HttpStatusCode.OK)
        {
            var body = await updateResponse.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException($"Profile update failed with {(int)updateResponse.StatusCode}: {body}");
        }

        var updatePayload = await updateResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("EUR", updatePayload.GetProperty("preferredCurrencyCode").GetString());
        Assert.Equal("pl", updatePayload.GetProperty("preferredLanguageCode").GetString());
    }

    [Fact]
    public async Task AuthenticatedUser_CanClearDataAndDeleteAccount()
    {
        using var client = _factory.CreateClient();

        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var checkingId = await CreateAccountAsync(client, "Personal Checking", "Checking", 1500m);
        var groceriesCategoryId = await CreateCategoryAsync(client, "Groceries", "Expense");

        var transactionResponse = await client.PostAsJsonAsync("/api/transactions", new
        {
            accountId = checkingId,
            categoryId = groceriesCategoryId,
            amount = 42.17m,
            type = "Expense",
            occurredOnUtc = "2026-04-10T08:00:00Z",
            note = "Weekly groceries"
        });
        Assert.Equal(HttpStatusCode.Created, transactionResponse.StatusCode);

        var budgetResponse = await client.PutAsJsonAsync("/api/budgets/2026/4", new
        {
            categoryLimits = new[]
            {
                new
                {
                    categoryId = groceriesCategoryId,
                    plannedAmount = 500m
                }
            }
        });
        Assert.Equal(HttpStatusCode.OK, budgetResponse.StatusCode);

        var clearResponse = await client.DeleteAsync("/api/settings/account-data");
        Assert.Equal(HttpStatusCode.NoContent, clearResponse.StatusCode);

        var profileAfterClearResponse = await client.GetAsync("/api/settings/profile");
        Assert.Equal(HttpStatusCode.OK, profileAfterClearResponse.StatusCode);

        var accountsAfterClearResponse = await client.GetAsync("/api/accounts");
        var accountsAfterClear = await accountsAfterClearResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, accountsAfterClear.GetArrayLength());

        var transactionsAfterClearResponse = await client.GetAsync("/api/transactions");
        var transactionsAfterClear = await transactionsAfterClearResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, transactionsAfterClear.GetArrayLength());

        var categoriesAfterClearResponse = await client.GetAsync("/api/categories");
        var categoriesAfterClear = await categoriesAfterClearResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, categoriesAfterClear.GetArrayLength());

        var deleteResponse = await client.DeleteAsync("/api/settings/account");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var profileAfterDeleteResponse = await client.GetAsync("/api/settings/profile");
        Assert.Equal(HttpStatusCode.NotFound, profileAfterDeleteResponse.StatusCode);
    }

    [Fact]
    public async Task AuthenticatedUser_CanReplaceTransferWithExpenseTransaction()
    {
        using var client = _factory.CreateClient();

        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var checkingId = await CreateAccountAsync(client, "Personal Checking", "Checking", 1500m);
        var jointId = await CreateAccountAsync(client, "Shared Household", "Joint", 600m);
        var groceriesCategoryId = await CreateCategoryAsync(client, "Groceries", "Expense");

        var createTransferResponse = await client.PostAsJsonAsync("/api/transactions", new
        {
            accountId = checkingId,
            destinationAccountId = jointId,
            amount = 200m,
            type = "Transfer",
            occurredOnUtc = "2026-04-06T08:00:00Z",
            note = "Move to shared account"
        });

        Assert.Equal(HttpStatusCode.Created, createTransferResponse.StatusCode);
        var createdTransfer = await createTransferResponse.Content.ReadFromJsonAsync<JsonElement>();
        var transferId = createdTransfer.GetProperty("id").GetGuid();

        var updateResponse = await client.PutAsJsonAsync($"/api/transactions/{transferId}", new
        {
            categoryId = groceriesCategoryId,
            amount = 75m,
            type = "Expense",
            occurredOnUtc = "2026-04-07T08:00:00Z",
            note = "Converted from transfer"
        });

        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updatedPayload = await updateResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Expense", updatedPayload.GetProperty("type").GetString());
        Assert.Equal(checkingId, updatedPayload.GetProperty("accountId").GetGuid());
        Assert.Equal(groceriesCategoryId, updatedPayload.GetProperty("categoryId").GetGuid());

        var checkingTransactionsResponse = await client.GetAsync($"/api/transactions?accountId={checkingId}");
        var checkingTransactions = await checkingTransactionsResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Single(checkingTransactions.EnumerateArray());
        Assert.Equal("Expense", checkingTransactions[0].GetProperty("type").GetString());

        var jointTransactionsResponse = await client.GetAsync($"/api/transactions?accountId={jointId}");
        var jointTransactions = await jointTransactionsResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, jointTransactions.GetArrayLength());
    }

    [Fact]
    public async Task AuthenticatedUser_CanMoveNonTransferTransactionToAnotherAccount()
    {
        using var client = _factory.CreateClient();

        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var checkingId = await CreateAccountAsync(client, "Personal Checking", "Checking", 1500m);
        var savingsId = await CreateAccountAsync(client, "Savings", "Savings", 600m);
        var groceriesCategoryId = await CreateCategoryAsync(client, "Groceries", "Expense");

        var createResponse = await client.PostAsJsonAsync("/api/transactions", new
        {
            accountId = checkingId,
            categoryId = groceriesCategoryId,
            amount = 42.17m,
            type = "Expense",
            occurredOnUtc = "2026-04-10T08:00:00Z",
            note = "Weekly groceries"
        });

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var createdPayload = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var transactionId = createdPayload.GetProperty("id").GetGuid();

        var moveResponse = await client.PostAsJsonAsync($"/api/transactions/{transactionId}/move-account", new
        {
            destinationAccountId = savingsId
        });

        Assert.Equal(HttpStatusCode.OK, moveResponse.StatusCode);
        var movedPayload = await moveResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(transactionId, movedPayload.GetProperty("id").GetGuid());
        Assert.Equal(savingsId, movedPayload.GetProperty("accountId").GetGuid());

        var checkingTransactionsResponse = await client.GetAsync($"/api/transactions?accountId={checkingId}");
        var checkingTransactions = await checkingTransactionsResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, checkingTransactions.GetArrayLength());

        var savingsTransactionsResponse = await client.GetAsync($"/api/transactions?accountId={savingsId}");
        var savingsTransactions = await savingsTransactionsResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Single(savingsTransactions.EnumerateArray());
        Assert.Equal(transactionId, savingsTransactions[0].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task AuthenticatedUser_CannotMoveTransferTransactionToAnotherAccount()
    {
        using var client = _factory.CreateClient();

        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var checkingId = await CreateAccountAsync(client, "Personal Checking", "Checking", 1500m);
        var savingsId = await CreateAccountAsync(client, "Savings", "Savings", 600m);
        var jointId = await CreateAccountAsync(client, "Shared Household", "Joint", 600m);

        var createResponse = await client.PostAsJsonAsync("/api/transactions", new
        {
            accountId = checkingId,
            destinationAccountId = savingsId,
            amount = 200m,
            type = "Transfer",
            occurredOnUtc = "2026-04-06T08:00:00Z",
            note = "Move to savings"
        });

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var createdPayload = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var transactionId = createdPayload.GetProperty("id").GetGuid();

        var moveResponse = await client.PostAsJsonAsync($"/api/transactions/{transactionId}/move-account", new
        {
            destinationAccountId = jointId
        });

        Assert.Equal(HttpStatusCode.BadRequest, moveResponse.StatusCode);

        var checkingTransactionsResponse = await client.GetAsync($"/api/transactions?accountId={checkingId}");
        var checkingTransactions = await checkingTransactionsResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Single(checkingTransactions.EnumerateArray());
        Assert.Equal(transactionId, checkingTransactions[0].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task AuthenticatedUser_CanFilterTransactionsAndGetTransactionById()
    {
        using var client = _factory.CreateClient();

        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var checkingId = await CreateAccountAsync(client, "Personal Checking", "Checking", 1500m);
        var groceriesCategoryId = await CreateCategoryAsync(client, "Groceries", "Expense");
        var salaryCategoryId = await CreateCategoryAsync(client, "Salary", "Income");

        await client.PostAsJsonAsync("/api/transactions", new
        {
            accountId = checkingId,
            categoryId = salaryCategoryId,
            amount = 3200m,
            type = "Income",
            occurredOnUtc = "2026-04-01T08:00:00Z",
            note = "Monthly salary"
        });

        var expenseResponse = await client.PostAsJsonAsync("/api/transactions", new
        {
            accountId = checkingId,
            categoryId = groceriesCategoryId,
            amount = 180m,
            type = "Expense",
            occurredOnUtc = "2026-04-05T08:00:00Z",
            note = "Weekly groceries"
        });

        Assert.Equal(HttpStatusCode.Created, expenseResponse.StatusCode);
        var expensePayload = await expenseResponse.Content.ReadFromJsonAsync<JsonElement>();
        var expenseId = expensePayload.GetProperty("id").GetGuid();

        var filteredResponse = await client.GetAsync($"/api/transactions?accountId={checkingId}&categoryId={groceriesCategoryId}&type=Expense&from=2026-04-01&to=2026-04-30");
        Assert.Equal(HttpStatusCode.OK, filteredResponse.StatusCode);

        var filteredPayload = await filteredResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Single(filteredPayload.EnumerateArray());
        Assert.Equal(expenseId, filteredPayload[0].GetProperty("id").GetGuid());

        var transactionResponse = await client.GetAsync($"/api/transactions/{expenseId}");
        Assert.Equal(HttpStatusCode.OK, transactionResponse.StatusCode);

        var transactionPayload = await transactionResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Expense", transactionPayload.GetProperty("type").GetString());
        Assert.Equal(groceriesCategoryId, transactionPayload.GetProperty("categoryId").GetGuid());
    }

    [Fact]
    public async Task AuthenticatedUser_CanFilterTransactionsByAmountNoteAndUncategorizedOnServer()
    {
        using var client = _factory.CreateClient();

        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var checkingId = await CreateAccountAsync(client, "Personal Checking", "Checking", 1500m);
        var savingsId = await CreateAccountAsync(client, "Savings", "Savings", 600m);
        var groceriesCategoryId = await CreateCategoryAsync(client, "Groceries", "Expense");

        var matchingResponse = await client.PostAsJsonAsync("/api/transactions", new
        {
            accountId = checkingId,
            amount = 42.17m,
            type = "Expense",
            occurredOnUtc = "2026-04-10T08:00:00Z",
            note = "Corner Market"
        });
        Assert.Equal(HttpStatusCode.Created, matchingResponse.StatusCode);
        var matchingPayload = await matchingResponse.Content.ReadFromJsonAsync<JsonElement>();
        var matchingId = matchingPayload.GetProperty("id").GetGuid();

        await client.PostAsJsonAsync("/api/transactions", new
        {
            accountId = checkingId,
            categoryId = groceriesCategoryId,
            amount = 42.17m,
            type = "Expense",
            occurredOnUtc = "2026-04-11T08:00:00Z",
            note = "Corner Market categorized"
        });
        await client.PostAsJsonAsync("/api/transactions", new
        {
            accountId = savingsId,
            amount = 500m,
            type = "Income",
            occurredOnUtc = "2026-04-12T08:00:00Z",
            note = "Payroll"
        });

        var filteredResponse = await client.GetAsync($"/api/transactions?accountId={checkingId}&accountId={savingsId}&minAmount=40&maxAmount=50&q=market&uncategorizedOnly=true");
        Assert.Equal(HttpStatusCode.OK, filteredResponse.StatusCode);

        var filteredPayload = await filteredResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Single(filteredPayload.EnumerateArray());
        Assert.Equal(matchingId, filteredPayload[0].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task AuthenticatedUser_CanUpdateAccountButCannotDeleteAccountWithTransactions()
    {
        using var client = _factory.CreateClient();

        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var checkingId = await CreateAccountAsync(client, "Personal Checking", "Checking", 1500m);
        var groceriesCategoryId = await CreateCategoryAsync(client, "Groceries", "Expense");

        var updateResponse = await client.PutAsJsonAsync($"/api/accounts/{checkingId}", new
        {
            name = "Household Checking",
            type = "Cash",
            currencyCode = "eur",
            openingBalance = 1200m,
            isActive = false
        });

        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updatePayload = await updateResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Household Checking", updatePayload.GetProperty("name").GetString());
        Assert.Equal("Cash", updatePayload.GetProperty("type").GetString());
        Assert.Equal("EUR", updatePayload.GetProperty("currencyCode").GetString());
        Assert.False(updatePayload.GetProperty("isActive").GetBoolean());

        await client.PostAsJsonAsync("/api/transactions", new
        {
            accountId = checkingId,
            categoryId = groceriesCategoryId,
            amount = 15.25m,
            type = "Expense",
            occurredOnUtc = "2026-04-09T12:00:00Z",
            note = "Locks delete"
        });

        var deleteResponse = await client.DeleteAsync($"/api/accounts/{checkingId}");
        Assert.Equal(HttpStatusCode.Conflict, deleteResponse.StatusCode);
    }

    [Fact]
    public async Task AuthenticatedUser_CanUpdateAndDeleteUnusedCategory()
    {
        using var client = _factory.CreateClient();

        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var categoryId = await CreateCategoryAsync(client, "Groceries", "Expense");

        var updateResponse = await client.PutAsJsonAsync($"/api/categories/{categoryId}", new
        {
            name = "Food",
            kind = "Income",
            color = "#00AA88",
            isSystem = true
        });

        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updatePayload = await updateResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Food", updatePayload.GetProperty("name").GetString());
        Assert.Equal("Income", updatePayload.GetProperty("kind").GetString());
        Assert.Equal("#00AA88", updatePayload.GetProperty("color").GetString());
        Assert.True(updatePayload.GetProperty("isSystem").GetBoolean());

        var getResponse = await client.GetAsync($"/api/categories/{categoryId}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        var deleteResponse = await client.DeleteAsync($"/api/categories/{categoryId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var getAfterDeleteResponse = await client.GetAsync($"/api/categories/{categoryId}");
        Assert.Equal(HttpStatusCode.NotFound, getAfterDeleteResponse.StatusCode);
    }

    [Fact]
    public async Task AuthenticatedUser_CanSaveAndRemoveEncryptedAiProviderKeys()
    {
        using var client = _factory.CreateClient();

        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var saveResponse = await client.PutAsJsonAsync("/api/settings/ai/openai", new
        {
            apiKey = "sk-test-openai-secret-123456"
        });

        Assert.Equal(HttpStatusCode.OK, saveResponse.StatusCode);
        var savePayload = await saveResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(savePayload.GetProperty("providers").GetProperty("openAi").GetProperty("isConfigured").GetBoolean());
        Assert.Equal("OpenAi", savePayload.GetProperty("defaultProvider").GetString());
        Assert.DoesNotContain("sk-test-openai-secret-123456", savePayload.ToString(), StringComparison.Ordinal);

        var statusResponse = await client.GetAsync("/api/settings/ai");
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        var statusPayload = await statusResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(statusPayload.GetProperty("providers").GetProperty("openAi").GetProperty("isConfigured").GetBoolean());
        Assert.Equal("...3456", statusPayload.GetProperty("providers").GetProperty("openAi").GetProperty("maskedKey").GetString());
        Assert.False(statusPayload.GetProperty("providers").GetProperty("openAiCompatible").GetProperty("isConfigured").GetBoolean());
        Assert.DoesNotContain("sk-test-openai-secret-123456", statusPayload.ToString(), StringComparison.Ordinal);

        var deleteResponse = await client.DeleteAsync("/api/settings/ai/openai");
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);
        var deletePayload = await deleteResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(deletePayload.GetProperty("providers").GetProperty("openAi").GetProperty("isConfigured").GetBoolean());
    }

    [Fact]
    public async Task AuthenticatedUser_CanSaveOpenAiCompatibleProviderSettings()
    {
        using var client = _factory.CreateClient();

        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var saveResponse = await client.PutAsJsonAsync("/api/settings/ai/openai-compatible", new
        {
            apiKey = "sk-test-compatible-secret-123456",
            baseUrl = "https://api.synthetic.example/v1/",
            model = "synthetic-finance-1"
        });

        Assert.Equal(HttpStatusCode.OK, saveResponse.StatusCode);
        var savePayload = await saveResponse.Content.ReadFromJsonAsync<JsonElement>();
        var compatibleProvider = savePayload.GetProperty("providers").GetProperty("openAiCompatible");
        Assert.True(compatibleProvider.GetProperty("isConfigured").GetBoolean());
        Assert.Equal("https://api.synthetic.example/v1", compatibleProvider.GetProperty("baseUrl").GetString());
        Assert.Equal("synthetic-finance-1", compatibleProvider.GetProperty("model").GetString());
        Assert.Equal("OpenAiCompatible", savePayload.GetProperty("defaultProvider").GetString());
        Assert.DoesNotContain("sk-test-compatible-secret-123456", savePayload.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AiProviderKeyUpdates_DoNotImplicitlyChangeExistingDefaultProvider()
    {
        using var client = _factory.CreateClient();

        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        await client.PutAsJsonAsync("/api/settings/ai/openai", new { apiKey = "sk-test-openai-secret-123456" });
        await client.PutAsJsonAsync("/api/settings/ai/anthropic", new { apiKey = "sk-test-anthropic-secret-abcdef" });
        await client.PutAsJsonAsync("/api/settings/ai/default-provider", new { provider = "Anthropic" });

        var saveResponse = await client.PutAsJsonAsync("/api/settings/ai/openai", new { apiKey = "sk-test-openai-secret-654321" });

        Assert.Equal(HttpStatusCode.OK, saveResponse.StatusCode);
        var payload = await saveResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Anthropic", payload.GetProperty("defaultProvider").GetString());
    }

    [Fact]
    public async Task AiDefaultProvider_RequiresConfiguredCredential()
    {
        using var client = _factory.CreateClient();

        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var updateResponse = await client.PutAsJsonAsync("/api/settings/ai/default-provider", new
        {
            provider = "Anthropic"
        });

        Assert.Equal(HttpStatusCode.BadRequest, updateResponse.StatusCode);
    }

    [Fact]
    public async Task AiProviderKeyRemoval_ClearsDeletedDefaultProvider()
    {
        using var client = _factory.CreateClient();

        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        await client.PutAsJsonAsync("/api/settings/ai/openai", new { apiKey = "sk-test-openai-secret-123456" });

        var deleteResponse = await client.DeleteAsync("/api/settings/ai/openai");

        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);
        var payload = await deleteResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(payload.GetProperty("providers").GetProperty("openAi").GetProperty("isConfigured").GetBoolean());
        Assert.Null(payload.GetProperty("defaultProvider").GetString());
    }

    [Fact]
    public async Task AiProviderKeyRemoval_SkipsIncompleteOpenAiCompatibleFallback()
    {
        using var client = _factory.CreateClient();

        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        await client.PutAsJsonAsync("/api/settings/ai/openai", new { apiKey = "sk-test-openai-secret-123456" });
        await client.PutAsJsonAsync("/api/settings/ai/openai-compatible", new
        {
            apiKey = "sk-test-compatible-secret-123456",
            baseUrl = "https://api.synthetic.example/v1"
        });

        var deleteResponse = await client.DeleteAsync("/api/settings/ai/openai");

        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);
        var payload = await deleteResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(payload.GetProperty("providers").GetProperty("openAiCompatible").GetProperty("isConfigured").GetBoolean());
        Assert.Null(payload.GetProperty("defaultProvider").GetString());
    }

    [Fact]
    public async Task AuthenticatedUser_CanChooseDefaultAiProvider()
    {
        using var client = _factory.CreateClient();

        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        await client.PutAsJsonAsync("/api/settings/ai/anthropic", new { apiKey = "sk-test-anthropic-secret-abcdef" });

        var updateResponse = await client.PutAsJsonAsync("/api/settings/ai/default-provider", new
        {
            provider = "Anthropic"
        });

        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var payload = await updateResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Anthropic", payload.GetProperty("defaultProvider").GetString());
    }

    [Fact]
    public async Task AuthenticatedUser_CanManageImportRules()
    {
        using var client = _factory.CreateClient();

        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var groceriesCategoryId = await CreateCategoryAsync(client, "Groceries", "Expense");

        var createResponse = await client.PostAsJsonAsync("/api/import-rules", new
        {
            name = "Market groceries",
            matchField = "Note",
            matchOperator = "Contains",
            matchValue = "Market",
            assignCategoryId = groceriesCategoryId,
            assignTransactionType = "Expense",
            priority = 10,
            isActive = true
        });

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var ruleId = created.GetProperty("id").GetGuid();
        Assert.Equal("Market groceries", created.GetProperty("name").GetString());
        Assert.Equal("Note", created.GetProperty("matchField").GetString());
        Assert.Equal("Contains", created.GetProperty("matchOperator").GetString());
        Assert.Equal("Market", created.GetProperty("matchValue").GetString());
        Assert.Equal(groceriesCategoryId, created.GetProperty("assignCategoryId").GetGuid());
        Assert.Equal("Expense", created.GetProperty("assignTransactionType").GetString());
        Assert.Equal(10, created.GetProperty("priority").GetInt32());
        Assert.True(created.GetProperty("isActive").GetBoolean());

        var getCreatedResponse = await client.GetAsync(createResponse.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, getCreatedResponse.StatusCode);
        var fetched = await getCreatedResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ruleId, fetched.GetProperty("id").GetGuid());

        var listResponse = await client.GetAsync("/api/import-rules");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var listed = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Single(listed.EnumerateArray());

        var updateResponse = await client.PutAsJsonAsync($"/api/import-rules/{ruleId}", new
        {
            name = "Market disabled",
            matchField = "Note",
            matchOperator = "Contains",
            matchValue = "Imported: Market",
            assignCategoryId = groceriesCategoryId,
            assignTransactionType = "Expense",
            priority = 5,
            isActive = false
        });

        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await updateResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Market disabled", updated.GetProperty("name").GetString());
        Assert.False(updated.GetProperty("isActive").GetBoolean());
        Assert.Equal(5, updated.GetProperty("priority").GetInt32());

        var deleteResponse = await client.DeleteAsync($"/api/import-rules/{ruleId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var emptyListResponse = await client.GetAsync("/api/import-rules");
        var emptyList = await emptyListResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(emptyList.EnumerateArray());
    }

    [Fact]
    public async Task ImportRules_ValidateRuleShapeAndCategoryOwnership()
    {
        using var ownerClient = _factory.CreateClient();
        var ownerAuth = await RegisterAndAuthenticateAsync(ownerClient);
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerAuth.AccessToken);

        using var otherClient = _factory.CreateClient();
        var otherAuth = await RegisterAndAuthenticateAsync(otherClient);
        otherClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", otherAuth.AccessToken);

        var ownerCategoryId = await CreateCategoryAsync(ownerClient, "Owner groceries", "Expense");
        var otherCategoryId = await CreateCategoryAsync(otherClient, "Other groceries", "Expense");

        var unsupportedFieldResponse = await ownerClient.PostAsJsonAsync("/api/import-rules", new
        {
            name = "Bad field",
            matchField = "Amount",
            matchOperator = "Contains",
            matchValue = "Market",
            assignCategoryId = ownerCategoryId,
            assignTransactionType = "Expense",
            priority = 1,
            isActive = true
        });

        Assert.Equal(HttpStatusCode.BadRequest, unsupportedFieldResponse.StatusCode);

        var undefinedNumericFieldResponse = await ownerClient.PostAsJsonAsync("/api/import-rules", new
        {
            name = "Bad numeric field",
            matchField = "999",
            matchOperator = "Contains",
            matchValue = "Market",
            assignCategoryId = ownerCategoryId,
            assignTransactionType = "Expense",
            priority = 1,
            isActive = true
        });

        Assert.Equal(HttpStatusCode.BadRequest, undefinedNumericFieldResponse.StatusCode);
        var undefinedNumericFieldProblem = await undefinedNumericFieldResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(undefinedNumericFieldProblem.GetProperty("errors").TryGetProperty("matchField", out _));

        var undefinedNumericOperatorResponse = await ownerClient.PostAsJsonAsync("/api/import-rules", new
        {
            name = "Bad numeric operator",
            matchField = "Note",
            matchOperator = "999",
            matchValue = "Market",
            assignCategoryId = ownerCategoryId,
            assignTransactionType = "Expense",
            priority = 1,
            isActive = true
        });

        Assert.Equal(HttpStatusCode.BadRequest, undefinedNumericOperatorResponse.StatusCode);
        var undefinedNumericOperatorProblem = await undefinedNumericOperatorResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(undefinedNumericOperatorProblem.GetProperty("errors").TryGetProperty("matchOperator", out _));

        var foreignCategoryResponse = await ownerClient.PostAsJsonAsync("/api/import-rules", new
        {
            name = "Foreign category",
            matchField = "Note",
            matchOperator = "Contains",
            matchValue = "Market",
            assignCategoryId = otherCategoryId,
            assignTransactionType = "Expense",
            priority = 1,
            isActive = true
        });

        Assert.Equal(HttpStatusCode.NotFound, foreignCategoryResponse.StatusCode);
    }

    [Fact]
    public async Task Categories_DeleteReturnsBadRequestWhenImportRulesUseCategory()
    {
        using var client = _factory.CreateClient();

        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var groceriesCategoryId = await CreateCategoryAsync(client, "Groceries", "Expense");
        var createRuleResponse = await client.PostAsJsonAsync("/api/import-rules", new
        {
            name = "Market groceries",
            matchField = "Note",
            matchOperator = "Contains",
            matchValue = "Market",
            assignCategoryId = groceriesCategoryId,
            assignTransactionType = "Expense",
            priority = 10,
            isActive = true
        });

        Assert.Equal(HttpStatusCode.Created, createRuleResponse.StatusCode);

        var deleteResponse = await client.DeleteAsync($"/api/categories/{groceriesCategoryId}");

        Assert.Equal(HttpStatusCode.BadRequest, deleteResponse.StatusCode);
        var problem = await deleteResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Category is used by import rules", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task AuthenticatedUser_CanCommitReviewedMonthlyReportDrafts()
    {
        using var client = _factory.CreateClient();

        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var checkingId = await CreateAccountAsync(client, "Personal Checking", "Checking", 1500m);
        var groceriesCategoryId = await CreateCategoryAsync(client, "Groceries", "Expense");

        var commitResponse = await client.PostAsJsonAsync("/api/imports/monthly-report/commit", new
        {
            transactions = new[]
            {
                new
                {
                    sourceId = "row-1",
                    accountId = checkingId,
                    categoryId = groceriesCategoryId,
                    amount = 42.17m,
                    type = "Expense",
                    occurredOnUtc = "2026-04-10T12:00:00Z",
                    note = "Imported: Market"
                }
            }
        });

        Assert.Equal(HttpStatusCode.Created, commitResponse.StatusCode);
        var payload = await commitResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, payload.GetProperty("created").GetArrayLength());

        var transactionsResponse = await client.GetAsync($"/api/transactions?accountId={checkingId}&from=2026-04-01&to=2026-04-30");
        var transactionsPayload = await transactionsResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, transactionsPayload.GetArrayLength());
        Assert.Equal(42.17m, transactionsPayload[0].GetProperty("amount").GetDecimal());
    }

    [Fact]
    public async Task MonthlyReportCommit_RejectsInvalidDraftWithoutPartialSave()
    {
        using var client = _factory.CreateClient();

        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var checkingId = await CreateAccountAsync(client, "Personal Checking", "Checking", 1500m);
        var groceriesCategoryId = await CreateCategoryAsync(client, "Groceries", "Expense");

        var commitResponse = await client.PostAsJsonAsync("/api/imports/monthly-report/commit", new
        {
            transactions = new[]
            {
                new
                {
                    sourceId = "row-valid",
                    accountId = checkingId,
                    categoryId = groceriesCategoryId,
                    amount = 15.25m,
                    type = "Expense",
                    occurredOnUtc = "2026-04-09T12:00:00Z",
                    note = "Valid draft before invalid one"
                },
                new
                {
                    sourceId = "row-invalid",
                    accountId = checkingId,
                    categoryId = Guid.NewGuid(),
                    amount = 42.17m,
                    type = "Expense",
                    occurredOnUtc = "2026-04-10T12:00:00Z",
                    note = "Invalid category"
                }
            }
        });

        Assert.Equal(HttpStatusCode.NotFound, commitResponse.StatusCode);

        var transactionsResponse = await client.GetAsync($"/api/transactions?accountId={checkingId}");
        var transactionsPayload = await transactionsResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, transactionsPayload.GetArrayLength());
    }

    [Fact]
    public async Task MonthlyReportCommit_RejectsEmptyAccountAndDefaultDateBeforeProcessing()
    {
        using var client = _factory.CreateClient();

        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var commitResponse = await client.PostAsJsonAsync("/api/imports/monthly-report/commit", new
        {
            transactions = new[]
            {
                new
                {
                    sourceId = "row-1",
                    accountId = Guid.Empty,
                    amount = 42.17m,
                    type = "Expense",
                    occurredOnUtc = "0001-01-01T00:00:00Z",
                    note = "Invalid identifiers"
                }
            }
        });

        Assert.Equal(HttpStatusCode.BadRequest, commitResponse.StatusCode);
    }

    [Fact]
    public async Task MonthlyReportCommit_RejectsMissingOrDuplicateSourceIdsBeforeDuplicateAcceptance()
    {
        using var client = _factory.CreateClient();

        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var checkingId = await CreateAccountAsync(client, "Personal Checking", "Checking", 1500m);
        var groceriesCategoryId = await CreateCategoryAsync(client, "Groceries", "Expense");

        var duplicateSourceIdResponse = await client.PostAsJsonAsync("/api/imports/monthly-report/commit", new
        {
            transactions = new[]
            {
                new
                {
                    sourceId = "row-duplicate",
                    accountId = checkingId,
                    categoryId = groceriesCategoryId,
                    amount = 15.25m,
                    type = "Expense",
                    occurredOnUtc = "2026-04-09T12:00:00Z",
                    note = "First duplicate source id"
                },
                new
                {
                    sourceId = "ROW-DUPLICATE",
                    accountId = checkingId,
                    categoryId = groceriesCategoryId,
                    amount = 42.17m,
                    type = "Expense",
                    occurredOnUtc = "2026-04-10T12:00:00Z",
                    note = "Second duplicate source id"
                }
            },
            acceptedDuplicateSourceIds = new[] { "row-duplicate" }
        });

        Assert.Equal(HttpStatusCode.BadRequest, duplicateSourceIdResponse.StatusCode);

        var transactionsAfterDuplicateResponse = await client.GetAsync($"/api/transactions?accountId={checkingId}");
        var transactionsAfterDuplicatePayload = await transactionsAfterDuplicateResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, transactionsAfterDuplicatePayload.GetArrayLength());

        var missingSourceIdResponse = await client.PostAsJsonAsync("/api/imports/monthly-report/commit", new
        {
            transactions = new[]
            {
                new
                {
                    sourceId = "   ",
                    accountId = checkingId,
                    categoryId = groceriesCategoryId,
                    amount = 18.50m,
                    type = "Expense",
                    occurredOnUtc = "2026-04-11T12:00:00Z",
                    note = "Missing source id"
                }
            }
        });

        Assert.Equal(HttpStatusCode.BadRequest, missingSourceIdResponse.StatusCode);

        var transactionsAfterMissingResponse = await client.GetAsync($"/api/transactions?accountId={checkingId}");
        var transactionsAfterMissingPayload = await transactionsAfterMissingResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, transactionsAfterMissingPayload.GetArrayLength());
    }

    [Fact]
    public async Task MonthlyReportCommit_RejectsMissingOrDuplicateAcceptedDuplicateSourceIds()
    {
        using var client = _factory.CreateClient();

        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var checkingId = await CreateAccountAsync(client, "Personal Checking", "Checking", 1500m);
        var groceriesCategoryId = await CreateCategoryAsync(client, "Groceries", "Expense");

        var blankAcceptedIdResponse = await client.PostAsJsonAsync("/api/imports/monthly-report/commit", new
        {
            transactions = new[]
            {
                new
                {
                    sourceId = "row-1",
                    accountId = checkingId,
                    categoryId = groceriesCategoryId,
                    amount = 15.25m,
                    type = "Expense",
                    occurredOnUtc = "2026-04-09T12:00:00Z",
                    note = "Valid draft with invalid accepted duplicate id"
                }
            },
            acceptedDuplicateSourceIds = new[] { " " }
        });

        Assert.Equal(HttpStatusCode.BadRequest, blankAcceptedIdResponse.StatusCode);

        var duplicateAcceptedIdResponse = await client.PostAsJsonAsync("/api/imports/monthly-report/commit", new
        {
            transactions = new[]
            {
                new
                {
                    sourceId = "row-1",
                    accountId = checkingId,
                    categoryId = groceriesCategoryId,
                    amount = 18.50m,
                    type = "Expense",
                    occurredOnUtc = "2026-04-11T12:00:00Z",
                    note = "Valid draft with duplicate accepted duplicate id"
                }
            },
            acceptedDuplicateSourceIds = new[] { "row-1", "ROW-1" }
        });

        Assert.Equal(HttpStatusCode.BadRequest, duplicateAcceptedIdResponse.StatusCode);

        var transactionsResponse = await client.GetAsync($"/api/transactions?accountId={checkingId}");
        var transactionsPayload = await transactionsResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, transactionsPayload.GetArrayLength());
    }

    [Theory]
    [InlineData("SAINT NEKTARIOS GREEK 607-498-5285 NY3000089192230031772899", "text/csv")]
    [InlineData("Bank export", "application/vnd.ms-excel")]
    [InlineData("Bank export", "application/octet-stream")]
    [InlineData("Bank export", "text/plain")]
    [InlineData("\"Market, downtown | groceries\"", "text/csv")]
    public async Task CsvPreview_PreservesOriginalCsvHeadersAndQuotedCells(string memo, string contentType)
    {
        using var client = _factory.CreateClient();
        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        var accountId = await CreateAccountAsync(client, "CSV Checking", "Checking", 0m);

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(accountId.ToString()), "accountId");
        form.Add(new StringContent("date"), "dateColumn");
        form.Add(new StringContent("Amount"), "amountColumn");
        form.Add(new StringContent("memo"), "descriptionColumn");
        using var file = new ByteArrayContent(Encoding.UTF8.GetBytes(
            $"date,payment,number,payee,memo,Amount,Category,tags\n3/7/2026,,,Payee,{memo},133,Services,\n"));
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", "statement.csv");

        var response = await client.PostAsync("/api/imports/monthly-report/csv-preview", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var transaction = Assert.Single(payload.GetProperty("transactions").EnumerateArray());
        Assert.Equal(133m, transaction.GetProperty("amount").GetDecimal());
        Assert.Equal("Income", transaction.GetProperty("type").GetString());
        Assert.Equal("2026-03-07", transaction.GetProperty("occurredOnUtc").GetString()![..10]);
        Assert.Equal(memo.Trim('"'), transaction.GetProperty("note").GetString());

        // CSV preview and commit must work without configuring an AI provider.
        var commit = await client.PostAsJsonAsync("/api/imports/monthly-report/commit", new
        {
            transactions = new[] { transaction }
        });
        Assert.Equal(HttpStatusCode.Created, commit.StatusCode);
        var saved = await commit.Content.ReadFromJsonAsync<JsonElement>();
        var created = Assert.Single(saved.GetProperty("created").EnumerateArray());
        Assert.Equal(accountId, created.GetProperty("accountId").GetGuid());
        Assert.Equal(133m, created.GetProperty("amount").GetDecimal());
        Assert.Equal(memo.Trim('"'), created.GetProperty("note").GetString());
    }

    [Theory]
    [InlineData(133, true, "Expense")]
    [InlineData(-133, true, "Income")]
    [InlineData(133, false, "Income")]
    [InlineData(-133, false, "Expense")]
    public async Task CsvPreview_InvertsAmountsBeforePreviewAndCommit(decimal inputAmount, bool invertAmounts, string expectedType)
    {
        const string memo = "Credit card transaction";
        const string contentType = "text/csv";
        using var client = _factory.CreateClient();
        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        var accountId = await CreateAccountAsync(client, "CSV Checking", "Checking", 0m);

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(accountId.ToString()), "accountId");
        form.Add(new StringContent("date"), "dateColumn");
        form.Add(new StringContent("Amount"), "amountColumn");
        form.Add(new StringContent("memo"), "descriptionColumn");
        form.Add(new StringContent(invertAmounts.ToString()), "invertAmounts");
        using var file = new ByteArrayContent(Encoding.UTF8.GetBytes(
            $"date,payment,number,payee,memo,Amount,Category,tags\n3/7/2026,,,Payee,{memo},{inputAmount},Services,\n"));
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", "statement.csv");

        var response = await client.PostAsync("/api/imports/monthly-report/csv-preview", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var transaction = Assert.Single(payload.GetProperty("transactions").EnumerateArray());
        Assert.Equal(133m, transaction.GetProperty("amount").GetDecimal());
        Assert.Equal(expectedType, transaction.GetProperty("type").GetString());
        Assert.Equal("2026-03-07", transaction.GetProperty("occurredOnUtc").GetString()![..10]);
        Assert.Equal(memo.Trim('"'), transaction.GetProperty("note").GetString());

        // CSV preview and commit must work without configuring an AI provider.
        var commit = await client.PostAsJsonAsync("/api/imports/monthly-report/commit", new
        {
            transactions = new[] { transaction }
        });
        Assert.Equal(HttpStatusCode.Created, commit.StatusCode);
        var saved = await commit.Content.ReadFromJsonAsync<JsonElement>();
        var created = Assert.Single(saved.GetProperty("created").EnumerateArray());
        Assert.Equal(accountId, created.GetProperty("accountId").GetGuid());
        Assert.Equal(133m, created.GetProperty("amount").GetDecimal());
        Assert.Equal(memo.Trim('"'), created.GetProperty("note").GetString());
    }

    [Fact]
    public async Task AuthenticatedUser_CanAnalyzeCsvMonthlyReportIntoDrafts()
    {
        using var client = _factory.CreateClient();

        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var checkingId = await CreateAccountAsync(client, "Personal Checking", "Checking", 1500m);
        var groceriesCategoryId = await CreateCategoryAsync(client, "Groceries", "Expense");

        await client.PutAsJsonAsync("/api/settings/ai/openai", new { apiKey = "sk-test-openai-secret-123456" });

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(checkingId.ToString()), "accountId");
        form.Add(new StringContent("2026-04"), "month");
        form.Add(new StringContent("OpenAi"), "provider");
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes($"Date,Description,Amount\n2026-04-10,Market,-42.17\ncategory:{groceriesCategoryId}\n"));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(fileContent, "file", "statement.csv");

        var job = await AnalyzeMonthlyReportJobAsync(client, form);
        Assert.Equal("completed", job.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(job.GetProperty("statusMessage").GetString()));
        Assert.Equal(32, job.GetProperty("generatedOutputCharacters").GetInt32());
        Assert.Equal(JsonValueKind.Object, job.GetProperty("usage").ValueKind);
        Assert.Equal(160, job.GetProperty("usage").GetProperty("totalTokens").GetInt32());
        Assert.Equal(JsonValueKind.Object, job.GetProperty("analysis").ValueKind);
        var payload = job.GetProperty("analysis");
        Assert.Equal(1, payload.GetProperty("transactions").GetArrayLength());
        Assert.Equal(checkingId, payload.GetProperty("transactions")[0].GetProperty("accountId").GetGuid());
        Assert.Equal(groceriesCategoryId, payload.GetProperty("transactions")[0].GetProperty("categoryId").GetGuid());
        Assert.Equal(42.17m, payload.GetProperty("transactions")[0].GetProperty("amount").GetDecimal());
    }

    [Fact]
    public async Task MonthlyReportAnalyze_RejectsBlankSourceIds()
    {
        using var client = _factory.CreateClient();

        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var checkingId = await CreateAccountAsync(client, "Personal Checking", "Checking", 1500m);
        await client.PutAsJsonAsync("/api/settings/ai/openai", new { apiKey = "sk-test-openai-secret-123456" });

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(checkingId.ToString()), "accountId");
        form.Add(new StringContent("2026-04"), "month");
        form.Add(new StringContent("OpenAi"), "provider");
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("Date,Description,Amount\n2026-04-10,Market,-42.17\nsource:<blank>\n"));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(fileContent, "file", "statement.csv");

        var job = await AnalyzeMonthlyReportJobAsync(client, form);

        Assert.Equal("failed", job.GetProperty("status").GetString());
        Assert.Contains("malformed transaction draft", job.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MonthlyReportAnalyze_RejectsDuplicateSourceIds()
    {
        using var client = _factory.CreateClient();

        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var checkingId = await CreateAccountAsync(client, "Personal Checking", "Checking", 1500m);
        await client.PutAsJsonAsync("/api/settings/ai/openai", new { apiKey = "sk-test-openai-secret-123456" });

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(checkingId.ToString()), "accountId");
        form.Add(new StringContent("2026-04"), "month");
        form.Add(new StringContent("OpenAi"), "provider");
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("Date,Description,Amount\n2026-04-10,Market,-42.17\nduplicate-source\n"));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(fileContent, "file", "statement.csv");

        var job = await AnalyzeMonthlyReportJobAsync(client, form);

        Assert.Equal("failed", job.GetProperty("status").GetString());
        Assert.Contains("malformed transaction draft", job.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MonthlyReportAnalyze_SavesRawAiOutputForRetryAfterParseFailure()
    {
        using var client = _factory.CreateClient();

        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var checkingId = await CreateAccountAsync(client, "Personal Checking", "Checking", 1500m);
        await client.PutAsJsonAsync("/api/settings/ai/openai", new { apiKey = "sk-test-openai-secret-123456" });

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(checkingId.ToString()), "accountId");
        form.Add(new StringContent("2026-04"), "month");
        form.Add(new StringContent("OpenAi"), "provider");
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("Date,Description,Amount\nparse-error-raw-valid\n"));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(fileContent, "file", "statement.csv");

        var failedJob = await AnalyzeMonthlyReportJobAsync(client, form);

        Assert.Equal("failed", failedJob.GetProperty("status").GetString());
        Assert.True(failedJob.GetProperty("hasRawAiOutput").GetBoolean());

        var downloadResponse = await client.GetAsync($"/api/imports/monthly-report/analyze/{failedJob.GetProperty("jobId").GetGuid()}/raw-output");

        Assert.Equal(HttpStatusCode.OK, downloadResponse.StatusCode);
        Assert.Equal("application/json; charset=utf-8", downloadResponse.Content.Headers.ContentType?.ToString());
        Assert.Contains($"ledgerra-ai-output-{failedJob.GetProperty("jobId").GetGuid()}.json", downloadResponse.Content.Headers.ContentDisposition?.FileName);
        Assert.Contains("statement-2026-004-001", await downloadResponse.Content.ReadAsStringAsync());

        var retryResponse = await client.PostAsync($"/api/imports/monthly-report/analyze/{failedJob.GetProperty("jobId").GetGuid()}/retry-parse", null);

        Assert.Equal(HttpStatusCode.OK, retryResponse.StatusCode);
        var retriedJob = await retryResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("completed", retriedJob.GetProperty("status").GetString());
        Assert.Equal(1, retriedJob.GetProperty("analysis").GetProperty("transactions").GetArrayLength());
        Assert.Equal(160, retriedJob.GetProperty("usage").GetProperty("totalTokens").GetInt32());
    }

    [Fact]
    public async Task MonthlyReportAnalyze_RetryParseKeepsFailureStateForInvalidSavedAiOutput()
    {
        using var client = _factory.CreateClient();

        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var checkingId = await CreateAccountAsync(client, "Personal Checking", "Checking", 1500m);
        await client.PutAsJsonAsync("/api/settings/ai/openai", new { apiKey = "sk-test-openai-secret-123456" });

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(checkingId.ToString()), "accountId");
        form.Add(new StringContent("2026-04"), "month");
        form.Add(new StringContent("OpenAi"), "provider");
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("Date,Description,Amount\nparse-error-raw-invalid\n"));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(fileContent, "file", "statement.csv");

        var failedJob = await AnalyzeMonthlyReportJobAsync(client, form);

        Assert.Equal("failed", failedJob.GetProperty("status").GetString());
        Assert.True(failedJob.GetProperty("hasRawAiOutput").GetBoolean());

        var retryResponse = await client.PostAsync($"/api/imports/monthly-report/analyze/{failedJob.GetProperty("jobId").GetGuid()}/retry-parse", null);

        Assert.Equal(HttpStatusCode.OK, retryResponse.StatusCode);
        var retriedJob = await retryResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("failed", retriedJob.GetProperty("status").GetString());
        Assert.Equal("Saved AI output parse failed.", retriedJob.GetProperty("statusMessage").GetString());
        Assert.True(retriedJob.GetProperty("hasRawAiOutput").GetBoolean());
        Assert.True(retriedJob.GetProperty("generatedOutputCharacters").GetInt32() > 0);
        Assert.Equal(160, retriedJob.GetProperty("usage").GetProperty("totalTokens").GetInt32());
    }

    [Fact]
    public async Task MonthlyReportAnalyze_AppliesActiveImportRuleMetadata()
    {
        using var client = _factory.CreateClient();

        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var checkingId = await CreateAccountAsync(client, "Personal Checking", "Checking", 1500m);
        var groceriesCategoryId = await CreateCategoryAsync(client, "Groceries", "Expense");

        await client.PutAsJsonAsync("/api/settings/ai/openai", new { apiKey = "sk-test-openai-secret-123456" });
        var ruleResponse = await client.PostAsJsonAsync("/api/import-rules", new
        {
            name = "Market groceries",
            matchField = "Note",
            matchOperator = "Contains",
            matchValue = "market",
            assignCategoryId = groceriesCategoryId,
            assignTransactionType = "Expense",
            priority = 1,
            isActive = true
        });
        var rulePayload = await ruleResponse.Content.ReadFromJsonAsync<JsonElement>();
        var ruleId = rulePayload.GetProperty("id").GetGuid();

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(checkingId.ToString()), "accountId");
        form.Add(new StringContent("2026-04"), "month");
        form.Add(new StringContent("OpenAi"), "provider");
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("Date,Description,Amount\n2026-04-10,Market,-42.17\n"));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(fileContent, "file", "statement.csv");

        var payload = await AnalyzeMonthlyReportAsync(client, form);
        var draft = payload.GetProperty("transactions")[0];
        Assert.Equal(groceriesCategoryId, draft.GetProperty("categoryId").GetGuid());
        Assert.Equal("Expense", draft.GetProperty("type").GetString());
        Assert.Equal(ruleId, draft.GetProperty("appliedRuleId").GetGuid());
        Assert.Equal("Market groceries", draft.GetProperty("appliedRuleName").GetString());
        Assert.True(draft.GetProperty("isSelectedByDefault").GetBoolean());
        Assert.False(draft.GetProperty("isLikelyDuplicate").GetBoolean());
    }

    [Fact]
    public async Task MonthlyReportAnalyze_MarksLikelyDuplicatesUnselectedByDefault()
    {
        using var client = _factory.CreateClient();

        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var checkingId = await CreateAccountAsync(client, "Personal Checking", "Checking", 1500m);
        var groceriesCategoryId = await CreateCategoryAsync(client, "Groceries", "Expense");
        await client.PutAsJsonAsync("/api/settings/ai/openai", new { apiKey = "sk-test-openai-secret-123456" });

        var existingResponse = await client.PostAsJsonAsync("/api/transactions", new
        {
            accountId = checkingId,
            categoryId = groceriesCategoryId,
            amount = 42.17m,
            type = "Expense",
            occurredOnUtc = "2026-04-10T08:00:00Z",
            note = "Imported: Market"
        });
        var existingPayload = await existingResponse.Content.ReadFromJsonAsync<JsonElement>();
        var existingId = existingPayload.GetProperty("id").GetGuid();

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(checkingId.ToString()), "accountId");
        form.Add(new StringContent("2026-04"), "month");
        form.Add(new StringContent("OpenAi"), "provider");
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("Date,Description,Amount\n2026-04-10,Market,-42.17\n"));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(fileContent, "file", "statement.csv");

        var payload = await AnalyzeMonthlyReportAsync(client, form);
        var draft = payload.GetProperty("transactions")[0];
        Assert.True(draft.GetProperty("isLikelyDuplicate").GetBoolean());
        Assert.Equal(existingId, draft.GetProperty("duplicateTransactionId").GetGuid());
        Assert.Contains("same account, date, type, amount, and note", draft.GetProperty("duplicateReason").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(draft.GetProperty("isSelectedByDefault").GetBoolean());
    }

    [Fact]
    public async Task MonthlyReportAnalyze_DoesNotMarkDuplicateWhenOnlyOneNoteIsBlank()
    {
        using var client = _factory.CreateClient();

        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var checkingId = await CreateAccountAsync(client, "Personal Checking", "Checking", 1500m);
        var groceriesCategoryId = await CreateCategoryAsync(client, "Groceries", "Expense");
        await client.PutAsJsonAsync("/api/settings/ai/openai", new { apiKey = "sk-test-openai-secret-123456" });

        await client.PostAsJsonAsync("/api/transactions", new
        {
            accountId = checkingId,
            categoryId = groceriesCategoryId,
            amount = 42.17m,
            type = "Expense",
            occurredOnUtc = "2026-04-10T08:00:00Z",
            note = "Imported: Market"
        });

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(checkingId.ToString()), "accountId");
        form.Add(new StringContent("2026-04"), "month");
        form.Add(new StringContent("OpenAi"), "provider");
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("Date,Description,Amount\n2026-04-10,Market,-42.17\nnote:<blank>\n"));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(fileContent, "file", "statement.csv");

        var payload = await AnalyzeMonthlyReportAsync(client, form);
        var draft = payload.GetProperty("transactions")[0];
        Assert.False(draft.GetProperty("isLikelyDuplicate").GetBoolean());
        Assert.True(draft.GetProperty("isSelectedByDefault").GetBoolean());
    }

    [Fact]
    public async Task MonthlyReportCommit_RejectsUnacceptedDuplicateAndAcceptsExplicitDuplicate()
    {
        using var client = _factory.CreateClient();

        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var checkingId = await CreateAccountAsync(client, "Personal Checking", "Checking", 1500m);
        var groceriesCategoryId = await CreateCategoryAsync(client, "Groceries", "Expense");

        await client.PostAsJsonAsync("/api/transactions", new
        {
            accountId = checkingId,
            categoryId = groceriesCategoryId,
            amount = 42.17m,
            type = "Expense",
            occurredOnUtc = "2026-04-10T08:00:00Z",
            note = "Imported: Market"
        });

        var duplicateDraft = new
        {
            sourceId = "row-1",
            accountId = checkingId,
            categoryId = groceriesCategoryId,
            amount = 42.17m,
            type = "Expense",
            occurredOnUtc = "2026-04-10T12:00:00Z",
            note = "Imported: Market"
        };

        var rejectedResponse = await client.PostAsJsonAsync("/api/imports/monthly-report/commit", new
        {
            transactions = new[] { duplicateDraft },
            acceptedDuplicateSourceIds = Array.Empty<string>()
        });

        Assert.Equal(HttpStatusCode.BadRequest, rejectedResponse.StatusCode);

        var acceptedResponse = await client.PostAsJsonAsync("/api/imports/monthly-report/commit", new
        {
            transactions = new[] { duplicateDraft },
            acceptedDuplicateSourceIds = new[] { "row-1" }
        });

        Assert.Equal(HttpStatusCode.Created, acceptedResponse.StatusCode);
    }

    [Fact]
    public async Task MonthlyReportAnalyze_RequiresConfiguredProviderKey()
    {
        using var client = _factory.CreateClient();

        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var checkingId = await CreateAccountAsync(client, "Personal Checking", "Checking", 1500m);

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(checkingId.ToString()), "accountId");
        form.Add(new StringContent("2026-04"), "month");
        form.Add(new StringContent("Anthropic"), "provider");
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("Date,Description,Amount\n2026-04-10,Market,-42.17\n"));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(fileContent, "file", "statement.csv");

        var job = await AnalyzeMonthlyReportJobAsync(client, form);

        Assert.Equal("failed", job.GetProperty("status").GetString());
        Assert.Contains("not configured", job.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }


    [Fact]
    public async Task RecurringTransactions_GenerateMonthlyAnchoredMissedIntervalsAndPreventDuplicates()
    {
        using var client = _factory.CreateClient();

        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var accountId = await CreateAccountAsync(client, "Recurring Checking", "Checking", 1000m);
        var categoryId = await CreateCategoryAsync(client, "Rent", "Expense");

        var createResponse = await client.PostAsJsonAsync("/api/recurring-transactions", new
        {
            accountId,
            categoryId,
            amount = 250m,
            type = "Expense",
            interval = "Monthly",
            startOnUtc = "2024-01-31T09:00:00Z",
            note = "Anchored rent"
        });
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);

        await using (var scope = _factory.Services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope())
        {
            var generator = scope.ServiceProvider.GetRequiredService<Ledgerra.Application.Transactions.RecurringTransactionUseCases>();
            Assert.Equal(3, await generator.GenerateDueAsync(auth.UserId, new DateTime(2024, 3, 31, 12, 0, 0, DateTimeKind.Utc), CancellationToken.None));
            Assert.Equal(0, await generator.GenerateDueAsync(auth.UserId, new DateTime(2024, 3, 31, 12, 0, 0, DateTimeKind.Utc), CancellationToken.None));
        }

        var transactionsResponse = await client.GetAsync($"/api/transactions?accountId={accountId}&from=2024-01-01&to=2024-03-31");
        Assert.Equal(HttpStatusCode.OK, transactionsResponse.StatusCode);
        var transactions = (await transactionsResponse.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToArray();
        Assert.Equal(3, transactions.Length);
        Assert.Contains(transactions, transaction => transaction.GetProperty("occurredOnUtc").GetDateTime() == new DateTime(2024, 1, 31, 9, 0, 0, DateTimeKind.Utc));
        Assert.Contains(transactions, transaction => transaction.GetProperty("occurredOnUtc").GetDateTime() == new DateTime(2024, 2, 29, 9, 0, 0, DateTimeKind.Utc));
        Assert.Contains(transactions, transaction => transaction.GetProperty("occurredOnUtc").GetDateTime() == new DateTime(2024, 3, 31, 9, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task RecurringTransactions_CanUpdatePauseResumeAndDeleteTemplates()
    {
        using var client = _factory.CreateClient();

        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var accountId = await CreateAccountAsync(client, "Paycheck Account", "Checking", 1000m);
        var incomeCategoryId = await CreateCategoryAsync(client, "Payroll", "Income");

        var createResponse = await client.PostAsJsonAsync("/api/recurring-transactions", new
        {
            accountId,
            categoryId = incomeCategoryId,
            amount = 1200m,
            type = "Income",
            interval = "Weekly",
            startOnUtc = "2026-01-01T10:00:00Z",
            note = "Weekly pay"
        });
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var templateId = created.GetProperty("id").GetGuid();

        var pauseResponse = await client.PatchAsJsonAsync($"/api/recurring-transactions/{templateId}/status", new { isActive = false });
        Assert.Equal(HttpStatusCode.OK, pauseResponse.StatusCode);
        Assert.False((await pauseResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("isActive").GetBoolean());

        await using (var scope = _factory.Services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope())
        {
            var generator = scope.ServiceProvider.GetRequiredService<Ledgerra.Application.Transactions.RecurringTransactionUseCases>();
            Assert.Equal(0, await generator.GenerateDueAsync(auth.UserId, new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc), CancellationToken.None));
        }

        var updateResponse = await client.PutAsJsonAsync($"/api/recurring-transactions/{templateId}", new
        {
            accountId,
            categoryId = incomeCategoryId,
            amount = 1500m,
            type = "Income",
            interval = "Monthly",
            startOnUtc = "2026-01-15T10:00:00Z",
            isActive = true,
            note = "Monthly pay"
        });
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await updateResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Monthly", updated.GetProperty("interval").GetString());
        Assert.True(updated.GetProperty("isActive").GetBoolean());
        Assert.Equal(1500m, updated.GetProperty("amount").GetDecimal());

        var deleteResponse = await client.DeleteAsync($"/api/recurring-transactions/{templateId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var listResponse = await client.GetAsync("/api/recurring-transactions");
        Assert.Empty((await listResponse.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
    }

    [Fact]
    public async Task RecurringTransactions_ReturnBadRequestForInvalidReferencesAndNotFoundForMissingTemplates()
    {
        using var client = _factory.CreateClient();

        var auth = await RegisterAndAuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var accountId = await CreateAccountAsync(client, "Recurring Validation", "Checking", 1000m);
        var categoryId = await CreateCategoryAsync(client, "Subscriptions", "Expense");

        var createResponse = await client.PostAsJsonAsync("/api/recurring-transactions", new
        {
            accountId,
            categoryId,
            amount = 25m,
            type = "Expense",
            interval = "Monthly",
            startOnUtc = "2026-01-01T10:00:00Z",
            note = "Streaming"
        });
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var templateId = created.GetProperty("id").GetGuid();

        var invalidAccountResponse = await client.PutAsJsonAsync($"/api/recurring-transactions/{templateId}", new
        {
            accountId = Guid.NewGuid(),
            categoryId,
            amount = 25m,
            type = "Expense",
            interval = "Monthly",
            startOnUtc = "2026-01-01T10:00:00Z",
            isActive = true,
            note = "Streaming"
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalidAccountResponse.StatusCode);

        var missingTemplateId = Guid.NewGuid();
        var missingTemplateResponse = await client.PutAsJsonAsync($"/api/recurring-transactions/{missingTemplateId}", new
        {
            accountId,
            categoryId,
            amount = 25m,
            type = "Expense",
            interval = "Monthly",
            startOnUtc = "2026-01-01T10:00:00Z",
            isActive = true,
            note = "Streaming"
        });
        Assert.Equal(HttpStatusCode.NotFound, missingTemplateResponse.StatusCode);

        var missingStatusResponse = await client.PatchAsJsonAsync($"/api/recurring-transactions/{missingTemplateId}/status", new { isActive = false });
        Assert.Equal(HttpStatusCode.NotFound, missingStatusResponse.StatusCode);

        var missingDeleteResponse = await client.DeleteAsync($"/api/recurring-transactions/{missingTemplateId}");
        Assert.Equal(HttpStatusCode.NotFound, missingDeleteResponse.StatusCode);
    }

    private static async Task<JsonElement> AnalyzeMonthlyReportAsync(HttpClient client, MultipartFormDataContent form)
    {
        var job = await AnalyzeMonthlyReportJobAsync(client, form);
        Assert.Equal("completed", job.GetProperty("status").GetString());
        return job.GetProperty("analysis").Clone();
    }

    private static async Task<JsonElement> AnalyzeMonthlyReportJobAsync(HttpClient client, MultipartFormDataContent form)
    {
        const int maxPollAttempts = 100;
        const int pollDelayMs = 100;
        var response = await client.PostAsync("/api/imports/monthly-report/analyze", form);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var jobId = payload.GetProperty("jobId").GetGuid();

        for (var attempt = 0; attempt < maxPollAttempts; attempt++)
        {
            var jobResponse = await client.GetAsync($"/api/imports/monthly-report/analyze/{jobId}");
            Assert.Equal(HttpStatusCode.OK, jobResponse.StatusCode);
            var job = await jobResponse.Content.ReadFromJsonAsync<JsonElement>();
            var status = job.GetProperty("status").GetString();
            if (status is "completed" or "failed")
            {
                return job.Clone();
            }

            await Task.Delay(pollDelayMs);
        }

        throw new Xunit.Sdk.XunitException($"Monthly report analysis job did not finish after {maxPollAttempts * pollDelayMs}ms.");
    }

    private static async Task<AuthResult> RegisterAndAuthenticateAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            login = $"user-{Guid.NewGuid():N}".Substring(0, 20),
            email = $"user-{Guid.NewGuid():N}@ledgerra.local",
            password = "P@ssw0rd123!"
        });

        if (response.StatusCode != HttpStatusCode.Created)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException($"Registration failed with {(int)response.StatusCode}: {body}");
        }

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        return new AuthResult(
            payload.GetProperty("userId").GetGuid(),
            payload.GetProperty("accessToken").GetString()!,
            payload.GetProperty("refreshToken").GetString()!);
    }

    private static async Task<Guid> CreateAccountAsync(HttpClient client, string name, string type, decimal openingBalance)
    {
        var response = await client.PostAsJsonAsync("/api/accounts", new
        {
            name,
            type,
            currencyCode = "USD",
            openingBalance
        });

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return payload.GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateCategoryAsync(HttpClient client, string name, string kind)
    {
        var response = await client.PostAsJsonAsync("/api/categories", new
        {
            name,
            kind
        });

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return payload.GetProperty("id").GetGuid();
    }

    private sealed record AuthResult(Guid UserId, string AccessToken, string RefreshToken);
}
