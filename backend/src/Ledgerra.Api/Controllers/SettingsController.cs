using System.Globalization;
using Ledgerra.Api.Contracts;
using Ledgerra.Api.Extensions;
using Ledgerra.Application.Settings;
using Ledgerra.Application.ExchangeRates;
using Ledgerra.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ledgerra.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/settings")]
public sealed class SettingsController : ControllerBase
{
    private readonly LedgerraDbContext _dbContext;
    private readonly GetProfileQueryHandler _getProfileQueryHandler;
    private readonly UpdateProfileCommandHandler _updateProfileCommandHandler;
    private readonly GetExchangeRatesQueryHandler _getExchangeRatesQueryHandler;
    private readonly UpsertExchangeRateCommandHandler _upsertExchangeRateCommandHandler;
    private readonly DeleteExchangeRateCommandHandler _deleteExchangeRateCommandHandler;

    public SettingsController(
        LedgerraDbContext dbContext,
        GetProfileQueryHandler getProfileQueryHandler,
        UpdateProfileCommandHandler updateProfileCommandHandler,
        GetExchangeRatesQueryHandler getExchangeRatesQueryHandler,
        UpsertExchangeRateCommandHandler upsertExchangeRateCommandHandler,
        DeleteExchangeRateCommandHandler deleteExchangeRateCommandHandler)
    {
        _dbContext = dbContext;
        _getProfileQueryHandler = getProfileQueryHandler;
        _updateProfileCommandHandler = updateProfileCommandHandler;
        _getExchangeRatesQueryHandler = getExchangeRatesQueryHandler;
        _upsertExchangeRateCommandHandler = upsertExchangeRateCommandHandler;
        _deleteExchangeRateCommandHandler = deleteExchangeRateCommandHandler;
    }

    [HttpGet("profile")]
    public async Task<ActionResult<ProfileResponse>> GetProfile(CancellationToken cancellationToken)
    {
        var userId = User.GetRequiredUserId();
        var profile = await _getProfileQueryHandler.HandleAsync(new GetProfileQuery(userId), cancellationToken);

        return profile is null
            ? NotFound()
            : Ok(new ProfileResponse(profile.Email, profile.PreferredCurrencyCode, profile.PreferredLanguageCode));
    }

    [HttpPut("profile")]
    public async Task<ActionResult<ProfileResponse>> UpdateProfile(UpdateProfileRequest request, CancellationToken cancellationToken)
    {
        var userId = User.GetRequiredUserId();
        var profile = await _updateProfileCommandHandler.HandleAsync(
            new UpdateProfileCommand(userId, request.PreferredCurrencyCode, request.PreferredLanguageCode),
            cancellationToken);

        if (profile is null)
        {
            return NotFound();
        }

        return Ok(new ProfileResponse(profile.Email, profile.PreferredCurrencyCode, profile.PreferredLanguageCode));
    }


    [HttpGet("exchange-rates")]
    public async Task<ActionResult<IReadOnlyList<ExchangeRateResponse>>> GetExchangeRates(CancellationToken cancellationToken)
    {
        var rates = await _getExchangeRatesQueryHandler.HandleAsync(User.GetRequiredUserId(), cancellationToken);

        return Ok(rates.Select(rate => new ExchangeRateResponse(
            rate.Id,
            rate.FromCurrencyCode,
            rate.ToCurrencyCode,
            rate.Month,
            rate.Rate,
            rate.UpdatedAtUtc)).ToList());
    }

    [HttpPut("exchange-rates")]
    public async Task<ActionResult<ExchangeRateResponse>> UpsertExchangeRate(UpsertExchangeRateRequest request, CancellationToken cancellationToken)
    {
        if (!DateOnly.TryParseExact($"{request.Month}-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedMonth))
        {
            return this.ValidationError(new Dictionary<string, string[]>
            {
                ["month"] = ["Month must be provided as YYYY-MM."]
            });
        }

        var rate = await _upsertExchangeRateCommandHandler.HandleAsync(
            new UpsertExchangeRateCommand(
                User.GetRequiredUserId(),
                request.FromCurrencyCode,
                request.ToCurrencyCode,
                parsedMonth,
                request.Rate),
            cancellationToken);

        return Ok(new ExchangeRateResponse(rate.Id, rate.FromCurrencyCode, rate.ToCurrencyCode, rate.Month, rate.Rate, rate.UpdatedAtUtc));
    }

    [HttpDelete("exchange-rates/{rateId:guid}")]
    public async Task<ActionResult> DeleteExchangeRate(Guid rateId, CancellationToken cancellationToken)
    {
        var deleted = await _deleteExchangeRateCommandHandler.HandleAsync(User.GetRequiredUserId(), rateId, cancellationToken);
        return deleted ? NoContent() : NotFound();
    }

    [HttpDelete("account-data")]
    public async Task<ActionResult> ClearAccountData(CancellationToken cancellationToken)
    {
        var userId = User.GetRequiredUserId();
        if (!await _dbContext.Users.AnyAsync(user => user.Id == userId, cancellationToken))
        {
            return NotFound();
        }

        ClearAccountData(userId);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return NoContent();
    }

    [HttpDelete("account")]
    public async Task<ActionResult> DeleteAccount(CancellationToken cancellationToken)
    {
        var userId = User.GetRequiredUserId();
        var user = await _dbContext.Users.SingleOrDefaultAsync(item => item.Id == userId, cancellationToken);
        if (user is null)
        {
            return NotFound();
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        ClearAccountData(userId);

        _dbContext.PersonalAccessTokens.RemoveRange(_dbContext.PersonalAccessTokens.Where(token => token.UserId == userId));
        _dbContext.RefreshTokens.RemoveRange(_dbContext.RefreshTokens.Where(token => token.UserId == userId));
        _dbContext.Users.Remove(user);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return NoContent();
    }

    private void ClearAccountData(Guid userId)
    {
        var budgetPeriodIds = _dbContext.BudgetPeriods.Where(period => period.UserId == userId).Select(period => period.Id);

        _dbContext.BudgetCategoryLimits.RemoveRange(_dbContext.BudgetCategoryLimits.Where(limit => budgetPeriodIds.Contains(limit.BudgetPeriodId)));
        _dbContext.BudgetPeriods.RemoveRange(_dbContext.BudgetPeriods.Where(period => period.UserId == userId));
        _dbContext.RecurringTransactionTemplates.RemoveRange(_dbContext.RecurringTransactionTemplates.Where(template => template.UserId == userId));
        _dbContext.MonthlyAccountBalanceSnapshots.RemoveRange(_dbContext.MonthlyAccountBalanceSnapshots.Where(snapshot => snapshot.UserId == userId));
        _dbContext.ExchangeRates.RemoveRange(_dbContext.ExchangeRates.Where(rate => rate.UserId == userId));
        _dbContext.TransferMatches.RemoveRange(_dbContext.TransferMatches.Where(x => x.UserId == userId));
        _dbContext.EntryMetadata.RemoveRange(_dbContext.EntryMetadata.Where(x => x.UserId == userId));
        _dbContext.Reconciliations.RemoveRange(_dbContext.Reconciliations.Where(x => x.UserId == userId));
        _dbContext.Payees.RemoveRange(_dbContext.Payees.Where(x => x.UserId == userId));
        _dbContext.ImportProfiles.RemoveRange(_dbContext.ImportProfiles.Where(x => x.UserId == userId));
        _dbContext.Transactions.RemoveRange(_dbContext.Transactions.Where(transaction => transaction.UserId == userId));
        _dbContext.CategorizationRules.RemoveRange(_dbContext.CategorizationRules.Where(rule => rule.UserId == userId));
        _dbContext.SavingsGoals.RemoveRange(_dbContext.SavingsGoals.Where(goal => goal.UserId == userId));
        _dbContext.Categories.RemoveRange(_dbContext.Categories.Where(category => category.UserId == userId));
        _dbContext.Accounts.RemoveRange(_dbContext.Accounts.Where(account => account.UserId == userId));
        _dbContext.AiProviderCredentials.RemoveRange(_dbContext.AiProviderCredentials.Where(credential => credential.UserId == userId));
        _dbContext.UserAiPreferences.RemoveRange(_dbContext.UserAiPreferences.Where(preference => preference.UserId == userId));
    }
}
