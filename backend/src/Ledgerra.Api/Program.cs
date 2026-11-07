using System.Text;
using Ledgerra.Application.Budgets;
using Ledgerra.Application.Accounts;
using Ledgerra.Application.Categories;
using Ledgerra.Application.Dashboard;
using Ledgerra.Application.Imports;
using Ledgerra.Application.Reporting;
using Ledgerra.Application.Settings;
using Ledgerra.Application.ExchangeRates;
using Ledgerra.Application.Transactions;
using Ledgerra.Api.Services;
using Ledgerra.Api.Services.Ai;
using Ledgerra.Api.Services.Imports;
using Ledgerra.Infrastructure.Authentication;
using Ledgerra.Infrastructure.Persistence;
using Ledgerra.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
var dataProtectionBuilder = builder.Services.AddDataProtection();
var dataProtectionKeysPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(dataProtectionKeysPath))
{
    dataProtectionBuilder.PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));
}
builder.Services.AddScoped<ISecretProtector, DataProtectionSecretProtector>();
builder.Services.AddScoped<CsvReportContentExtractor>();
builder.Services.AddScoped<PdfReportContentExtractor>();
builder.Services.AddScoped<IReportContentExtractor, ReportContentExtractor>();
builder.Services.AddScoped<CsvBankImportMapper>();
builder.Services.AddHttpClient<OpenAiReportAnalysisClient>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient<AnthropicReportAnalysisClient>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient<OpenAiCompatibleReportAnalysisClient>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddScoped<IAiReportAnalysisClient>(provider => provider.GetRequiredService<OpenAiReportAnalysisClient>());
builder.Services.AddScoped<IAiReportAnalysisClient>(provider => provider.GetRequiredService<AnthropicReportAnalysisClient>());
builder.Services.AddScoped<IAiReportAnalysisClient>(provider => provider.GetRequiredService<OpenAiCompatibleReportAnalysisClient>());
builder.Services.AddScoped<AiReportAnalysisClientFactory>();
builder.Services.AddScoped<AiReportAnalysisService>();
builder.Services.AddScoped<IImportCategorizationRuleMatcher, ImportCategorizationRuleMatcher>();
builder.Services.AddScoped<IImportDuplicateDetector, ImportDuplicateDetector>();
builder.Services.AddSingleton<MonthlyReportAnalysisJobStore>();
builder.Services.AddScoped<AnalyzeMonthlyReportCommandHandler>();
builder.Services.AddScoped<GetDashboardSummaryQueryHandler>();
builder.Services.AddScoped<GetReportingOverviewQueryHandler>();
builder.Services.AddScoped<IAccountStore, AccountStore>();
builder.Services.AddScoped<ICategoryStore, CategoryStore>();
builder.Services.AddScoped<CreateAccountCommandHandler>();
builder.Services.AddScoped<CreateCategoryCommandHandler>();
builder.Services.AddScoped<DeleteAccountCommandHandler>();
builder.Services.AddScoped<DeleteCategoryCommandHandler>();
builder.Services.AddScoped<GetBudgetSummaryQueryHandler>();
builder.Services.AddScoped<GetAccountByIdQueryHandler>();
builder.Services.AddScoped<GetCategoryByIdQueryHandler>();
builder.Services.AddScoped<GetAccountsQueryHandler>();
builder.Services.AddScoped<GetCategoriesQueryHandler>();
builder.Services.AddScoped<CommitMonthlyReportDraftsCommandHandler>();
builder.Services.AddScoped<IMonthlyReportAnalyzer, MonthlyReportAnalysisAdapter>();
builder.Services.AddScoped<IMonthlyReportDuplicateMarker, MonthlyReportDuplicateMarkerAdapter>();
builder.Services.AddScoped<CreateTransactionCommandHandler>();
builder.Services.AddScoped<DeleteTransactionCommandHandler>();
builder.Services.AddScoped<MoveTransactionAccountCommandHandler>();
builder.Services.AddScoped<GetProfileQueryHandler>();
builder.Services.AddScoped<IMonthlyReportDuplicateReviewer, MonthlyReportDuplicateReviewerAdapter>();
builder.Services.AddScoped<IMonthlyReportImportCommitStore, MonthlyReportImportCommitStore>();
builder.Services.AddScoped<IMonthlyReportRuleMatcher, MonthlyReportRuleMatcherAdapter>();
builder.Services.AddScoped<ITransactionCommandStore, TransactionCommandStore>();
builder.Services.AddScoped<ITransactionQueryStore, TransactionQueryStore>();
builder.Services.AddScoped<UpdateBudgetCommandHandler>();
builder.Services.AddScoped<UpdateAccountCommandHandler>();
builder.Services.AddScoped<UpdateCategoryCommandHandler>();
builder.Services.AddScoped<UpdateProfileCommandHandler>();
builder.Services.AddScoped<UpdateTransactionCommandHandler>();
builder.Services.AddScoped<GetTransactionByIdQueryHandler>();
builder.Services.AddScoped<GetTransactionsQueryHandler>();
builder.Services.AddScoped<IRecurringTransactionRepository, RecurringTransactionRepository>();
builder.Services.AddScoped<RecurringTransactionUseCases>();
builder.Services.AddSingleton<IRecurringTransactionGenerationCoordinator, PostgresRecurringTransactionGenerationCoordinator>();
builder.Services.AddHostedService<RecurringTransactionGenerationService>();
builder.Services.AddScoped<IBudgetSummaryStore, BudgetSummaryStore>();
builder.Services.AddScoped<IDashboardSummaryDataProvider, DashboardSummaryDataProvider>();
builder.Services.AddScoped<IReportingDataProvider, ReportingDataProvider>();
builder.Services.AddScoped<IMonthlyAccountBalanceSnapshotService, MonthlyAccountBalanceSnapshotService>();
builder.Services.AddScoped<IUserProfileStore, UserProfileStore>();
builder.Services.AddScoped<IExchangeRateStore, ExchangeRateStore>();
builder.Services.AddScoped<GetExchangeRatesQueryHandler>();
builder.Services.AddScoped<UpsertExchangeRateCommandHandler>();
builder.Services.AddScoped<DeleteExchangeRateCommandHandler>();

builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));
var authOptions = builder.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();

builder.Services.AddDbContext<LedgerraDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("Ledgerra");

    if (builder.Environment.IsEnvironment("Testing"))
    {
        options.UseInMemoryDatabase("ledgerra-testing");
        return;
    }

    options.UseNpgsql(connectionString);
});

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ValidIssuer = authOptions.Issuer,
            ValidAudience = authOptions.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(authOptions.SigningKey)),
            ClockSkew = TimeSpan.FromSeconds(30)
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var patId = context.Principal?.FindFirst("pat_id")?.Value;
                if (string.IsNullOrWhiteSpace(patId) || !Guid.TryParse(patId, out var parsedPatId))
                {
                    return;
                }

                var dbContext = context.HttpContext.RequestServices.GetRequiredService<LedgerraDbContext>();
                var token = await dbContext.PersonalAccessTokens.SingleOrDefaultAsync(item => item.Id == parsedPatId);
                if (token is null || token.RevokedAtUtc is not null)
                {
                    context.Fail("Token revoked.");
                    return;
                }

                var now = DateTime.UtcNow;
                if (token.LastUsedAtUtc == null || (now - token.LastUsedAtUtc.Value) > TimeSpan.FromHours(1))
                {
                    token.LastUsedAtUtc = now;
                    try
                    {
                        await dbContext.SaveChangesAsync();
                    }
                    catch (Exception exception)
                    {
                        var logger = context.HttpContext.RequestServices
                            .GetRequiredService<ILoggerFactory>()
                            .CreateLogger("Ledgerra.Authentication.PersonalAccessToken");
                        logger.LogWarning(exception, "Unable to update personal access token last-used timestamp.");
                    }
                }
            }
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddScoped<IJwtTokenService, JwtTokenService>();
builder.Services.AddScoped<IPasswordService, PasswordService>();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        var origins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [];
        if (origins.Length == 0)
        {
            policy.AllowAnyHeader().AllowAnyMethod().AllowAnyOrigin();
            return;
        }

        policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod();
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler();
}
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<LedgerraDbContext>();
    await dbContext.Database.EnsureCreatedAsync();
    await AuthSchemaInitializer.InitializeAsync(dbContext);
    await CategorizationRuleSchemaInitializer.InitializeAsync(dbContext);
    await AccountSchemaInitializer.InitializeAsync(dbContext);
    await BudgetSchemaInitializer.InitializeAsync(dbContext);
    await BookkeepingSchemaInitializer.InitializeAsync(dbContext);
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapControllers();

app.Run();

public partial class Program;
