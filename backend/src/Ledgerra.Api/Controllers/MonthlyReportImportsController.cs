using System.Text;
using Ledgerra.Api.Contracts;
using Ledgerra.Api.Extensions;
using Ledgerra.Application.Imports;
using Ledgerra.Application.Transactions;
using Ledgerra.Api.Services.Imports;
using Ledgerra.Domain.Ai;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ledgerra.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/imports/monthly-report")]
public sealed class MonthlyReportImportsController : ControllerBase
{
    private readonly MonthlyReportAnalysisJobStore _analysisJobStore;
    private readonly CommitMonthlyReportDraftsCommandHandler _commitMonthlyReportDraftsCommandHandler;
    private readonly IReportContentExtractor _reportContentExtractor;
    private readonly CsvBankImportMapper _csvBankImportMapper;

    public MonthlyReportImportsController(
        MonthlyReportAnalysisJobStore analysisJobStore,
        CommitMonthlyReportDraftsCommandHandler commitMonthlyReportDraftsCommandHandler,
        IReportContentExtractor reportContentExtractor,
        CsvBankImportMapper csvBankImportMapper)
    {
        _analysisJobStore = analysisJobStore;
        _commitMonthlyReportDraftsCommandHandler = commitMonthlyReportDraftsCommandHandler;
        _reportContentExtractor = reportContentExtractor;
        _csvBankImportMapper = csvBankImportMapper;
    }

    [HttpPost("analyze")]
    public async Task<ActionResult<MonthlyReportAnalysisJobResponse>> Analyze(
        [FromForm] Guid accountId,
        [FromForm] string month,
        [FromForm] string provider,
        [FromForm] IFormFile file,
        CancellationToken cancellationToken)
    {
        if (!AiProviderParsingExtensions.TryParseAiProvider(provider, out var parsedProvider))
        {
            return this.ValidationError(new Dictionary<string, string[]>
            {
                ["provider"] = ["Supported providers are OpenAi, Anthropic, and OpenAiCompatible."]
            });
        }

        try
        {
            var report = await _reportContentExtractor.ExtractAsync(file, cancellationToken);
            var job = _analysisJobStore.Start(User.GetRequiredUserId(), accountId, month, parsedProvider, report.Content);

            return AcceptedAtAction(nameof(GetAnalysisJob), new { jobId = job.JobId }, job.ToResponse());
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new ProblemDetails { Title = exception.Message });
        }
    }

    [HttpGet("analyze/{jobId:guid}")]
    public ActionResult<MonthlyReportAnalysisJobResponse> GetAnalysisJob(Guid jobId)
    {
        var job = _analysisJobStore.Get(User.GetRequiredUserId(), jobId);
        return job is null ? NotFound(new ProblemDetails { Title = "Analysis job not found." }) : Ok(job.ToResponse());
    }

    [HttpPost("analyze/{jobId:guid}/retry-parse")]
    public async Task<ActionResult<MonthlyReportAnalysisJobResponse>> RetryParseAnalysisJob(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await _analysisJobStore.RetryParseAsync(User.GetRequiredUserId(), jobId, cancellationToken);
        return job is null ? NotFound(new ProblemDetails { Title = "Analysis job with saved AI output not found." }) : Ok(job.ToResponse());
    }

    [HttpGet("analyze/{jobId:guid}/raw-output")]
    public IActionResult DownloadRawAnalysisOutput(Guid jobId)
    {
        var job = _analysisJobStore.Get(User.GetRequiredUserId(), jobId);
        if (job is not { RawAiOutput: { Length: > 0 } rawOutput })
        {
            return NotFound(new ProblemDetails { Title = "Analysis job with saved AI output not found." });
        }

        return File(
            Encoding.UTF8.GetBytes(rawOutput),
            "application/json; charset=utf-8",
            $"ledgerra-ai-output-{jobId}.json");
    }

    [HttpPost("csv-preview")]
    public async Task<ActionResult<MonthlyReportAnalysisResponse>> PreviewCsv(
        [FromForm] CsvImportPreviewRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            if (request.File.Length == 0 || request.File.Length > 8 * 1024 * 1024)
            {
                throw new InvalidOperationException("CSV file must contain data and be 8 MB or smaller.");
            }

            var contentType = request.File.ContentType.Split(';')[0].Trim();
            if (!Path.GetExtension(request.File.FileName).Equals(".csv", StringComparison.OrdinalIgnoreCase)
                || !(string.IsNullOrEmpty(contentType)
                    || contentType.Equals("text/csv", StringComparison.OrdinalIgnoreCase)
                    || contentType.Equals("application/vnd.ms-excel", StringComparison.OrdinalIgnoreCase)
                    || contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)
                    || contentType.Equals("text/plain", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("CSV preview requires a CSV file.");
            }

            // Parse the original CSV directly; AI report normalization loses delimiters.
            await using var stream = request.File.OpenReadStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var csvContent = await reader.ReadToEndAsync(cancellationToken);
            var drafts = _csvBankImportMapper.Map(
                csvContent,
                request.AccountId,
                request.DateColumn,
                request.AmountColumn,
                request.DescriptionColumn,
                request.InvertAmounts);
            return Ok(new MonthlyReportAnalysisResponse(drafts.Select(MapDraft).ToList(), []));
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new ProblemDetails { Title = exception.Message });
        }
    }

    [HttpPost("commit")]
    public async Task<ActionResult<CommitMonthlyReportDraftsResponse>> Commit(
        CommitMonthlyReportDraftsRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _commitMonthlyReportDraftsCommandHandler.HandleAsync(
            new CommitMonthlyReportDraftsCommand(
                User.GetRequiredUserId(),
                request.Transactions.Select(draft => new MonthlyReportDraftInput(
                    draft.SourceId,
                    draft.AccountId,
                    draft.CategoryId,
                    draft.Amount,
                    draft.Type,
                    draft.OccurredOnUtc,
                    draft.Note)).ToList(),
                request.AcceptedDuplicateSourceIds),
            cancellationToken);

        if (result.IsNotFound)
        {
            return NotFound(new ProblemDetails { Title = result.NotFoundTitle });
        }

        if (result.HasValidationError)
        {
            return this.ValidationError(new Dictionary<string, string[]>
            {
                [result.ValidationKey!] = [result.ValidationMessage!]
            });
        }

        return Created(
            "/api/transactions",
            new CommitMonthlyReportDraftsResponse(result.Created!.Select(MapTransaction).ToList()));
    }

    private static TransactionResponse MapTransaction(TransactionDetails transaction)
    {
        return new TransactionResponse(
            transaction.Id,
            transaction.AccountId,
            transaction.CategoryId,
            transaction.Amount,
            transaction.Type.ToString(),
            transaction.OccurredOnUtc,
            transaction.Note,
            transaction.TransferGroupId,
            transaction.SavingsGoalId,
            transaction.SplitGroupId,
            transaction.ParentTransactionId);
    }

    private static MonthlyReportDraftTransactionResponse MapDraft(AnalyzedMonthlyReportDraft draft)
    {
        return new MonthlyReportDraftTransactionResponse(
            draft.SourceId,
            draft.AccountId,
            draft.CategoryId,
            draft.Amount,
            draft.Type,
            draft.OccurredOnUtc,
            draft.Note,
            draft.Confidence,
            draft.Warnings,
            draft.AppliedRuleId,
            draft.AppliedRuleName,
            draft.IsLikelyDuplicate,
            draft.DuplicateTransactionId,
            draft.DuplicateReason,
            draft.IsSelectedByDefault);
    }
}
