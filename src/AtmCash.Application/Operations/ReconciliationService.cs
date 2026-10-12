using AtmCash.Application.Common;
using AtmCash.Domain.Reconciliation;

namespace AtmCash.Application.Operations;

public sealed class ReconciliationService(IOperationsStore store, TimeProvider clock)
{
    public Task<ReconciliationView> SubmitAsync(Guid atmId, IReadOnlyCollection<CassetteCount> counts, string user, CancellationToken token)
        => store.ExecuteAsync(atmId, async (session, cancellation) =>
        {
            var now = clock.GetOperationTime();
            var reconciliation = CashReconciliation.Create(session.Atm, counts, await session.ReadRatesAsync(cancellation), user, now);
            if (reconciliation.Status == ReconciliationStatus.AWAITING_APPROVAL)
                session.Atm.AwaitReconciliationApproval(reconciliation);
            else session.Atm.ApplyReconciliation(reconciliation, now);
            session.AddReconciliation(reconciliation);
            return reconciliation.ToView();
        }, token);

    public Task<ReconciliationView> ApproveAsync(Guid atmId, Guid id, string user, CancellationToken token)
        => store.ExecuteAsync(atmId, async (session, cancellation) =>
        {
            var reconciliation = await session.FindReconciliationAsync(id, cancellation) ?? throw new ReconciliationNotFoundException(id);
            reconciliation.ValidateApprover(user);
            session.Atm.ApplyReconciliation(reconciliation, clock.GetOperationTime(), user);
            return reconciliation.ToView();
        }, token);

    public async Task<ReconciliationView> GetAsync(Guid atmId, Guid id, CancellationToken token)
        => (await store.FindReconciliationAsync(atmId, id, token) ?? throw new ReconciliationNotFoundException(id)).ToView();
}
