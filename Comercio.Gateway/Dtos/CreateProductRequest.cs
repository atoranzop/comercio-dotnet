namespace Comercio.Gateway.Dtos;

public record CreateProductRequest(
    string SKU,
    string Name,
    decimal Price,
    int Stock
);