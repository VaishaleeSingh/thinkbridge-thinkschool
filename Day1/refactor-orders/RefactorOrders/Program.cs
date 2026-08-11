using Microsoft.EntityFrameworkCore;
using RefactorOrders.Data;
using RefactorOrders.Repositories;
using RefactorOrders.Services;
using RefactorOrders.Services.Pricing;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite("Data Source=orders.db"));

builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddScoped<IOrderRepository, OrderRepository>();
builder.Services.AddSingleton<IDiscountStrategy, TieredDiscountStrategy>();
builder.Services.AddSingleton<ITaxStrategy, StateTaxStrategy>();

var app = builder.Build();

app.MapControllers();

app.Run();

public partial class Program
{
}