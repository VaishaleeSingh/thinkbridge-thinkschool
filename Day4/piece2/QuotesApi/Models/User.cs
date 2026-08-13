namespace QuotesApi.Models;

/// <summary>
/// A registered account that can log in with an email + password to get
/// our own self-issued JWT (see AuthService). This is separate from an
/// Entra ID identity: Entra users never get a row here at all, because
/// Azure is the one who authenticates them, not us.
/// </summary>
public class User
{
    public int Id { get; set; }
    public string Email { get; set; } = null!;

    // Never store a raw password. This holds the OUTPUT of
    // PasswordHasher<User>.HashPassword(...) -- a salted hash that can be
    // verified against a password later, but can't be reversed back into
    // the original password.
    public string PasswordHash { get; set; } = null!;

    public DateTime CreatedAt { get; set; }
}
