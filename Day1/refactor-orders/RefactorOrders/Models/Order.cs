namespace RefactorOrders.Models;

public class Order
{
    public int Id { get; set; }
    public string CustomerName { get; set; }
    public string CustomerEmail { get; set; }
    public string ShippingAddress { get; set; }
    public string Status { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal TaxAmount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ShippedAt { get; set; }
    public string Notes { get; set; }
    public List<OrderItem> Items { get; set; }
}

public class OrderItem
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public Order Order { get; set; }
    public string ProductName { get; set; }
    public string Sku { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal LineTotal { get; set; }
}

public class Product
{
    public int Id { get; set; }
    public string Name { get; set; }
    public string Sku { get; set; }
    public decimal Price { get; set; }
    public int StockQuantity { get; set; }
    public bool IsActive { get; set; }
    public string Category { get; set; }
}

public class Customer
{
    public int Id { get; set; }
    public string Name { get; set; }
    public string Email { get; set; }
    public string Tier { get; set; }  // "Gold", "Silver", "Bronze"
    public int LoyaltyPoints { get; set; }
    public DateTime JoinedAt { get; set; }
}
