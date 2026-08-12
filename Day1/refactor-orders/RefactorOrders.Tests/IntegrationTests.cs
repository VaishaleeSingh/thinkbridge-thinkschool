using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace RefactorOrders.Tests;

public class IntegrationTests
{
    [Fact]
    public async Task GetMissingOrder_ReturnsNotFound()
    {
        await using var factory = new WebApplicationFactory<RefactorOrders.Program>();

        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/order/999999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}