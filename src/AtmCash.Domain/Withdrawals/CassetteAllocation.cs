namespace AtmCash.Domain.Withdrawals;

public sealed record CassetteAllocation(Guid CassetteId, int Denomination, int Count);
