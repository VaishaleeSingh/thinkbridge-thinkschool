using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using QuotesApi.Data;
using QuotesApi.Models;

namespace QuotesApi.Services;

public interface IAuthService
{
    Task<(string AccessToken, string RefreshToken, int ExpiresIn)?> LoginAsync(
        string email,
        string password,
        CancellationToken cancellationToken);

    string HashPassword(string password);
}

public sealed class AuthService : IAuthService
{
    private readonly QuotesDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<AuthService> _logger;

    public AuthService(QuotesDbContext db, IConfiguration config, ILogger<AuthService> logger)
    {
        _db = db;
        _config = config;
        _logger = logger;
    }

    public async Task<(string AccessToken, string RefreshToken, int ExpiresIn)?> LoginAsync(
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email, cancellationToken);

        if (user is null)
        {
            _logger.LogWarning("Login failed for email {Email}: user not found", email);
            return null;
        }

        var hasher = new PasswordHasher<User>();
        var result = hasher.VerifyHashedPassword(user, user.PasswordHash, password);

        if (result == PasswordVerificationResult.Failed)
        {
            _logger.LogWarning("Login failed for email {Email}: invalid password", email);
            return null;
        }

        var accessToken = GenerateAccessToken(user);
        var refreshToken = GenerateRefreshToken();
        const int expiresIn = 3600; // 1 hour in seconds

        _logger.LogInformation("User {Email} logged in successfully", email);

        return (accessToken, refreshToken, expiresIn);
    }

    public string HashPassword(string password)
    {
        var hasher = new PasswordHasher<User>();
        return hasher.HashPassword(new User(), password);
    }

    private string GenerateAccessToken(User user)
    {
        var key = _config["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key not configured");
        var issuer = _config["Jwt:Issuer"] ?? "QuotesApi";
        var audience = _config["Jwt:Audience"] ?? "QuotesApi";

        var keyBytes = Encoding.UTF8.GetBytes(key);
        var signingKey = new SymmetricSecurityKey(keyBytes);
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Email, user.Email)
        };

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string GenerateRefreshToken()
    {
        var randomNumber = new byte[32];
        using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
        rng.GetBytes(randomNumber);
        return Convert.ToBase64String(randomNumber);
    }
}
