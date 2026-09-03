<#
.SYNOPSIS
    Measures the hit rate and the database-load drop, cache off then cache on.

.DESCRIPTION
    Runs the SAME load twice against the SAME seeded data, changing exactly one
    thing between runs: Cache:Enabled. Both numbers come from the same
    instrument (DbCommandCounterInterceptor, read through /api/cache/stats), so
    the comparison is a comparison.

    Sequence per run:
      1. Start the API with Cache:Enabled set for this run.
      2. Seed, so both runs read the same volume. Measuring the baseline on 20
         rows and the cached run on 20,000 measures the seed, not the cache.
      3. Read /api/cache/stats and zero nothing -- the numbers are taken as a
         delta across the load, because the endpoint deliberately exposes no
         reset (a counter an operator can zero is a counter nobody can trust).
      4. Run the load.
      5. Read /api/cache/stats again and report the delta.

    The two things this script refuses to do, both because they would produce a
    flattering number by accident:

      - It will not report a hit rate without the distinct key count beside it.
        99% over one key is a warm loop, not a result.
      - It will not use PowerShell for the concurrency. Windows PowerShell 5.1
        has no ForEach-Object -Parallel, and Start-Job spawns a process per
        request, so the harness becomes the bottleneck and the "concurrent"
        load is not concurrent. bombardier is required.

.NOTES
    Requires: .NET 10 SDK and bombardier on PATH
    (winget install bombardier, or a GitHub release binary).

    Redis is NOT required. Stampede protection is an in-process property, so
    the measurement is valid on L1 alone. Pass -RedisConnectionString to also
    exercise L2.
#>

[CmdletBinding()]
param(
    [string]$ApiProject = "$PSScriptRoot/../../Day7/piece2/QuotesApi",
    [string]$BaseUrl = "http://localhost:5080",
    [string]$Email = 'cache-measure@example.com',
    [string]$Password = 'a-long-enough-password',

    [int]$Concurrency = 100,
    [int]$Requests = 5000,

    # How many distinct pages the load spreads over. 1 is the best case and
    # makes the prettiest number; the default is deliberately not 1.
    [int]$Pages = 5,
    [int]$PageSize = 20,

    [int]$SeedQuotes = 2000,
    [string]$RedisConnectionString = ''
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command bombardier -ErrorAction SilentlyContinue)) {
    throw "bombardier not found on PATH. Install it (winget install bombardier) and re-run. " +
          "See the .NOTES section for why PowerShell is not used for the load."
}

$env:Jwt__Secret = if ($env:Jwt__Secret) { $env:Jwt__Secret } else {
    'cache-measure-local-signing-key-not-used-anywhere-real'
}

function Start-Api {
    param([bool]$CacheEnabled)

    $env:ASPNETCORE_URLS        = $BaseUrl
    $env:ASPNETCORE_ENVIRONMENT = 'Development'

    # Every variable the app reads is assigned here, never inherited. A shell
    # left with Cache__Enabled=true from an earlier experiment would otherwise
    # make the "baseline" run cached, and the comparison would silently be
    # cache-versus-cache.
    $env:Cache__Enabled                    = if ($CacheEnabled) { 'true' } else { 'false' }
    $env:Cache__Redis__ConnectionString    = $RedisConnectionString
    $env:Outbox__RelayEnabled              = 'false'
    $env:ServiceBus__Enabled               = 'false'

    $stamp  = Get-Date -Format 'HHmmss-fff'
    $outLog = Join-Path $env:TEMP "cache-api-$stamp.out.log"
    $errLog = Join-Path $env:TEMP "cache-api-$stamp.err.log"

    # LAUNCH THE DLL, NOT `dotnet run`.
    #
    # `dotnet run` spawns the app as a CHILD process (QuotesApi.exe), so the
    # handle Start-Process returns is the launcher, not the application.
    # Stop-Process then kills the launcher and leaves the app running: a
    # QuotesApi.exe survived one of these runs holding a lock on
    # bin\Debug\net10.0\QuotesApi.exe, and the next `dotnet build` failed with
    # MSB3027 "the file is locked by: QuotesApi (9260)".
    #
    # Running the DLL directly makes the returned handle the application
    # itself, so stopping it stops the thing that was started. For a script
    # whose job is to start and kill processes deterministically, that is not
    # tidiness -- it is the difference between killing what you meant to and
    # killing its parent.
    $dll = Join-Path $ApiProject 'bin/Debug/net10.0/QuotesApi.dll'
    if (-not (Test-Path $dll)) { throw "Not built: $dll. Run dotnet build first." }

    $process = Start-Process dotnet `
        -ArgumentList @($dll) `
        -WorkingDirectory $ApiProject `
        -RedirectStandardOutput $outLog -RedirectStandardError $errLog `
        -NoNewWindow -PassThru

    $deadline = (Get-Date).AddSeconds(90)
    do {
        Start-Sleep -Seconds 2

        if ($process.HasExited) {
            Write-Host "`nThe API exited with code $($process.ExitCode). Last lines:" -ForegroundColor Red
            if (Test-Path $errLog) { Get-Content $errLog -Tail 40 | Write-Host }
            if (Test-Path $outLog) { Get-Content $outLog -Tail 40 | Write-Host }
            throw "API failed to start. Logs: $outLog / $errLog"
        }

        try { $null = Invoke-RestMethod "$BaseUrl/health" -TimeoutSec 3; return $process } catch { }
    } while ((Get-Date) -lt $deadline)

    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    throw "API did not become healthy within 90s. Logs: $outLog / $errLog"
}

function Get-Token {
    $body = @{ email = $Email; password = $Password } | ConvertTo-Json
    try {
        (Invoke-RestMethod "$BaseUrl/api/auth/register" -Method Post `
            -ContentType 'application/json' -Body $body).accessToken
    } catch {
        (Invoke-RestMethod "$BaseUrl/api/auth/login" -Method Post `
            -ContentType 'application/json' -Body $body).accessToken
    }
}

function Remove-Database {
    # BOTH RUNS MUST START FROM AN IDENTICAL DATABASE.
    #
    # /api/diagnostics/seed APPENDS -- it is AddRange + SaveChanges, and it
    # returns totalRowsNow rather than a count of what it replaced. quotes.db
    # persists between the two runs, so without this the baseline read ~2,020
    # rows and the cached run read ~4,020: the cached run would be reading a
    # table twice the size.
    #
    # The direction of that error matters. A bigger table slows the UNCACHED
    # path and barely touches the cached one, which flatters the cache. A
    # measurement whose bug points at its own conclusion is worse than no
    # measurement.
    foreach ($suffix in @('', '-wal', '-shm')) {
        $path = Join-Path $ApiProject "quotes.db$suffix"
        if (Test-Path $path) { Remove-Item $path -Force -ErrorAction SilentlyContinue }
    }
}

function Invoke-Run {
    param([string]$Label, [bool]$CacheEnabled)

    Write-Host "`n=== $Label ===" -ForegroundColor Cyan
    Remove-Database
    $api = Start-Api -CacheEnabled $CacheEnabled
    try {
        $token   = Get-Token
        $headers = @{ Authorization = "Bearer $token" }

        Write-Host "  seeding $SeedQuotes quotes..."
        # authorCount is required too -- both parameters are non-nullable ints
        # on the diagnostics endpoint, so omitting either is a 400.
        $authorCount = [math]::Max(1, [int]($SeedQuotes / 20))
        $seed = Invoke-RestMethod "$BaseUrl/api/diagnostics/seed?count=$SeedQuotes&authorCount=$authorCount" `
            -Method Post -Headers $headers -TimeoutSec 300

        # Reported, not assumed. If these two numbers differ between the runs,
        # the comparison is measuring the seed and the result is void.
        Write-Host "  rows in Quotes: $($seed.totalRowsNow)"

        $before = Invoke-RestMethod "$BaseUrl/api/cache/stats" -Headers $headers

        Write-Host "  load: $Requests requests, $Concurrency concurrent, over $Pages page(s)"
        $perPage = [math]::Max(1, [int]($Requests / $Pages))

        foreach ($page in 1..$Pages) {
            bombardier -c $Concurrency -n $perPage -l `
                -H "Authorization: Bearer $token" `
                "$BaseUrl/api/quotes?page=$page&size=$PageSize" | Write-Host
        }

        $after = Invoke-RestMethod "$BaseUrl/api/cache/stats" -Headers $headers

        [pscustomobject]@{
            Label        = $Label
            Rows         = $seed.totalRowsNow
            Requests     = $after.requests - $before.requests
            Hits         = $after.hits     - $before.hits
            Misses       = $after.misses   - $before.misses
            Bypasses     = $after.bypasses - $before.bypasses
            DistinctKeys = $after.distinctKeys
            HitRatio     = if (($after.requests - $before.requests) -gt 0) {
                               [math]::Round(($after.hits - $before.hits) / ($after.requests - $before.requests), 4)
                           } else { 0 }
            DbCommands   = [int]$after.dbCommands.'quotes.list' - [int]$before.dbCommands.'quotes.list'
        }
    }
    finally {
        Stop-Process -Id $api.Id -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }
}

Write-Output ("Day 21 measurement -- {0:u}" -f (Get-Date))
Write-Output ("  load: {0} requests, {1} concurrent, {2} page(s) of size {3}, seed {4}" -f `
    $Requests, $Concurrency, $Pages, $PageSize, $SeedQuotes)
Write-Output ("  redis: {0}" -f $(if ($RedisConnectionString) { $RedisConnectionString } else { 'none (L1 only)' }))
Write-Output ""

Write-Host "[0/3] Building" -ForegroundColor Cyan
dotnet build $ApiProject | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$baseline = Invoke-Run -Label 'Cache OFF (baseline)' -CacheEnabled $false
$cached   = Invoke-Run -Label 'Cache ON'             -CacheEnabled $true

Write-Host "`n================ RESULT ================" -ForegroundColor Green

# Out-String | Write-Output, not Out-Host. Out-Host writes to the console and
# NOT to the success stream, so `.\measure-cache.ps1 *>&1 | Tee-Object` captured
# every bombardier block and then silently dropped this table -- the one row of
# output that carries the query counts. Evidence that is missing from the
# captured file precisely because it went to the screen is the worst kind of
# missing.
@($baseline, $cached) | Format-Table -AutoSize | Out-String -Width 200 | Write-Output

if ($baseline.Rows -ne $cached.Rows) {
    Write-Host ("REFUSING TO REPORT: the two runs read different row counts ({0} vs {1}). " -f `
        $baseline.Rows, $cached.Rows) -ForegroundColor Red
    Write-Host "That comparison measures the seed, not the cache." -ForegroundColor Red
    exit 1
}

if ($baseline.DbCommands -gt 0) {
    $drop = 1 - ($cached.DbCommands / $baseline.DbCommands)

    # Both absolute numbers, always. A bare percentage hides whether the
    # baseline was 10 commands or 10,000.
    Write-Host ("DB commands (quotes.list): {0} -> {1}  ({2:P1} fewer)" -f `
        $baseline.DbCommands, $cached.DbCommands, $drop) -ForegroundColor Green
    Write-Host ("Hit rate: {0:P2} over {1} distinct key(s)" -f `
        $cached.HitRatio, $cached.DistinctKeys) -ForegroundColor Green
    Write-Host ""
    Write-Host "Read the key count next to the hit rate. A high ratio over one key" -ForegroundColor Yellow
    Write-Host "is deduplication, not evidence about a real traffic mix." -ForegroundColor Yellow
}
