namespace RefactorOrders.Services.Pricing;

public interface ITaxStrategy
{
    decimal GetTaxRate(string state);
}
