using System.Data;
using System.Text.RegularExpressions;
using Ledgerra.Domain.Transactions;
using Ledgerra.Domain.Bookkeeping;
using Microsoft.EntityFrameworkCore;

namespace Ledgerra.Infrastructure.Persistence;

public sealed record TransferCandidate(Guid OutgoingId, Guid IncomingId, string FromAccount, string ToAccount,
    decimal Amount, string CurrencyCode, DateTime OutgoingDate, DateTime IncomingDate,
    string? OutgoingNote, string? IncomingNote, bool CanAutoLink, string Reason);

/// <summary>Finds existing opposite ledger entries without creating new money movements.</summary>
public sealed class TransferRecognitionService(LedgerraDbContext db)
{
    private static readonly Regex TransferText = new(@"\b(transfer|xfer|virement|überweisung|przelew|traspaso|credit card payment)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static bool IdentifiesAccount(string? note, Ledgerra.Domain.Accounts.Account account)
    {
        note ??= "";
        if (account.Name.Trim().Length >= 4 && Regex.IsMatch(note, @"(?<![\p{L}\p{N}])" + Regex.Escape(account.Name.Trim()) + @"(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) return true;
        var digits = new string((account.AccountNumberMasked ?? "").Where(char.IsDigit).ToArray());
        return digits.Length >= 4 && Regex.IsMatch(note, @"(?<!\d)" + Regex.Escape(digits[^4..]) + @"(?!\d)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }

    public async Task<IReadOnlyList<TransferCandidate>> FindAsync(Guid userId, int dayWindow = 3, CancellationToken ct = default)
    {
        if (dayWindow is < 0 or > 7) throw new ArgumentOutOfRangeException(nameof(dayWindow));
        var locked = db.EntryMetadata.Where(m => m.UserId == userId && m.Status == "Reconciled").Select(m => m.Id);
        var entries = await db.Transactions.Include(t => t.Account)
            .Where(t => t.UserId == userId && t.Amount > 0 && t.ParentTransactionId == null && t.SplitGroupId == null && t.TransferGroupId == null
                && (t.Type == TransactionType.Income || t.Type == TransactionType.Expense) && !locked.Contains(t.Id))
            .ToListAsync(ct);
        var references = await db.EntryMetadata.Where(m => m.UserId == userId && m.Reference != "").ToDictionaryAsync(m => m.Id, m => m.Reference.Trim(), ct);
        var incoming = entries.Where(t => t.Type == TransactionType.Income)
            .GroupBy(t => (t.Account!.CurrencyCode, t.Amount, t.OccurredOnUtc.Date)).ToDictionary(g => g.Key, g => g.ToList());
        var pairs = new List<(Transaction Outgoing, Transaction Incoming, bool Evidence, string Reason)>();
        foreach (var outgoing in entries.Where(t => t.Type == TransactionType.Expense))
        {
            for (var day = -dayWindow; day <= dayWindow; day++)
            {
                var date = outgoing.OccurredOnUtc.Date;
                if ((day < 0 && date < DateTime.MinValue.AddDays(-day)) || (day > 0 && date > DateTime.MaxValue.AddDays(-day))) continue;
                if (!incoming.TryGetValue((outgoing.Account!.CurrencyCode, outgoing.Amount, date.AddDays(day)), out var matches)) continue;
                foreach (var target in matches.Where(t => t.AccountId != outgoing.AccountId))
                {
                    var sameReference = references.TryGetValue(outgoing.Id, out var left) && left.Length >= 8 && left.Count(char.IsDigit) >= 4 && left.Distinct().Count() >= 3
                        && references.TryGetValue(target.Id, out var right) && left.Equals(right, StringComparison.OrdinalIgnoreCase);
                    var transferDescriptions = TransferText.IsMatch(outgoing.Note ?? "") && TransferText.IsMatch(target.Note ?? "")
                        && IdentifiesAccount(outgoing.Note, target.Account!) && IdentifiesAccount(target.Note, outgoing.Account!);
                    pairs.Add((outgoing, target, sameReference || transferDescriptions,
                        sameReference ? "Opposite amounts, nearby dates, and matching bank references." : transferDescriptions
                            ? "Opposite amounts, nearby dates, and transfer descriptions identifying both owned accounts."
                            : "Opposite amounts and nearby dates. Review the descriptions before linking."));
                    if (pairs.Count > 5000) throw new InvalidOperationException("Too many possible transfer matches. Link or reconcile older entries before scanning again.");
                }
            }
        }
        var counts = pairs.SelectMany(p => new[] { p.Outgoing.Id, p.Incoming.Id }).GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
        return pairs.Select(p => {
            var unique = counts[p.Outgoing.Id] == 1 && counts[p.Incoming.Id] == 1;
            return new TransferCandidate(p.Outgoing.Id, p.Incoming.Id, p.Outgoing.Account!.Name, p.Incoming.Account!.Name,
                p.Outgoing.Amount, p.Outgoing.Account.CurrencyCode, p.Outgoing.OccurredOnUtc, p.Incoming.OccurredOnUtc,
                p.Outgoing.Note, p.Incoming.Note, unique && p.Evidence, unique ? p.Reason : "Several entries match this amount/date. Choose the correct pair manually.");
        }).OrderByDescending(p => p.OutgoingDate).ThenBy(p => p.OutgoingId).ThenBy(p => p.IncomingId).ToList();
    }

    public static TransferMatch History(Guid group, Guid userId, Transaction outgoing, Transaction incoming, bool automatic) => new()
    {
        Id = group, UserId = userId, OutgoingId = outgoing.Id, IncomingId = incoming.Id,
        OutgoingCategoryId = outgoing.CategoryId, IncomingCategoryId = incoming.CategoryId,
        OutgoingType = outgoing.Type, IncomingType = incoming.Type, Automatic = automatic, MatchedAtUtc = DateTime.UtcNow
    };

    public async Task<int> RecognizeAsync(Guid userId, IReadOnlyCollection<Guid>? affectedIds = null, int dayWindow = 3, CancellationToken ct = default)
    {
        await using var tx = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        var pairs = (await FindAsync(userId, dayWindow, ct)).Where(p => p.CanAutoLink
            && (affectedIds is null || affectedIds.Contains(p.OutgoingId) || affectedIds.Contains(p.IncomingId))).ToList();
        var ids = pairs.SelectMany(p => new[] { p.OutgoingId, p.IncomingId }).ToList();
        var entries = await db.Transactions.Where(t => t.UserId == userId && ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        foreach (var pair in pairs)
        {
            var outgoing = entries[pair.OutgoingId]; var incoming = entries[pair.IncomingId];
            var group = Guid.NewGuid();
            db.TransferMatches.Add(History(group, userId, outgoing, incoming, true));
            outgoing.Type = TransactionType.TransferOut; incoming.Type = TransactionType.TransferIn;
            outgoing.TransferGroupId = incoming.TransferGroupId = group;
            outgoing.CategoryId = incoming.CategoryId = null;
        }
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return pairs.Count;
    }
}
