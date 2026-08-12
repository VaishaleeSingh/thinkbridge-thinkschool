using RefactorOrders.DTOs;
using RefactorOrders.Models;
using RefactorOrders.Repositories;
using RefactorOrders.Services;
using RefactorOrders.Services.Pricing;

namespace RefactorOrders.Tests;

public class OrderServiceTests
{
    [Fact]
    public async Task CreateOrder_ShouldCalculateGoldDiscount()
    {
        var repository = new FakeOrderRepository
        {
            Customer = new Customer
            {
                Email = "test@example.com",
                Name = "Test User",
                Tier = "Gold",
                LoyaltyPoints = 0
            },
            Product = new Product
            {
                Name = "Laptop",
                Sku = "LAP-1",
                Price = 100,
                StockQuantity = 10,
                IsActive = true
            }
        };

        var service = new OrderService(
            repository,
            new TestLogger<OrderService>(),
            new TieredDiscountStrategy(),
            new StateTaxStrategy());

        var order = await service.CreateOrderAsync(
            new CreateOrderRequest
            {
                CustomerEmail = "test@example.com",
                CustomerName = "Test User",
                ShippingAddress = "123 Main Street",
                State = "CA",
                Items =
                [
                    new CreateOrderItemRequest
                    {
                        Sku = "LAP-1",
                        Quantity = 2
                    }
                ]
            },
            CancellationToken.None);

        Assert.Equal(15, order.DiscountPercent);
        Assert.Equal(200, repository.Product!.Price * 2);
    }

    [Fact]
    public async Task CreateOrder_ShouldRejectMissingItems()
    {
        var repository = new FakeOrderRepository();

        var service = new OrderService(
            repository,
            new TestLogger<OrderService>(),
            new TieredDiscountStrategy(),
            new StateTaxStrategy());

        var request = new CreateOrderRequest
        {
            CustomerEmail = "test@example.com",
            CustomerName = "Test User",
            ShippingAddress = "123 Main Street",
            Items = []
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateOrderAsync(
                request,
                CancellationToken.None));
    }

    [Fact]
    public async Task CreateOrder_ShouldRejectEmptyCustomerEmail()
    {
        var repository = new FakeOrderRepository();

        var service = new OrderService(
            repository,
            new TestLogger<OrderService>(),
            new TieredDiscountStrategy(),
            new StateTaxStrategy());

        var request = new CreateOrderRequest
        {
            CustomerEmail = "",
            CustomerName = "Test User",
            ShippingAddress = "123 Main Street",
            Items =
            [
                new CreateOrderItemRequest
                {
                    Sku = "LAP-1",
                    Quantity = 1
                }
            ]
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateOrderAsync(
                request,
                CancellationToken.None));
    }

    [Fact]
    public async Task CreateOrder_ShouldRejectInsufficientStock()
    {
        var repository = new FakeOrderRepository
        {
            Product = new Product
            {
                Name = "Phone",
                Sku = "PHONE-1",
                Price = 500,
                StockQuantity = 1,
                IsActive = true
            }
        };

        var service = new OrderService(
            repository,
            new TestLogger<OrderService>(),
            new TieredDiscountStrategy(),
            new StateTaxStrategy());

        var request = new CreateOrderRequest
        {
            CustomerEmail = "test@example.com",
            CustomerName = "Test User",
            ShippingAddress = "123 Main Street",
            Items =
            [
                new CreateOrderItemRequest
                {
                    Sku = "PHONE-1",
                    Quantity = 5
                }
            ]
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateOrderAsync(
                request,
                CancellationToken.None));
    }

    [Fact]
    public async Task CreateOrder_ShouldRejectNegativeQuantity()
    {
        var repository = new FakeOrderRepository
        {
            Product = new Product
            {
                Name = "Phone",
                Sku = "PHONE-1",
                Price = 500,
                StockQuantity = 10,
                IsActive = true
            }
        };

        var service = new OrderService(
            repository,
            new TestLogger<OrderService>(),
            new TieredDiscountStrategy(),
            new StateTaxStrategy());

        var request = new CreateOrderRequest
        {
            CustomerEmail = "test@example.com",
            CustomerName = "Test User",
            ShippingAddress = "123 Main Street",
            Items =
            [
                new CreateOrderItemRequest
                {
                    Sku = "PHONE-1",
                    Quantity = -1
                }
            ]
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateOrderAsync(
                request,
                CancellationToken.None));
    }

    [Fact]
    public async Task CreateOrder_ShouldRejectZeroQuantity()
    {
        var repository = new FakeOrderRepository
        {
            Product = new Product
            {
                Name = "Phone",
                Sku = "PHONE-1",
                Price = 500,
                StockQuantity = 10,
                IsActive = true
            }
        };

        var service = new OrderService(
            repository,
            new TestLogger<OrderService>(),
            new TieredDiscountStrategy(),
            new StateTaxStrategy());

        var request = new CreateOrderRequest
        {
            CustomerEmail = "test@example.com",
            CustomerName = "Test User",
            ShippingAddress = "123 Main Street",
            Items =
            [
                new CreateOrderItemRequest
                {
                    Sku = "PHONE-1",
                    Quantity = 0
                }
            ]
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateOrderAsync(
                request,
                CancellationToken.None));
    }
}