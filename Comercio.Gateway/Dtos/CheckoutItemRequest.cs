namespace Comercio.Gateway.Dtos;

public record CheckoutItemRequest(
    Guid ProductId,
    int Quantity
);