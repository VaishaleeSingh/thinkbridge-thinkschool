namespace RefactorOrders.Services.Pricing;

public interface IDiscountStrategy
{
    decimal GetDiscountPercent(string? customerTier);
}
