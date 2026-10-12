using AtmCash.Domain.Atms;
using AtmCash.Domain.Cash;
using AtmCash.Domain.Common;

namespace AtmCash.Domain.Reconciliation;

public enum ReconciliationStatus { AWAITING_APPROVAL, APPLIED }

public sealed class CashReconciliation
{
    public const decimal ApprovalThresholdTry = 1000m;
    private readonly List<ReconciliationLine> _lines = [];

    private CashReconciliation() { }

    public Guid Id { get; private set; }
    public Guid AtmId { get; private set; }
    public string SubmittedBy { get; private set; } = string.Empty;
    public string? ApprovedBy { get; private set; }
    public DateTimeOffset SubmittedAt { get; private set; }
    public DateTimeOffset? AppliedAt { get; private set; }
    public decimal TotalDifferenceTry { get; private set; }
    public ReconciliationStatus Status { get; private set; }
    public IReadOnlyCollection<ReconciliationLine> Lines => _lines.AsReadOnly();

    public static CashReconciliation Create(Atm atm, IReadOnlyCollection<CassetteCount> counts,
        IReadOnlyDictionary<Currency, decimal> rates, string user, DateTimeOffset now)
    {
        user = ValidateUser(user);
        atm.ValidateReconciliation(counts);
        var reconciliation = new CashReconciliation
        {
            Id = Guid.NewGuid(),
            AtmId = atm.Id,
            SubmittedBy = user,
            SubmittedAt = now
        };
        foreach (var count in counts.OrderBy(item => item.Number))
        {
            var cassette = atm.Cassettes.Single(item => item.Number == count.Number);
            var measured = count.Notes.ToDictionary(note => (note.Currency, note.Denomination), note => note.Count);
            var stored = cassette.Inventory.ToDictionary(note => (note.Currency, note.Denomination), note => note.NoteCount);
            foreach (var key in measured.Keys.Union(stored.Keys).OrderBy(key => key.Currency).ThenBy(key => key.Denomination))
                reconciliation._lines.Add(new ReconciliationLine(reconciliation.Id, cassette.Id, cassette.Number,
                    key.Currency, key.Denomination, stored.GetValueOrDefault(key), measured.GetValueOrDefault(key),
                    ExchangeRate.Require(rates, key.Currency)));
        }
        reconciliation.TotalDifferenceTry = reconciliation._lines.Sum(line => line.AbsoluteDifferenceTry);
        reconciliation.Status = reconciliation.TotalDifferenceTry > ApprovalThresholdTry
            ? ReconciliationStatus.AWAITING_APPROVAL : ReconciliationStatus.APPLIED;
        return reconciliation;
    }

    public void ValidateApprover(string user)
    {
        user = ValidateUser(user);
        if (Status != ReconciliationStatus.AWAITING_APPROVAL)
            throw new DomainException("reconciliation_already_applied", "This reconciliation has already been applied.");
        if (string.Equals(user, SubmittedBy, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("same_user_approval", "A different user must approve the reconciliation.");
    }

    public void MarkApplied(string? approver, DateTimeOffset now)
    {
        if (AppliedAt is not null)
            throw new DomainException("reconciliation_already_applied", "This reconciliation has already been applied.");
        if (Status == ReconciliationStatus.AWAITING_APPROVAL)
        {
            ValidateApprover(approver ?? string.Empty);
            ApprovedBy = ValidateUser(approver!);
        }
        Status = ReconciliationStatus.APPLIED;
        AppliedAt = now;
    }

    private static string ValidateUser(string user)
    {
        if (string.IsNullOrWhiteSpace(user) || user.Trim().Length > 100)
            throw new DomainException("invalid_operations_user", "X-User must contain between 1 and 100 characters.");
        return user.Trim();
    }
}
