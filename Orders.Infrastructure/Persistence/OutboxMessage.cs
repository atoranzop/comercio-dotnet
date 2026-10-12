using System.Text.Json;
using MediatR;
using Orders.Core.Events;

namespace Orders.Infrastructure.Persistence;

public sealed class OutboxMessage
{
    public const string OrderCreatedEventType = "orders.order-created.v1";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public Guid Id { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; }
    public DateTime? ProcessedAtUtc { get; set; }
    public int Attempts { get; set; }
    public DateTime NextAttemptAtUtc { get; set; }
    public string? LastError { get; set; }

    public static OutboxMessage Create(OrderCreatedEvent notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        return new OutboxMessage
        {
            Id = Guid.NewGuid(),
            EventType = OrderCreatedEventType,
            Payload = JsonSerializer.Serialize(notification, JsonOptions),
            OccurredAtUtc = DateTime.UtcNow,
            NextAttemptAtUtc = DateTime.UtcNow
        };
    }

    public INotification DeserializeNotification()
    {
        if (EventType == OrderCreatedEventType)
        {
            return JsonSerializer.Deserialize<OrderCreatedEvent>(Payload, JsonOptions)
                ?? throw new InvalidOperationException(
                    $"Outbox message {Id} has an empty order-created payload.");
        }

        throw new InvalidOperationException(
            $"Outbox message {Id} has unsupported event type '{EventType}'.");
    }
}
