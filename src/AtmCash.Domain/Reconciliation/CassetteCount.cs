using AtmCash.Domain.Cash;

namespace AtmCash.Domain.Reconciliation;

public sealed record CassetteCount(int Number, IReadOnlyCollection<NoteBatch> Notes);
