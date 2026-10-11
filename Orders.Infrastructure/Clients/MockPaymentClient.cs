using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Orders.Core.Entities;

namespace Orders.Infrastructure.Clients;

/// <summary>
/// Mocked HTTP client of perimetral integration with third-parties 
/// payment runways
/// </summary>
/// <param name="httpClient"></param>
/// <param name="logger"></param>
public class MockPaymentClient(
    HttpClient httpClient, 
    ILogger<MockPaymentClient> logger
)
{
    /// <summary>
    /// Simulates the cashing confirmation for an order
    /// </summary>
    /// <param name="orderId"></param>
    /// <param name="amount"></param>
    /// <returns></returns>
    public async Task<bool> ProcessPaymentAsync(Guid orderId, decimal amount)
    {
        logger.LogInformation(
            "[PAYMENTS] Starting external payment process for order {OrderId}" +
            "for an amount of $ {Amount}", orderId, amount
        );

        try
        {
            // We target a mocked endpoint to test retry and disjuntor of Polly
            var response = await httpClient.PostAsJsonAsync(
                $"/api/external/payments/process",
                new
                {
                    OrderId = orderId,
                    Amount = amount
                }
            );

            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation(
                    "[PAYMENTS] Charging aprooved successfully by the payment runway " +
                    " for the order {OrderId}.", orderId
                );

                return true;
            }

            logger.LogWarning(
                "[PAYMENTS] The payment runway rejected or failed the request with " +
                "HTTP status code {StatusCode} for order {OrderId}.",
                response.StatusCode, orderId
            );
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[PAYMENTS] Error on the physical network while" +
            "connecting with payment runway");
            throw;
        }
    }
}