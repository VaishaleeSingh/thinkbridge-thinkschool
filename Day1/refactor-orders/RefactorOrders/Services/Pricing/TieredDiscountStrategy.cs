namespace RefactorOrders.Services.Pricing;

public class TieredDiscountStrategy : IDiscountStrategy
{
    public decimal GetDiscountPercent(string? customerTier)
    {
        return customerTier switch
        {
            "Gold" => 15m,
            "Silver" => 10m,
            "Bronze" => 5m,
            _ => 0m
        };
    }
}
