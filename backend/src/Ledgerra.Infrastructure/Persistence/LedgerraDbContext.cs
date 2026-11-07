using Ledgerra.Domain.Accounts;
using Ledgerra.Domain.Bookkeeping;
using Ledgerra.Domain.Ai;
using Ledgerra.Domain.Auth;
using Ledgerra.Domain.Budgets;
using Ledgerra.Domain.Categories;
using Ledgerra.Domain.Imports;
using Ledgerra.Domain.Goals;
using Ledgerra.Domain.ExchangeRates;
using Ledgerra.Domain.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Ledgerra.Infrastructure.Persistence;

public sealed class LedgerraDbContext : DbContext
{
    public LedgerraDbContext(DbContextOptions<LedgerraDbContext> options)
        : base(options)
    {
    }

    public DbSet<AppUser> Users => Set<AppUser>();

    public DbSet<Account> Accounts => Set<Account>();

    public DbSet<MonthlyAccountBalanceSnapshot> MonthlyAccountBalanceSnapshots => Set<MonthlyAccountBalanceSnapshot>();

    public DbSet<Category> Categories => Set<Category>();

    public DbSet<Transaction> Transactions => Set<Transaction>();

    public DbSet<RecurringTransactionTemplate> RecurringTransactionTemplates => Set<RecurringTransactionTemplate>();

    public DbSet<BudgetPeriod> BudgetPeriods => Set<BudgetPeriod>();

    public DbSet<BudgetCategoryLimit> BudgetCategoryLimits => Set<BudgetCategoryLimit>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<PersonalAccessToken> PersonalAccessTokens => Set<PersonalAccessToken>();

    public DbSet<AiProviderCredential> AiProviderCredentials => Set<AiProviderCredential>();

    public DbSet<UserAiPreference> UserAiPreferences => Set<UserAiPreference>();

    public DbSet<CategorizationRule> CategorizationRules => Set<CategorizationRule>();

    public DbSet<SavingsGoal> SavingsGoals => Set<SavingsGoal>();

    public DbSet<UserExchangeRate> ExchangeRates => Set<UserExchangeRate>();

    public DbSet<TransferMatch> TransferMatches => Set<TransferMatch>();
    public DbSet<SavedLedgerView> SavedLedgerViews => Set<SavedLedgerView>();
    public DbSet<ImportProfile> ImportProfiles => Set<ImportProfile>();
    public DbSet<Payee> Payees => Set<Payee>();
    public DbSet<EntryMetadata> EntryMetadata => Set<EntryMetadata>();
    public DbSet<Reconciliation> Reconciliations => Set<Reconciliation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SavedLedgerView>(b => { b.HasKey(x => x.Id); b.Property(x => x.Name).HasMaxLength(120); b.Property(x => x.Query).HasMaxLength(4000); b.HasIndex(x => new { x.UserId, x.Name }).IsUnique(); });
        modelBuilder.Entity<EntryMetadata>().Property(x => x.PaymentMethod).HasMaxLength(40);
        modelBuilder.Entity<TransferMatch>(b => b.HasKey(x => x.Id));
        modelBuilder.Entity<ImportProfile>(b => { b.HasKey(x => x.Id); b.Property(x => x.Name).HasMaxLength(120); b.Property(x => x.SettingsJson).HasMaxLength(8000); b.HasIndex(x => new { x.UserId, x.Name }).IsUnique(); });
        modelBuilder.Entity<Payee>(b => { b.HasKey(x => x.Id); b.Property(x => x.Name).HasMaxLength(120); b.HasIndex(x => new { x.UserId, x.Name }).IsUnique(); });
        modelBuilder.Entity<EntryMetadata>(b => { b.HasKey(x => x.Id); b.Property(x => x.ExternalKey).HasMaxLength(64); b.HasIndex(x => new { x.UserId, x.ExternalKey }).IsUnique().HasFilter("\"ExternalKey\" IS NOT NULL"); b.Property(x => x.Reference).HasMaxLength(120); b.Property(x => x.Tags).HasMaxLength(400); b.Property(x => x.Status).HasMaxLength(20); b.HasOne<Transaction>().WithMany().HasForeignKey(x => x.Id).OnDelete(DeleteBehavior.Cascade); });
        modelBuilder.Entity<Reconciliation>(b => { b.HasKey(x => x.Id); b.Property(x => x.StatementBalance).HasPrecision(18, 2); b.HasIndex(x => new { x.UserId, x.AccountId, x.StatementDate }).IsUnique(); });
        modelBuilder.Entity<AppUser>(builder =>
        {
            builder.HasKey(user => user.Id);
            builder.HasIndex(user => user.Login).IsUnique();
            builder.HasIndex(user => user.Email).IsUnique().HasFilter("\"Email\" <> ''");
            builder.Property(user => user.Login).HasMaxLength(32);
            builder.Property(user => user.Email).HasMaxLength(320);
            builder.Property(user => user.PasswordHash).HasMaxLength(2048);
            builder.Property(user => user.PreferredCurrencyCode).HasMaxLength(3);
            builder.Property(user => user.PreferredLanguageCode).HasMaxLength(10);
        });


        modelBuilder.Entity<UserExchangeRate>(builder =>
        {
            builder.HasKey(rate => rate.Id);
            builder.Property(rate => rate.FromCurrencyCode).HasMaxLength(3);
            builder.Property(rate => rate.ToCurrencyCode).HasMaxLength(3);
            builder.Property(rate => rate.Rate).HasPrecision(18, 8);
            builder.HasIndex(rate => new { rate.UserId, rate.FromCurrencyCode, rate.ToCurrencyCode, rate.Month }).IsUnique();
            builder.HasOne<AppUser>()
                .WithMany(user => user.ExchangeRates)
                .HasForeignKey(rate => rate.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Account>(builder =>
        {
            builder.HasKey(account => account.Id);
            builder.Property(account => account.Name).HasMaxLength(120);
            builder.Property(account => account.CurrencyCode).HasMaxLength(3);
            builder.Property(account => account.InstitutionName).HasMaxLength(120);
            builder.Property(account => account.AccountNumberMasked).HasMaxLength(64);
            builder.Property(account => account.IconKind).HasConversion<int>();
            builder.Property(account => account.ExcludeFromBudget).HasDefaultValue(false);
            builder.Property(account => account.ExcludeFromNetWorth).HasDefaultValue(false);
            builder.HasOne<AppUser>()
                .WithMany(user => user.Accounts)
                .HasForeignKey(account => account.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MonthlyAccountBalanceSnapshot>(builder =>
        {
            builder.HasKey(snapshot => snapshot.Id);
            builder.Property(snapshot => snapshot.Balance).HasPrecision(18, 2);
            builder.Property(snapshot => snapshot.CurrencyCode).HasMaxLength(3);
            builder.HasIndex(snapshot => new { snapshot.UserId, snapshot.AccountId, snapshot.MonthEndDate }).IsUnique();
            builder.HasOne(snapshot => snapshot.Account)
                .WithMany()
                .HasForeignKey(snapshot => snapshot.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Category>(builder =>
        {
            builder.HasKey(category => category.Id);
            builder.Property(category => category.Name).HasMaxLength(120);
            builder.Property(category => category.Color).HasMaxLength(32);
            builder.HasOne<AppUser>()
                .WithMany(user => user.Categories)
                .HasForeignKey(category => category.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Transaction>(builder =>
        {
            builder.HasKey(transaction => transaction.Id);
            builder.Property(transaction => transaction.Amount).HasPrecision(18, 2);
            builder.Property(transaction => transaction.Note).HasMaxLength(400);
            builder.HasIndex(transaction => new { transaction.UserId, transaction.OccurredOnUtc });
            builder.HasIndex(transaction => new { transaction.UserId, transaction.SplitGroupId });
            builder.HasOne<AppUser>()
                .WithMany(user => user.Transactions)
                .HasForeignKey(transaction => transaction.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne(transaction => transaction.Account)
                .WithMany(account => account.Transactions)
                .HasForeignKey(transaction => transaction.AccountId)
                .OnDelete(DeleteBehavior.Restrict);
            builder.HasOne<SavingsGoal>()
                .WithMany()
                .HasForeignKey(transaction => transaction.SavingsGoalId)
                .OnDelete(DeleteBehavior.SetNull);
            builder.HasOne(transaction => transaction.Category)
                .WithMany(category => category.Transactions)
                .HasForeignKey(transaction => transaction.CategoryId)
                .OnDelete(DeleteBehavior.SetNull);
            builder.HasOne<Transaction>()
                .WithMany()
                .HasForeignKey(transaction => transaction.ParentTransactionId)
                .OnDelete(DeleteBehavior.Cascade);
        });


        modelBuilder.Entity<RecurringTransactionTemplate>(builder =>
        {
            builder.HasKey(template => template.Id);
            builder.HasOne<Account>().WithMany().HasForeignKey(template => template.DestinationAccountId).OnDelete(DeleteBehavior.Restrict);
            builder.Property(template => template.Amount).HasPrecision(18, 2);
            builder.Property(template => template.Note).HasMaxLength(400);
            builder.Property(template => template.Type).HasConversion<string>().HasMaxLength(32);
            builder.Property(template => template.Interval).HasConversion<string>().HasMaxLength(32);
            builder.HasIndex(template => new { template.UserId, template.AccountId, template.CategoryId, template.IsActive });
            builder.HasOne<AppUser>()
                .WithMany()
                .HasForeignKey(template => template.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<Account>()
                .WithMany()
                .HasForeignKey(template => template.AccountId)
                .OnDelete(DeleteBehavior.Restrict);
            builder.HasOne<Category>()
                .WithMany()
                .HasForeignKey(template => template.CategoryId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<BudgetPeriod>(builder =>
        {
            builder.HasKey(period => period.Id);
            builder.HasIndex(period => new { period.UserId, period.Year, period.Month }).IsUnique();
            builder.HasOne<AppUser>()
                .WithMany(user => user.BudgetPeriods)
                .HasForeignKey(period => period.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BudgetCategoryLimit>(builder =>
        {
            builder.HasKey(limit => limit.Id);
            builder.Property(limit => limit.PlannedAmount).HasPrecision(18, 2);
            builder.Property(limit => limit.CarryOverUnspent).HasDefaultValue(false);
            builder.HasIndex(limit => new { limit.BudgetPeriodId, limit.CategoryId }).IsUnique();
            builder.HasOne(limit => limit.BudgetPeriod)
                .WithMany(period => period.CategoryLimits)
                .HasForeignKey(limit => limit.BudgetPeriodId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne(limit => limit.Category)
                .WithMany()
                .HasForeignKey(limit => limit.CategoryId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RefreshToken>(builder =>
        {
            builder.HasKey(token => token.Id);
            builder.Property(token => token.TokenHash).HasMaxLength(128);
            builder.HasIndex(token => token.TokenHash).IsUnique();
            builder.HasOne(token => token.User)
                .WithMany(user => user.RefreshTokens)
                .HasForeignKey(token => token.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });


        modelBuilder.Entity<PersonalAccessToken>(builder =>
        {
            builder.HasKey(token => token.Id);
            builder.Property(token => token.Name).HasMaxLength(120);
            builder.Property(token => token.TokenHash).HasMaxLength(128);
            builder.Property(token => token.TokenPrefix).HasMaxLength(16);
            builder.HasIndex(token => token.TokenHash).IsUnique();
            builder.HasIndex(token => new { token.UserId, token.Name });
            builder.HasOne(token => token.User)
                .WithMany(user => user.PersonalAccessTokens)
                .HasForeignKey(token => token.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AiProviderCredential>(builder =>
        {
            builder.HasKey(credential => credential.Id);
            builder.HasIndex(credential => new { credential.UserId, credential.Provider }).IsUnique();
            builder.Property(credential => credential.Provider).HasConversion<string>().HasMaxLength(32);
            builder.Property(credential => credential.EncryptedApiKey).HasMaxLength(4096);
            builder.Property(credential => credential.MaskedKey).HasMaxLength(32);
            builder.Property(credential => credential.BaseUrl).HasMaxLength(2048);
            builder.Property(credential => credential.Model).HasMaxLength(200);
            builder.HasOne<AppUser>()
                .WithMany(user => user.AiProviderCredentials)
                .HasForeignKey(credential => credential.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserAiPreference>(builder =>
        {
            builder.HasKey(preference => preference.UserId);
            builder.Property(preference => preference.DefaultProvider).HasConversion<string>().HasMaxLength(32);
            builder.HasOne<AppUser>()
                .WithOne(user => user.AiPreference)
                .HasForeignKey<UserAiPreference>(preference => preference.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });


        modelBuilder.Entity<SavingsGoal>(builder =>
        {
            builder.HasKey(goal => goal.Id);
            builder.Property(goal => goal.Name).HasMaxLength(120);
            builder.Property(goal => goal.TargetAmount).HasPrecision(18, 2);
            builder.HasIndex(goal => new { goal.UserId, goal.Name }).IsUnique();
            builder.HasOne<AppUser>()
                .WithMany(user => user.SavingsGoals)
                .HasForeignKey(goal => goal.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CategorizationRule>(builder =>
        {
            builder.HasKey(rule => rule.Id);
            builder.HasIndex(rule => new { rule.UserId, rule.Priority });
            builder.HasIndex(rule => new { rule.UserId, rule.Name }).IsUnique();
            builder.Property(rule => rule.Name).HasMaxLength(120);
            builder.Property(rule => rule.MatchField).HasConversion<string>().HasMaxLength(32);
            builder.Property(rule => rule.MatchOperator).HasConversion<string>().HasMaxLength(32);
            builder.Property(rule => rule.ConditionsJson).HasMaxLength(4000);
            builder.Property(rule => rule.MatchValue).HasMaxLength(200);
            builder.Property(rule => rule.AssignTransactionType).HasConversion<string>().HasMaxLength(32);
            builder.HasOne(rule => rule.User)
                .WithMany(user => user.CategorizationRules)
                .HasForeignKey(rule => rule.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne(rule => rule.AssignCategory)
                .WithMany()
                .HasForeignKey(rule => rule.AssignCategoryId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
