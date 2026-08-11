using System.IdentityModel.Tokens.Jwt;
using BCrypt.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using QuotesApi.Data;
using QuotesApi.Models;

namespace QuotesApi.Tests;

public class AuthJwtTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        // Seed a test user
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QuotesDbContext>();
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var user = new User
        {
            Email = "test@example.com",
            PasswordHash = BCrypt.HashPassword("password123"),
            CreatedAt = DateTime.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Login_ReturnsTokens_WithValidCredentials()
    {
        var loginRequest = new { email = "test@example.com", password = "password123" };
        var content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(loginRequest),
            System.Text.Encoding.UTF8,
            "application/json");

        var response = await _client.PostAsync("/api/auth/login", content);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        var json = await response.Content.ReadAsAsync<dynamic>();
        ((string)json.accessToken).Should().NotBeNullOrEmpty();
        ((string)json.refreshToken).Should().NotBeNullOrEmpty();
        ((int)json.expiresIn).Should().Be(3600);
    }

    [Fact]
    public async Task Login_Returns401_WithInvalidPassword()
    {
        var loginRequest = new { email = "test@example.com", password = "wrongpassword" };
        var content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(loginRequest),
            System.Text.Encoding.UTF8,
            "application/json");

        var response = await _client.PostAsync("/api/auth/login", content);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PostQuote_Returns401_WithoutToken()
    {
        var quoteRequest = new { author = "Confucius", text = "The man who moves a mountain begins by carrying away small stones." };
        var content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(quoteRequest),
            System.Text.Encoding.UTF8,
            "application/json");

        var response = await _client.PostAsync("/api/quotes", content);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.Should().NotBeEmpty();
    }

    [Fact]
    public async Task PostQuote_Returns201_WithValidToken()
    {
        // Login to get token
        var loginRequest = new { email = "test@example.com", password = "password123" };
        var loginContent = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(loginRequest),
            System.Text.Encoding.UTF8,
            "application/json");

        var loginResponse = await _client.PostAsync("/api/auth/login", loginContent);
        var loginJson = await loginResponse.Content.ReadAsAsync<dynamic>();
        var token = (string)loginJson.accessToken;

        // Create quote with token
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var quoteRequest = new { author = "Confucius", text = "The man who moves a mountain begins by carrying away small stones." };
        var quoteContent = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(quoteRequest),
            System.Text.Encoding.UTF8,
            "application/json");

        var response = await _client.PostAsync("/api/quotes", quoteContent);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Created);
    }

    [Fact]
    public async Task GetQuotes_Returns200_WithoutToken()
    {
        var response = await _client.GetAsync("/api/quotes?page=1&size=10");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }
}
