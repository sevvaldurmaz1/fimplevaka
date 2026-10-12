using AtmCash.Domain.Atms;
using AtmCash.Application.Common;

namespace AtmCash.Application.Atms;

public sealed class AtmService(IAtmStore store, TimeProvider clock)
{
    public async Task<AtmView> CreateAsync(string name, CancellationToken cancellationToken)
    {
        var atm = Atm.Create(name);
        await store.AddAsync(atm, cancellationToken);
        return atm.ToView();
    }

    public async Task<AtmView> GetAsync(Guid id, CancellationToken cancellationToken)
        => (await store.FindAsync(id, cancellationToken) ?? throw new AtmNotFoundException(id)).ToView();

    public async Task<AtmView> ConfigureAsync(Guid id, IReadOnlyCollection<CassetteConfiguration> configurations,
        CancellationToken cancellationToken)
        => (await store.UpdateAsync(id, atm => atm.Configure(configurations), cancellationToken)).ToView();

    public async Task<AtmView> LoadAsync(Guid id, IReadOnlyCollection<CashLoad> loads, CancellationToken cancellationToken)
        => (await store.UpdateAsync(id, atm => atm.Load(loads, clock.GetOperationTime()), cancellationToken)).ToView();

    public async Task<AtmView> ChangeStateAsync(Guid id, AtmState target, CancellationToken cancellationToken)
        => (await store.UpdateAsync(id, atm => atm.ChangeState(target), cancellationToken)).ToView();

    public async Task<AtmView> UnloadAsync(Guid id, IReadOnlyCollection<CashLoad> notes, CancellationToken cancellationToken)
        => (await store.UpdateAsync(id, atm => atm.Unload(notes, clock.GetOperationTime()), cancellationToken)).ToView();
}
