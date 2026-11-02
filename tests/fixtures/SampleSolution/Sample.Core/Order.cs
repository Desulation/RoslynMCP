namespace Sample.Core;

public record Order(int Id, decimal Total, OrderStatus Status, string? Note = null);
