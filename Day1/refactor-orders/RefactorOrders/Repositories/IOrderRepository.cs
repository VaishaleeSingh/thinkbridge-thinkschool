using RefactorOrders.Models;

namespace RefactorOrders.Repositories;

public interface IOrderRepository
{
    Task<Order> AddAsync(
        Order order,
        CancellationToken cancellationToken);

    Task<Order?> GetByIdAsync(
        int id,
        CancellationToken cancellationToken);

    Task<Customer?> GetCustomerByEmailAsync(
        string email,
        CancellationToken cancellationToken);

    Task<Product?> GetProductBySkuAsync(
        string sku,
        CancellationToken cancellationToken);

    Task SaveChangesAsync(
        CancellationToken cancellationToken);
}