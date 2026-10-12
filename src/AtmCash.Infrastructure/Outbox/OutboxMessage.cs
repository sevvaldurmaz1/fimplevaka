using AtmCash.Domain.Alerts;
using AtmCash.Domain.Cash;

namespace AtmCash.Infrastructure.Outbox;

public sealed class OutboxMessage
{
    private OutboxMessage() { }

    public OutboxMessage(LowCashAlert alert)
    {
        Id = Guid.NewGuid();
        AtmId = alert.AtmId;
        CassetteId = alert.CassetteId;
        CassetteNumber = alert.CassetteNumber;
        Currency = alert.Currency;
        Denomination = alert.Denomination;
        AvailableCount = alert.AvailableCount;
        CreatedAt = alert.OccurredAt;
    }

    public Guid Id { get; private set; }
    public string EventType { get; private set; } = "LOW_CASH";
    public Guid AtmId { get; private set; }
    public Guid CassetteId { get; private set; }
    public int CassetteNumber { get; private set; }
    public Currency Currency { get; private set; }
    public int Denomination { get; private set; }
    public int AvailableCount { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? SentAt { get; private set; }

    public void MarkSent(DateTimeOffset now) => SentAt = now;
}
