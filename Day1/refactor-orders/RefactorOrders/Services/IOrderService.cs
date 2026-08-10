using RefactorOrders.DTOs;
using RefactorOrders.Models;

namespace RefactorOrders.Services;

public interface IOrderService
{
    Task<Order> CreateOrderAsync(
        CreateOrderRequest request,
        CancellationToken cancellationToken);

    Task<Order?> GetOrderAsync(
        int id,
        CancellationToken cancellationToken);

    Task<bool> CancelOrderAsync(
        int id,
        CancellationToken cancellationToken);
}