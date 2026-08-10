using RefactorOrders.DTOs;
using RefactorOrders.Models;
using RefactorOrders.Repositories;
using RefactorOrders.Services.Pricing;

namespace RefactorOrders.Services;

public class OrderService : IOrderService
{
    private readonly IOrderRepository _repository;
    private readonly ILogger<OrderService> _logger;
    private readonly IDiscountStrategy _discountStrategy;
    private readonly ITaxStrategy _taxStrategy;

    public OrderService(
        IOrderRepository repository,
        ILogger<OrderService> logger,
        IDiscountStrategy discountStrategy,
        ITaxStrategy taxStrategy)
    {
        _repository = repository;
        _logger = logger;
        _discountStrategy = discountStrategy;
        _taxStrategy = taxStrategy;
    }

    public async Task<Order> CreateOrderAsync(
        CreateOrderRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.CustomerEmail))
            throw new ArgumentException("Customer email is required.");

        if (string.IsNullOrWhiteSpace(request.CustomerName))
            throw new ArgumentException("Customer name is required.");

        if (string.IsNullOrWhiteSpace(request.ShippingAddress) ||
            request.ShippingAddress.Length < 5)
            throw new ArgumentException("Shipping address is too short.");

        if (request.Items.Count == 0)
            throw new ArgumentException("Order must have at least one item.");

        var customer = await _repository.GetCustomerByEmailAsync(
            request.CustomerEmail,
            cancellationToken);

        var discountPercent = _discountStrategy.GetDiscountPercent(
            customer?.Tier);

        var orderItems = new List<OrderItem>();
        decimal subtotal = 0;

        foreach (var item in request.Items)
        {
            if (item.Quantity <= 0)
                throw new ArgumentException(
                    $"Quantity must be greater than zero for {item.Sku}.");

            var product = await _repository.GetProductBySkuAsync(
                item.Sku,
                cancellationToken);

            if (product is null)
                throw new KeyNotFoundException(
                    $"Product not found: {item.Sku}");

            if (!product.IsActive)
                throw new InvalidOperationException(
                    $"Product is discontinued: {item.Sku}");

            if (product.StockQuantity < item.Quantity)
                throw new InvalidOperationException(
                    $"Insufficient stock for {item.Sku}.");

            product.StockQuantity -= item.Quantity;

            var lineTotal = product.Price * item.Quantity;
            subtotal += lineTotal;

            orderItems.Add(new OrderItem
            {
                ProductName = product.Name,
                Sku = product.Sku,
                Quantity = item.Quantity,
                UnitPrice = product.Price,
                LineTotal = lineTotal
            });
        }

        var discountAmount =
            subtotal * (discountPercent / 100m);

        var afterDiscount = subtotal - discountAmount;

        var taxRate = _taxStrategy.GetTaxRate(request.State);
        var taxAmount = afterDiscount * taxRate;
        var totalAmount = afterDiscount + taxAmount;

        if (totalAmount > 10000)
        {
            _logger.LogWarning(
                "Large order detected for {CustomerEmail}, amount {TotalAmount}",
                request.CustomerEmail,
                totalAmount);
        }

        var order = new Order
        {
            CustomerName = request.CustomerName,
            CustomerEmail = request.CustomerEmail,
            ShippingAddress = request.ShippingAddress,
            Status = "Pending",
            TotalAmount = totalAmount,
            DiscountPercent = discountPercent,
            TaxAmount = taxAmount,
            CreatedAt = DateTime.UtcNow,
            Notes = "",
            Items = orderItems
        };

        if (customer is not null)
        {
            customer.LoyaltyPoints += (int)(totalAmount / 10);
        }

        await _repository.AddAsync(order, cancellationToken);

        _logger.LogInformation(
            "Order {OrderId} created for {CustomerEmail}",
            order.Id,
            request.CustomerEmail);

        return order;
    }

    public async Task<Order?> GetOrderAsync(
        int id,
        CancellationToken cancellationToken)
    {
        return await _repository.GetByIdAsync(
            id,
            cancellationToken);
    }

    public async Task<bool> CancelOrderAsync(
        int id,
        CancellationToken cancellationToken)
    {
        var order = await _repository.GetByIdAsync(
            id,
            cancellationToken);

        if (order is null)
            return false;

        if (order.Status != "Pending")
            throw new InvalidOperationException(
                "Only pending orders can be cancelled.");

        foreach (var item in order.Items)
        {
            var product = await _repository.GetProductBySkuAsync(
                item.Sku,
                cancellationToken);

            if (product is not null)
                product.StockQuantity += item.Quantity;
        }

        order.Status = "Cancelled";

        await _repository.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Order {OrderId} cancelled",
            id);

        return true;
    }

}