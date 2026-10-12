using AtmCash.Domain.Atms;

namespace AtmCash.Application.Atms;

/// <summary>Persists an ATM aggregate and serializes mutations to that ATM.</summary>
public interface IAtmStore
{
    Task AddAsync(Atm atm, CancellationToken cancellationToken);
    Task<Atm?> FindAsync(Guid id, CancellationToken cancellationToken);
    Task<Atm> UpdateAsync(Guid id, Action<Atm> change, CancellationToken cancellationToken);
}
