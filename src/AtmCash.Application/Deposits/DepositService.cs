using AtmCash.Application.Atms;
using AtmCash.Application.Common;
using AtmCash.Domain.Cash;

namespace AtmCash.Application.Deposits;

public sealed class DepositService(IAtmStore store, TimeProvider clock)
{
    public async Task<DepositView> DepositAsync(Guid atmId, IReadOnlyCollection<NoteBatch> notes,
        int unrecognizedCount, CancellationToken cancellationToken)
        => (await store.UpdateAsync(atmId, atm => atm.Deposit(notes, unrecognizedCount, clock.GetOperationTime()),
            cancellationToken)).ToView();
}
