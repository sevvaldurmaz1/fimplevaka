using AtmCash.Domain.Cash;

namespace AtmCash.Domain.Alerts;

public sealed record LowCashAlert(Guid AtmId, Guid CassetteId, int CassetteNumber,
    Currency Currency, int Denomination, int AvailableCount, DateTimeOffset OccurredAt);
