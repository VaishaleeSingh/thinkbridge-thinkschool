<#
.SYNOPSIS
    Day 22 -- drives the entra-id circuit breaker open under sustained failure
    and watches it recover, recording the state at every step.

.DESCRIPTION
    The automated proof lives in
    Quotes.Tests.Unit/Resilience/CircuitBreakerLifecycleTests.cs, and that is
    the primary evidence: it runs in CI, needs no Docker, and cannot be fudged.
    This script is the other half -- the live run for the write-up, which shows
    the shape of the behaviour in a running process rather than in a test host.

    HOW THE FAILURE IS INJECTED, and why not by making Microsoft fail:
    AzureAd:Authority is pointed at a local port with nothing listening on it.
    Every request carrying an Entra-shaped token then forces the JwtBearer
    handler to fetch OIDC metadata from an address that refuses the
    connection, which is a real, sustained dependency failure that belongs
    entirely to us. Generating failure load against login.microsoftonline.com
    would be generating load against a third party, which is not something a
    resilience exercise gets to do.

    HOW THE TOKEN ROUTES TO THE ENTRA SCHEME: AuthSchemeSelector picks the
    EntraId scheme when the bearer token's "aud" claim contains "api://" (see
    that class). The token below is unsigned nonsense with that audience --
    signature validation will never be reached, because the metadata fetch it
    depends on fails first, which is exactly the failure under test.

.NOTES
    Run from the repository root with the API NOT already running.
    Requires PowerShell 7+ for the parallel burst.
#>

[CmdletBinding()]
param(
    [string]$ApiProject = "Day7/piece2/QuotesApi",
    [int]$Port = 5185,
    [string]$DeadAuthority = "http://127.0.0.1:59999/v2.0",

    # Deliberately smaller than the shipped defaults, so a walkthrough takes
    # seconds rather than a minute. The behaviour does not depend on the
    # magnitudes -- see ResilienceOptions.
    [int]$MinimumThroughput = 6,
    [string]$BreakDuration = "00:00:05",
    [string]$OutFile = "Day22/verification/day22-circuit-proof-run.txt"
)

$ErrorActionPreference = "Stop"
$baseUrl = "http://127.0.0.1:$Port"

function Write-Step {
    param([string]$Text)
    $line = "[{0:HH:mm:ss.fff}] {1}" -f (Get-Date), $Text
    Write-Host $line
    Add-Content -Path $OutFile -Value $line
}

function Get-ResilienceState {
    try {
        return Invoke-RestMethod -Uri "$baseUrl/api/diagnostics/resilience" -TimeoutSec 10
    }
    catch {
        throw "Could not read /api/diagnostics/resilience. Is the API running with Diagnostics enabled? $_"
    }
}

function Write-State {
    param([string]$Label)
    $s = Get-ResilienceState
    Write-Step ("{0,-22} circuit={1,-9} opened={2} halfOpened={3} closed={4} retries={5} suppressed={6} shed={7}" -f `
        $Label, $s.circuitState, $s.transitions.opened, $s.transitions.halfOpened, `
        $s.transitions.closed, $s.retries, $s.retriesSuppressed, $s.bulkheadRejections)
    return $s
}

# An unsigned JWT whose only meaningful claim is an api:// audience, which is
# what routes it to the EntraId scheme and therefore to the metadata fetch.
function New-EntraShapedToken {
    function ConvertTo-Base64Url([string]$Json) {
        $bytes = [Text.Encoding]::UTF8.GetBytes($Json)
        return [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    }

    $header = ConvertTo-Base64Url '{"alg":"RS256","typ":"JWT","kid":"not-a-real-key"}'
    $payload = ConvertTo-Base64Url '{"aud":"api://quotes-api/access","iss":"https://sts.windows.net/test/","sub":"day22"}'

    return "$header.$payload.not-a-real-signature"
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutFile) | Out-Null
Set-Content -Path $OutFile -Value "Day 22 -- circuit breaker proof run"

Write-Step "Policy for this run: MinimumThroughput=$MinimumThroughput BreakDuration=$BreakDuration"
Write-Step "Authority pointed at $DeadAuthority (nothing listens there -- this is the injected failure)"

$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:ASPNETCORE_URLS = $baseUrl
$env:AzureAd__Authority = $DeadAuthority
$env:Resilience__CircuitBreaker__MinimumThroughput = "$MinimumThroughput"
$env:Resilience__CircuitBreaker__BreakDuration = $BreakDuration
$env:Resilience__CircuitBreaker__SamplingDuration = "00:00:30"

# Short, so a failing attempt does not dominate the wall clock of the run.
$env:Resilience__AttemptTimeout = "00:00:02"
$env:Resilience__TotalTimeout = "00:00:06"

$api = Start-Process -FilePath "dotnet" -ArgumentList @("run", "--project", $ApiProject) -PassThru -NoNewWindow

try {
    Write-Step "Waiting for the API to answer /health ..."
    $deadline = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $deadline) {
        try {
            Invoke-RestMethod -Uri "$baseUrl/health" -TimeoutSec 3 | Out-Null
            break
        }
        catch { Start-Sleep -Milliseconds 500 }
    }

    $token = New-EntraShapedToken
    $headers = @{ Authorization = "Bearer $token" }

    # A protected endpoint, so token validation -- and therefore the metadata
    # fetch -- actually runs.
    $protectedUrl = "$baseUrl/api/quotes"

    Write-State "before any load"

    Write-Step "Driving $($MinimumThroughput * 2) authenticated requests against the dead authority ..."
    $latencies = @()
    for ($i = 1; $i -le ($MinimumThroughput * 2); $i++) {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        try { Invoke-WebRequest -Uri $protectedUrl -Headers $headers -TimeoutSec 30 -SkipHttpErrorCheck | Out-Null }
        catch { }
        $sw.Stop()
        $latencies += $sw.ElapsedMilliseconds
        Write-Step ("  request {0,2}  {1,6} ms" -f $i, $sw.ElapsedMilliseconds)
    }

    $opened = Write-State "after sustained failure"

    if ($opened.circuitState -ne "Open") {
        Write-Step "!! The circuit is not open. Either the failure did not reach the pipeline, or"
        Write-Step "!! MinimumThroughput was not met inside SamplingDuration. Nothing below is meaningful."
    }

    # THE LATENCY CLAIM. Before the circuit opens, each request pays the attempt
    # timeout. After, it pays a rejection. The two numbers side by side are the
    # point of a circuit breaker, and they are the reason to record latency per
    # request rather than an average over the run.
    $sw = [Diagnostics.Stopwatch]::StartNew()
    try { Invoke-WebRequest -Uri $protectedUrl -Headers $headers -TimeoutSec 30 -SkipHttpErrorCheck | Out-Null } catch { }
    $sw.Stop()
    Write-Step ("open-circuit request cost {0} ms (first request of the run cost {1} ms)" -f $sw.ElapsedMilliseconds, $latencies[0])

    Write-Step "Waiting past BreakDuration ($BreakDuration), dependency still dead ..."
    Start-Sleep -Seconds ([TimeSpan]::Parse($BreakDuration).TotalSeconds + 1)

    Write-Step "Bursting 8 concurrent requests -- half-open must admit exactly one ..."
    1..8 | ForEach-Object -Parallel {
        try { Invoke-WebRequest -Uri $using:protectedUrl -Headers $using:headers -TimeoutSec 30 -SkipHttpErrorCheck | Out-Null }
        catch { }
    } -ThrottleLimit 8

    Write-State "after half-open burst"

    # Recovery. A breaker that opens and never closes has removed the
    # dependency from the system permanently, which is not resilience.
    Write-Step "Repairing the dependency: pointing the authority at a real metadata document is out of"
    Write-Step "scope for an unattended script, so recovery is demonstrated with the manual control"
    Write-Step "instead -- and that is a WEAKER claim, stated as such. The automated proof of recovery"
    Write-Step "under a genuinely healed dependency is CircuitBreakerLifecycleTests."
    Invoke-RestMethod -Uri "$baseUrl/api/diagnostics/resilience/close" -Method Post -TimeoutSec 10 | Out-Null

    Write-State "after manual close"

    Write-Step "Raw run written to $OutFile"
}
finally {
    if ($api -and -not $api.HasExited) {
        Write-Step "Stopping the API (pid $($api.Id))"
        Stop-Process -Id $api.Id -Force
    }
}
