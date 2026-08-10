namespace RefactorOrders.DTOs;

public class CreateOrderRequest
{
    public string CustomerEmail { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public string ShippingAddress { get; set; } = "";
    public string State { get; set; } = "";

    public List<CreateOrderItemRequest> Items { get; set; } = [];
}

public class CreateOrderItemRequest
{
    public string Sku { get; set; } = "";
    public int Quantity { get; set; }
}