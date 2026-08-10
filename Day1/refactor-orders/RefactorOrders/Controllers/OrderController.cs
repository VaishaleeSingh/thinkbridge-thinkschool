using Microsoft.AspNetCore.Mvc;
using RefactorOrders.DTOs;
using RefactorOrders.Services;

namespace RefactorOrders.Controllers;

[ApiController]
[Route("api/[controller]")]
public class OrderController : ControllerBase
{
    private readonly IOrderService _orderService;

    public OrderController(IOrderService orderService)
    {
        _orderService = orderService;
    }

    [HttpPost]
    public async Task<ActionResult<OrderResponse>> CreateOrder(
        [FromBody] CreateOrderRequest request,
        CancellationToken cancellationToken)
    {
        var order = await _orderService.CreateOrderAsync(
            request,
            cancellationToken);

        return Ok(ToResponse(order));
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<OrderResponse>> GetOrder(
        int id,
        CancellationToken cancellationToken)
    {
        var order = await _orderService.GetOrderAsync(
            id,
            cancellationToken);

        if (order is null)
            return NotFound(new { message = "Order not found" });

        return Ok(ToResponse(order));
    }

    [HttpPut("{id:int}/cancel")]
    public async Task<IActionResult> CancelOrder(
        int id,
        CancellationToken cancellationToken)
    {
        var cancelled = await _orderService.CancelOrderAsync(
            id,
            cancellationToken);

        if (!cancelled)
            return NotFound(new { message = "Order not found" });

        return Ok(new
        {
            success = true,
            message = "Order cancelled"
        });
    }

    private static OrderResponse ToResponse(
        RefactorOrders.Models.Order order)
    {
        return new OrderResponse
        {
            Success = true,
            OrderId = order.Id,
            InvoiceNumber =
                $"INV-{order.CreatedAt.Year}-{order.Id:D6}",
            CustomerName = order.CustomerName,
            CustomerEmail = order.CustomerEmail,
            Tier = null,
            LoyaltyPoints = 0,
            Items = order.Items.Select(item => new OrderItemResponse
            {
                Product = item.ProductName,
                Sku = item.Sku,
                Quantity = item.Quantity,
                Price = item.UnitPrice,
                Total = item.LineTotal
            }).ToList(),
            TotalAmount = order.TotalAmount,
            DiscountPercent = order.DiscountPercent,
            TaxAmount = order.TaxAmount,
            ShippingAddress = order.ShippingAddress,
            Status = order.Status,
            CreatedAt = order.CreatedAt
        };
    }
}

public class OrderResponse
{
    public bool Success { get; set; }
    public int OrderId { get; set; }
    public string InvoiceNumber { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public string CustomerEmail { get; set; } = "";
    public string? Tier { get; set; }
    public int LoyaltyPoints { get; set; }
    public List<OrderItemResponse> Items { get; set; } = [];
    public decimal TotalAmount { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal TaxAmount { get; set; }
    public string ShippingAddress { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class OrderItemResponse
{
    public string Product { get; set; } = "";
    public string Sku { get; set; } = "";
    public int Quantity { get; set; }
    public decimal Price { get; set; }
    public decimal Total { get; set; }
}