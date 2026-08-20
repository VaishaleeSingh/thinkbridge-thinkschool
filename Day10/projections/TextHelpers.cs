namespace QueryTranslation.Demo;

/// <summary>
/// Exists for exactly one reason: Part 3b needs a plain C# method that EF Core
/// cannot translate, called from inside a projection, to demonstrate silent
/// client-side evaluation.
///
/// It is a static method on a class rather than a local function because an
/// expression tree cannot contain a reference to a local function at all --
/// that is a compile error (CS8110), not a runtime client-side evaluation. To
/// demonstrate the accident, the call has to be something the compiler will
/// happily put INTO the expression tree and EF will then discover it cannot
/// translate. A static method does that; a local function never gets far
/// enough to be interesting.
/// </summary>
public static class TextHelpers
{
    public static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..length];
}
