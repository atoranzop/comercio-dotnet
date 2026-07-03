namespace Comercio.Gateway.Dtos;

public record CheckoutRequest(
    Guid CustomerId,
    List<CheckoutItemRequest> Items
);