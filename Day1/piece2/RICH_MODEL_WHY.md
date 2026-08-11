# Why Rich Domain Model Beats Anemic

## The Problem with Anemic

Anemic Quote was just properties: `Author` and `Text` were settable anywhere, anytime. Validation lived in the controller. Two issues:

1. **Invariants weren't enforced in the model.** If code outside the endpoint tried to create a Quote directly—say, a background job or an internal service—it could bypass validation. A Quote with empty text or a 500-char author would silently slip into the database.

2. **Text was mutable.** Once a quote was stored, nothing stopped someone from changing it later. In the old model, `quote.Text = "...new content"` was always allowed. This violates an invariant: a quote's content should be immutable—if you want to remove it, soft-delete.

## What Rich Model Buys You

**Invariants are enforced everywhere, not just at the entry point.** Any code path that creates a Quote must use `Quote.Create()`, which validates. The private constructor blocks direct instantiation.

**Text is immutable after creation.** The property is read-only (`public string Text { get; private set; }`). To "remove" a quote, call `Delete()` and check `IsDeleted`. The original text is preserved—useful for audits.

**Validation is centralized in the model.** The controller no longer duplicates the rules. New developers don't have to guess what the rules are; they're in `Quote.Create()`. If you add a new invariant later, you change it once.

**Domain errors are explicit.** `Quote.Create()` returns `(Quote? Quote, string? Error)`, so the caller knows validation can fail and must handle it. The anemic model gave you no signal—you just got a Quote with bad data.

## Real Scenario: The Bug the Rich Model Catches

**Anemic version:**
A developer adds an internal service that batch-imports quotes from an archive:
```csharp
public async Task ImportQuotesAsync(ImportedQuoteDto[] batch)
{
    foreach (var dto in batch)
    {
        var quote = new Quote 
        { 
            Author = dto.Author,      // ← 300 chars, no validation here
            Text = dto.Text            // ← 2000 chars, no validation here
        };
        await _repository.AddAsync(quote);
    }
}
```
The developer forgot the controller validation. Bad data hits the DB. The database constraints might catch it (if they exist), but by then you've wasted a batch import, logged errors, and had to debug.

**Rich version:**
The same developer uses the factory:
```csharp
foreach (var dto in batch)
{
    var (quote, error) = Quote.Create(dto.Author, dto.Text);
    if (quote is null)
    {
        _logger.LogWarning("Invalid quote: {Error}", error);
        continue;  // ← safe
    }
    await _repository.AddAsync(quote);
}
```
The invariants are enforced at the source. Bad data never reaches the repository. The developer gets immediate feedback during import.

## Bottom Line

Anemic models shift the burden of invariant enforcement to every caller. Rich models enforce invariants in the model itself. This scales: the more code paths, the more likely an anemic model's invariants slip through. Rich models make the invariants impossible to bypass.
