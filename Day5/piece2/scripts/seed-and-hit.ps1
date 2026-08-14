# Seeds data and hits GET /api/collections so the listing endpoint produces
# a trace worth reading.
#
# There is no register endpoint, so rather than logging in this mints a token
# directly with the development signing key -- exactly what the tests in
# QuotesApi.Tests do. Same key, issuer and audience the API validates
# against, so the API cannot tell the difference.
#
# Usage (with the API already running via `dotnet run` in ../QuotesApi):
#   .\seed-and-hit.ps1
#   .\seed-and-hit.ps1 -Collections 30 -Requests 10

param(
    [int]$Collections = 15,
    [int]$QuotesPerCollection = 3,
    [int]$Requests = 5,
    [string]$BaseUrl = "http://localhost:5059",
    [string]$Secret = "local-dev-only-signing-key-please-replace-me-32+chars"
)

$ErrorActionPreference = "Stop"

function ConvertTo-Base64Url([byte[]]$bytes) {
    [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function New-DevToken {
    $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()

    $header = ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes('{"alg":"HS256","typ":"JWT"}'))

    $claims = [ordered]@{
        sub   = "seed-user"
        email = "seed@example.com"
        scope = @("quotes.read", "quotes.write", "quotes.delete",
                  "collections.read", "collections.write", "collections.delete")
        iss   = "https://yourapp.com"
        aud   = "quotes-api"
        nbf   = $now
        exp   = $now + 3600
    }

    $payload = ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes(($claims | ConvertTo-Json -Compress)))

    $hmac = [System.Security.Cryptography.HMACSHA256]::new([Text.Encoding]::UTF8.GetBytes($Secret))
    $signature = ConvertTo-Base64Url ($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes("$header.$payload")))

    "$header.$payload.$signature"
}

$headers = @{ Authorization = "Bearer $(New-DevToken)" }

# Quotes first -- a collection item is only a QuoteId, so there has to be
# something for it to point at.
Write-Host "Creating $QuotesPerCollection quotes..." -ForegroundColor Cyan
$quoteIds = 1..$QuotesPerCollection | ForEach-Object {
    $body = @{ author = "Author $_"; text = "Quote number $_, long enough to be valid." } | ConvertTo-Json
    $created = Invoke-RestMethod -Uri "$BaseUrl/api/quotes" -Method Post `
        -Headers $headers -ContentType 'application/json' -Body $body
    $created.id
}

Write-Host "Seeding $Collections collections, each with $QuotesPerCollection quotes..." -ForegroundColor Cyan
1..$Collections | ForEach-Object {
    $body = @{ name = "Collection $_" } | ConvertTo-Json
    $collection = Invoke-RestMethod -Uri "$BaseUrl/api/collections" -Method Post `
        -Headers $headers -ContentType 'application/json' -Body $body

    foreach ($quoteId in $quoteIds) {
        $itemBody = @{ quoteId = $quoteId } | ConvertTo-Json
        Invoke-RestMethod -Uri "$BaseUrl/api/collections/$($collection.id)/items" -Method Post `
            -Headers $headers -ContentType 'application/json' -Body $itemBody | Out-Null
    }
}

Write-Host "Calling GET /api/collections $Requests times..." -ForegroundColor Cyan
$timings = 1..$Requests | ForEach-Object {
    $elapsed = Measure-Command {
        Invoke-RestMethod -Uri "$BaseUrl/api/collections" -Headers $headers | Out-Null
    }
    [math]::Round($elapsed.TotalMilliseconds)
}

Write-Host ""
Write-Host "Response times (ms): $($timings -join ', ')" -ForegroundColor Yellow
Write-Host "Expected DB spans per request while the N+1 is present: 1 + $Collections"
Write-Host "Now open http://localhost:16686 -> service QuotesApi -> GET /api/collections"
