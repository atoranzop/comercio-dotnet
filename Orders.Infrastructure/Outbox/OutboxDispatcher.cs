using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orders.Infrastructure.Persistence;

namespace Orders.Infrastructure.Outbox;

public sealed class OutboxDispatcher(
    OrdersDbContext dbContext,
    IMediator mediator,
    ILogger<OutboxDispatcher> logger)
{
    private const int MaximumErrorLength = 4000;

    public async Task<bool> DispatchNextAsync(CancellationToken cancellationToken)
    {
        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var messages = await dbContext.OutboxMessages
            .FromSqlRaw(
                """
                SELECT *
                FROM orders."OutboxMessages"
                WHERE "ProcessedAtUtc" IS NULL
                  AND "NextAttemptAtUtc" <= NOW()
                ORDER BY "OccurredAtUtc"
                LIMIT 1
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);

        var message = messages.FirstOrDefault();
        if (message is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        try
        {
            await mediator.Publish(
                message.DeserializeNotification(),
                cancellationToken);

            message.ProcessedAtUtc = DateTime.UtcNow;
            message.LastError = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            message.Attempts++;
            message.NextAttemptAtUtc = DateTime.UtcNow.AddSeconds(
                Math.Min(300, Math.Pow(2, message.Attempts)));
            message.LastError = exception.ToString();
            if (message.LastError.Length > MaximumErrorLength)
            {
                message.LastError = message.LastError[..MaximumErrorLength];
            }

            logger.LogError(
                exception,
                "Failed to dispatch outbox message {OutboxMessageId}; attempt {Attempt}.",
                message.Id,
                message.Attempts);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (message.ProcessedAtUtc.HasValue)
        {
            logger.LogInformation(
                "Dispatched outbox message {OutboxMessageId} of type {EventType}.",
                message.Id,
                message.EventType);
        }

        return true;
    }
}
