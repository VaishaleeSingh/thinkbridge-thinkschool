using Microsoft.Extensions.Logging;
using RefactorOrders.Models;
using RefactorOrders.Repositories;

namespace RefactorOrders.Tests;

public class FakeOrderRepository : IOrderRepository
{
    public Customer? Customer { get; set; }
    public Product? Product { get; set; }
    public Order? SavedOrder { get; private set; }

    public Task<Customer?> GetCustomerByEmailAsync(
        string email,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(Customer);
    }

    public Task<Product?> GetProductBySkuAsync(
        string sku,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(Product);
    }

    public Task<Order> AddAsync(
        Order order,
        CancellationToken cancellationToken)
    {
        SavedOrder = order;
        order.Id = 1;
        return Task.FromResult(order);
    }

    public Task<Order?> GetByIdAsync(
        int id,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(SavedOrder);
    }

    public Task SaveChangesAsync(
        CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

public class TestLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => false;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
    }
}