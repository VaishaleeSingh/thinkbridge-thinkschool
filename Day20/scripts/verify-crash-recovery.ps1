<#
.SYNOPSIS
    Proves an outbox row survives a hard kill of the API and is published
    after the restart.

.DESCRIPTION
    The automated tests simulate a crash by throwing. A thrown exception is
    not a process death, and the submission should not pretend it is: an
    exception unwinds through finally blocks, keeps the CLR alive, and lets
    an in-memory retry finish the job. This script uses Stop-Process -Force,
    which is a real SIGKILL equivalent -- no graceful shutdown, no flush, no
    lease release.

    The sequence, and why each step is there:

      1. Start the API with a LONG poll interval, so there is a wide window
         in which the row is provably committed and provably not published.
      2. POST a quote. The caller gets 201.
      3. Read /api/outbox/status and confirm one PENDING row.
         THIS STEP CARRIES THE WHOLE ARGUMENT. Without it, step 6 cannot be
         told apart from a publish that simply happened on time.
      4. Kill the process.
      5. Restart it, with a short poll interval. Take no other action --
         no replay command, no manual fix.
      6. Confirm nothing remains pending and the row is Sent.

    Step 6 checks the OUTBOX only, via /api/outbox/status. It does not read
    QuoteAuditEntries or QuoteSearchProjections, because with
    ServiceBus:Enabled false there is no consumer running to write them. Add
    -WithServiceBus and an emulator to see the consumer side, and the
    exactly-once side effect, end to end.

    Run the same sequence on main to see the event lost. That before/after
    is what makes the claim checkable rather than narrated.

.NOTES
    Requires the .NET 10 SDK. Service Bus is optional: with ServiceBus:Enabled
    false the relay publishes through the no-op publisher, and the outbox
    state transitions -- which are what this script measures -- are identical.

    The script mints its own token. It has to: a token can only come from a
    running API, and this script starts and kills the only API involved, so
    demanding one as a parameter would be asking the caller to solve a
    chicken-and-egg problem the script created.
#>

[CmdletBinding()]
param(
    [string]$ApiProject = "$PSScriptRoot/../../Day7/piece2/QuotesApi",
    [string]$BaseUrl = "http://localhost:5080",

    # Reused across runs on purpose: quotes.db persists, so the second run
    # would get a 400 from the unique index on Email. Register-then-login
    # below handles both the first run and every one after it.
    [string]$Email = 'outbox-crash-proof@example.com',
    [string]$Password = 'a-long-enough-password',

    # Overrides the register/login flow if you already hold a token.
    [string]$Token,

    # Off by default, and that is not a shortcut. What this script measures is
    # the OUTBOX state machine -- committed, pending, published, marked -- and
    # every one of those transitions is identical whether the relay publishes
    # to a real broker or to the no-op publisher. Requiring an emulator to run
    # the crash proof would make the proof harder to run without making it
    # prove more. Pass -WithServiceBus once a namespace or the emulator is up
    # to see the messages actually land on the topic.
    [switch]$WithServiceBus,

    [int]$LongPollSeconds = 120
)

$ErrorActionPreference = 'Stop'

$env:Jwt__Secret = if ($env:Jwt__Secret) { $env:Jwt__Secret } else {
    'crash-proof-local-signing-key-not-used-anywhere-real'
}

function Get-Token {
    $body = @{ email = $Email; password = $Password } | ConvertTo-Json

    # Register first, fall back to login. A 400 here is the expected result of
    # a second run, not a failure -- the account already exists.
    try {
        $response = Invoke-RestMethod "$BaseUrl/api/auth/register" -Method Post `
            -ContentType 'application/json' -Body $body
    } catch {
        $response = Invoke-RestMethod "$BaseUrl/api/auth/login" -Method Post `
            -ContentType 'application/json' -Body $body
    }

    if (-not $response.accessToken) {
        throw "Could not obtain an access token for $Email."
    }

    return $response.accessToken
}

$headers = @{}

function Start-Api {
    param([int]$PollSeconds)

    $env:ASPNETCORE_URLS        = $BaseUrl
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:Outbox__RelayEnabled   = 'true'
    $env:Outbox__PollInterval   = ([TimeSpan]::FromSeconds($PollSeconds)).ToString('hh\:mm\:ss')

    # Set EXPLICITLY, never inherited. A shell that still has
    # ServiceBus__Enabled=true from an earlier experiment -- with no namespace
    # configured -- makes the host die on startup building a ServiceBusClient
    # for an empty namespace, and this script would report it as "the API did
    # not become healthy". Every variable the app reads and this script cares
    # about is assigned here, so a run does not depend on the state of the
    # terminal it happens to be launched from.
    $env:ServiceBus__Enabled = if ($WithServiceBus) { 'true' } else { 'false' }

    # Output goes to files, not to a window that vanishes with the process.
    # The first version of this script used a bare Start-Process, so a host
    # that failed to start produced nothing but this function's own timeout --
    # a diagnostic that names the symptom and hides the cause.
    $stamp  = Get-Date -Format 'HHmmss-fff'
    $outLog = Join-Path $env:TEMP "outbox-api-$stamp.out.log"
    $errLog = Join-Path $env:TEMP "outbox-api-$stamp.err.log"

    $process = Start-Process dotnet `
        -ArgumentList @('run', '--project', $ApiProject, '--no-launch-profile', '--no-build') `
        -WorkingDirectory $ApiProject `
        -RedirectStandardOutput $outLog `
        -RedirectStandardError $errLog `
        -NoNewWindow -PassThru

    # Poll readiness rather than sleeping a guessed number of seconds: a fixed
    # sleep is the usual reason a script like this is flaky on a cold machine
    # and fine on a warm one.
    $deadline = (Get-Date).AddSeconds(90)
    do {
        Start-Sleep -Seconds 2

        # Check this BEFORE the health probe. A host that crashed on startup
        # will never answer, and waiting out the full 90 seconds to discover
        # that wastes a minute and a half and still says nothing useful.
        if ($process.HasExited) {
            Write-Host "`nThe API process exited with code $($process.ExitCode). Last lines of its output:" -ForegroundColor Red
            if (Test-Path $errLog) { Get-Content $errLog -Tail 40 | Write-Host }
            if (Test-Path $outLog) { Get-Content $outLog -Tail 40 | Write-Host }
            throw "The API failed to start. Logs: $outLog / $errLog"
        }

        try {
            $null = Invoke-RestMethod "$BaseUrl/health" -TimeoutSec 3
            $script:ApiLog = $outLog
            return $process
        } catch { }
    } while ((Get-Date) -lt $deadline)

    Write-Host "`nThe API started but never answered /health. Last lines of its output:" -ForegroundColor Red
    if (Test-Path $outLog) { Get-Content $outLog -Tail 40 | Write-Host }
    if (Test-Path $errLog) { Get-Content $errLog -Tail 40 | Write-Host }
    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    throw "API did not become healthy at $BaseUrl within 90s. Logs: $outLog / $errLog"
}

function Get-OutboxStatus {
    Invoke-RestMethod "$BaseUrl/api/outbox/status" -Headers $headers
}

# Resolved to an absolute path: it is passed to Start-Process as both the
# project argument and the working directory, and a relative one would be
# interpreted against whatever directory the caller happened to be in.
$ApiProject = (Resolve-Path $ApiProject).Path

# Built here, once, with its output on screen. Start-Api then runs --no-build,
# so a compile error surfaces as a compile error rather than as a host that
# quietly never becomes healthy.
Write-Host "`n[0/6] Building" -ForegroundColor Cyan
dotnet build $ApiProject | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Build failed. Fix the compile errors and re-run." }

Write-Host "[1/6] Starting the API with a $LongPollSeconds s poll interval" -ForegroundColor Cyan
$api = Start-Api -PollSeconds $LongPollSeconds

if (-not $Token) { $Token = Get-Token }
$headers = @{ Authorization = "Bearer $Token" }
Write-Host "      authenticated as $Email"

Write-Host "[2/6] Creating a quote" -ForegroundColor Cyan
$created = Invoke-RestMethod "$BaseUrl/api/quotes" -Method Post -Headers $headers `
    -ContentType 'application/json' `
    -Body (@{ author = 'Crash Test'; text = "Committed at $(Get-Date -Format o)" } | ConvertTo-Json)

Write-Host "      quote id $($created.id) created (HTTP 201)"

Write-Host "[3/6] Confirming the event is committed and NOT published" -ForegroundColor Cyan
$before = Get-OutboxStatus
$before | ConvertTo-Json -Depth 5 | Write-Host

if (-not $before.counts.Pending -or $before.counts.Pending -lt 1) {
    throw "Expected at least one Pending outbox row before the kill. " +
          "With none, the rest of this run proves nothing -- raise -LongPollSeconds."
}

Write-Host "[4/6] Killing the process (no graceful shutdown)" -ForegroundColor Yellow
Stop-Process -Id $api.Id -Force
Start-Sleep -Seconds 2

Write-Host "[5/6] Restarting, with a 2 s poll interval. No other action." -ForegroundColor Cyan
$api = Start-Api -PollSeconds 2

# Re-mint: access tokens live 15 minutes, and a run that waited out a long
# poll interval could otherwise fail on an expired token and look like a
# recovery failure.
$headers = @{ Authorization = "Bearer $(Get-Token)" }

Write-Host "[6/6] Waiting for the relay to drain what the dead process left behind" -ForegroundColor Cyan
$deadline = (Get-Date).AddSeconds(60)
do {
    Start-Sleep -Seconds 3
    $after = Get-OutboxStatus
    $pending = if ($after.counts.Pending) { $after.counts.Pending } else { 0 }
} while ($pending -gt 0 -and (Get-Date) -lt $deadline)

$after | ConvertTo-Json -Depth 5 | Write-Host

Stop-Process -Id $api.Id -Force

if ($pending -gt 0) {
    Write-Host "`nFAILED: $pending row(s) still pending after the restart." -ForegroundColor Red
    exit 1
}

Write-Host "`nPASSED: the event committed before the kill was published after the restart," -ForegroundColor Green
Write-Host "        with no manual intervention." -ForegroundColor Green
