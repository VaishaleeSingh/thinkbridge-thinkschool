namespace RefactorOrders.Services.Pricing;

public class StateTaxStrategy : ITaxStrategy
{
    public decimal GetTaxRate(string state)
    {
        return state switch
        {
            "DE" or "MT" or "OR" or "NH" => 0m,
            "CA" => 0.0725m,
            "NY" => 0.08m,
            _ => 0.18m
        };
    }
}
