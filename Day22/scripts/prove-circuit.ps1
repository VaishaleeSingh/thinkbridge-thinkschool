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

    HOW THE FAILURE IS INJECTED, and why not through Entra ID:
    /api/diagnostics/resilience/probe issues one GET through the SAME named
    client and pipeline the Entra ID backchannel uses, aimed at a local port
    nothing listens on. The failure is a connection refusal -- fast,
    unambiguous, entirely local, and no load is generated against anyone
    else's service.

    The first two attempts at this drove Entra ID itself, by pointing
    AzureAd:Authority at a dead address. Both failed for reasons that belong
    to the caller rather than the pipeline:

      1. JwtBearer refuses to INITIALIZE with an http:// authority and throws
         from PostConfigure, before any network call. Every request failed in
         3ms having touched nothing.
      2. With https://, ConfigurationManager caches a FAILED metadata
         retrieval for its refresh interval -- so a burst of 12 requests makes
         one HTTP attempt, not 12, and the breaker never sees enough failures.
         The run would have measured ConfigurationManager's caching.

    The probe removes the caller from the experiment. The breaker instance is
    shared, so a circuit opened through the probe IS the circuit that protects
    token validation.

.NOTES
    WINDOWS POWERSHELL 5.1 COMPATIBLE, deliberately. The first draft used
    ForEach-Object -Parallel and Invoke-WebRequest -SkipHttpErrorCheck, both of
    which are PowerShell 7 only. Requiring a shell install to run a
    verification script is a barrier between the reader and the evidence, so
    the concurrency is done with System.Net.Http and Task.WaitAll instead.

    That is also the more honest harness, for the reason Day 21 wrote down:
    Start-Job spawns a process per request, so the harness becomes the
    bottleneck and the "concurrent" burst is not concurrent. One HttpClient
    with N in-flight GetAsync calls is genuinely N requests at once.

    Run from the repository root with the API NOT already running.
#>

[CmdletBinding()]
param(
    [string]$ApiProject = "Day7/piece2/QuotesApi",
    [int]$Port = 5185,
    [string]$DeadTarget = "http://127.0.0.1:59999/probe",

    # BOTH INJECTION POINTS ARE KEPT. "Probe" drives the pipeline directly and
    # is the default because it is deterministic. "Entra" drives it the way
    # production does -- through the JwtBearer backchannel -- which is the more
    # faithful demonstration when it works, and is kept for exactly that
    # reason. See the caveats in .DESCRIPTION: Entra mode needs an https
    # authority, and ConfigurationManager may cache a failed fetch, so the
    # attempt count it produces is not guaranteed to reach MinimumThroughput.
    [ValidateSet("Probe", "Entra")]
    [string]$Mode = "Probe",

    # Entra mode only. HTTPS is not optional here: JwtBearer refuses to
    # initialize with an http:// authority unless RequireHttpsMetadata is
    # false, and this script does not change the app's security posture to
    # make a demo work. Nothing listens on the port either way.
    [string]$DeadAuthority = "https://127.0.0.1:59999/v2.0",

    # Deliberately smaller than the shipped defaults, so a walkthrough takes
    # seconds rather than a minute. The behaviour does not depend on the
    # magnitudes -- see ResilienceOptions.
    [int]$MinimumThroughput = 6,
    [string]$BreakDuration = "00:00:05",
    [string]$OutFile = ""
)

$ErrorActionPreference = "Stop"
$baseUrl = "http://127.0.0.1:$Port"

# One file per mode. The first version wrote a single fixed filename, so
# running both modes left only the second run's evidence on disk -- the
# verification folder silently held less than the session had produced.
if ([string]::IsNullOrWhiteSpace($OutFile)) {
    $OutFile = "Day22/verification/day22-circuit-proof-run-$Mode.txt"
}

Add-Type -AssemblyName System.Net.Http

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
        throw "Could not read /api/diagnostics/resilience. Is the API running in Development? $_"
    }
}

function Write-State {
    param([string]$Label)
    $s = Get-ResilienceState
    Write-Step ("{0,-24} circuit={1,-9} opened={2} halfOpened={3} closed={4} retries={5} suppressed={6} shed={7}" -f `
        $Label, $s.circuitState, $s.transitions.opened, $s.transitions.halfOpened, `
        $s.transitions.closed, $s.retries, $s.retriesSuppressed, $s.bulkheadRejections)
    return $s
}


# Entra mode only. An unsigned JWT whose only meaningful claim is an api://
# audience, which is what routes it to the EntraId scheme and therefore to the
# metadata fetch. Signature validation is never reached, because the metadata
# fetch it depends on fails first -- which is the failure under test.
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

Write-Step "PowerShell $($PSVersionTable.PSVersion)"
Write-Step "Policy for this run: MinimumThroughput=$MinimumThroughput BreakDuration=$BreakDuration"
Write-Step "Mode: $Mode"
if ($Mode -eq "Probe") {
    Write-Step "Probe target: $DeadTarget (nothing listens there -- this is the injected failure)"
}
else {
    Write-Step "Authority: $DeadAuthority (nothing listens there -- this is the injected failure)"
}

$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:ASPNETCORE_URLS = $baseUrl
if ($Mode -eq "Entra") {
    # Scoped to this process only: Start-Process inherits it, and nothing is
    # written to appsettings.json or to any deployment configuration.
    $env:AzureAd__Authority = $DeadAuthority
}

$env:Resilience__CircuitBreaker__MinimumThroughput = "$MinimumThroughput"
$env:Resilience__CircuitBreaker__BreakDuration = $BreakDuration
$env:Resilience__CircuitBreaker__SamplingDuration = "00:00:30"

# Short, so a failing attempt does not dominate the wall clock of the run.
$env:Resilience__AttemptTimeout = "00:00:02"
$env:Resilience__TotalTimeout = "00:00:06"

# ONE RETRY, NO BACKOFF, so the failure count is legible.
#
# The breaker sits inside the retry, so it counts ATTEMPTS, not requests.
# Left at the defaults, each probe request would contribute four failures
# after three jittered backoff delays -- the circuit would still open, but
# neither the request count nor the wall clock would mean anything. Polly
# rejects MaxRetryAttempts = 0, so one is the floor.
$env:Resilience__Retry__MaxAttempts = "1"
$env:Resilience__Retry__BaseDelay = "00:00:00"

# --no-launch-profile IS LOAD-BEARING, and its absence cost the first run.
#
# dotnet run applies Properties/launchSettings.json by default, and this
# project's "http" profile sets applicationUrl to http://localhost:5059. That
# becomes the binding and OVERRIDES the ASPNETCORE_URLS exported above -- so
# the API came up healthy on 5059 while this script polled 5185 until it timed
# out. The env var looks authoritative and is not.
#
# --urls is passed as well, belt and braces, because it is the argument that
# cannot be silently overridden by a file.
#
# The profile also sets OpenTelemetry__OtlpEndpoint to localhost:4317 and
# launchBrowser: true, neither of which an unattended verification run wants.
$apiArgs = @(
    "run",
    "--project", $ApiProject,
    "--no-launch-profile",
    "--urls", $baseUrl
)

# THE API'S OWN OUTPUT IS CAPTURED, because without it a startup crash and a
# wrong port are indistinguishable: both are 120 seconds of silence followed
# by the same timeout. The log is read back and appended to the run file on
# failure, so the reason is in the evidence rather than in a console nobody
# kept.
$apiLog = "Day22/verification/day22-api-stdout.log"
$apiErrLog = "Day22/verification/day22-api-stderr.log"

$api = Start-Process -FilePath "dotnet" -ArgumentList $apiArgs -PassThru -NoNewWindow `
    -RedirectStandardOutput $apiLog -RedirectStandardError $apiErrLog

$httpClient = $null

try {
    Write-Step "Waiting for the API to answer /health ..."
    $ready = $false
    $deadline = (Get-Date).AddSeconds(120)
    while ((Get-Date) -lt $deadline) {
        try {
            Invoke-RestMethod -Uri "$baseUrl/health" -TimeoutSec 3 | Out-Null
            $ready = $true
            break
        }
        catch { Start-Sleep -Milliseconds 500 }
    }

    if (-not $ready) {
        Write-Step "!! The API never answered /health on $baseUrl within 120s."
        Write-Step "!! Its exit state: HasExited=$($api.HasExited)"
        Write-Step "!! Last 40 lines of its output follow. Look for a bound URL that is not $baseUrl,"
        Write-Step "!! a port conflict, or an options-validation failure at startup."

        foreach ($log in @($apiLog, $apiErrLog)) {
            if (Test-Path $log) {
                Write-Step "---- $log ----"
                Get-Content $log -Tail 40 | ForEach-Object { Add-Content -Path $OutFile -Value "     $_" }
            }
        }

        throw "The API never answered /health on $baseUrl. See $OutFile for its output."
    }

    Write-Step "API is up on $baseUrl"

    $probeUrl = "$baseUrl/api/diagnostics/resilience/probe?url=" + [uri]::EscapeDataString($DeadTarget)
    $protectedUrl = "$baseUrl/api/quotes"

    $httpClient = New-Object System.Net.Http.HttpClient
    $httpClient.Timeout = [TimeSpan]::FromSeconds(30)

    if ($Mode -eq "Entra") {
        $httpClient.DefaultRequestHeaders.Authorization =
            New-Object System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", (New-EntraShapedToken))
    }

    # The URL a single attempt hits, and how its outcome is read, are the only
    # things that differ between the modes. Everything below -- the load, the
    # state readings, the half-open burst -- is shared, so the two modes are
    # genuinely the same experiment against two injection points rather than
    # two scripts pretending to be one.
    $targetUrl = if ($Mode -eq "Probe") { $probeUrl } else { $protectedUrl }

    function Invoke-Attempt {
        param([string]$Url, [string]$RunMode)

        if ($RunMode -eq "Probe") {
            # The probe reports the pipeline's own verdict -- "circuit-open",
            # "bulkhead-rejected", or the exception type -- which is why this
            # mode can assert on half-open admission counts and Entra mode
            # cannot.
            $r = Invoke-RestMethod -Uri $Url -TimeoutSec 30
            return [pscustomobject]@{ Outcome = $r.outcome; ElapsedMs = $r.elapsedMs }
        }

        $sw = [Diagnostics.Stopwatch]::StartNew()
        try {
            $response = $httpClient.GetAsync($Url).GetAwaiter().GetResult()
            $status = [int]$response.StatusCode
            $response.Dispose()
            $sw.Stop()
            return [pscustomobject]@{ Outcome = "http $status"; ElapsedMs = $sw.ElapsedMilliseconds }
        }
        catch {
            $sw.Stop()
            return [pscustomobject]@{ Outcome = $_.Exception.GetType().Name; ElapsedMs = $sw.ElapsedMilliseconds }
        }
    }

    Write-State "before any load" | Out-Null

    $total = $MinimumThroughput * 2
    Write-Step "Driving $total requests ($Mode mode) ..."

    $firstElapsed = $null
    for ($i = 1; $i -le $total; $i++) {
        try {
            $a = Invoke-Attempt -Url $targetUrl -RunMode $Mode
            if ($null -eq $firstElapsed) { $firstElapsed = $a.ElapsedMs }
            Write-Step ("  request {0,2}  {1,-24} {2,6} ms" -f $i, $a.Outcome, $a.ElapsedMs)
        }
        catch {
            Write-Step ("  request {0,2}  HARNESS ERROR: {1}" -f $i, $_.Exception.Message)
        }
    }

    $opened = Write-State "after sustained failure"

    if ($opened.circuitState -ne "Open") {
        Write-Step "!! The circuit is not open. Nothing below is meaningful."
        if ($Mode -eq "Entra") {
            Write-Step "!! In Entra mode this is an EXPECTED possibility, not necessarily a bug:"
            Write-Step "!! ConfigurationManager caches a failed metadata fetch, so N requests can"
            Write-Step "!! produce far fewer than N HTTP attempts. Re-run with -Mode Probe, which"
            Write-Step "!! drives the same pipeline without that caching in the way."
        }
        else {
            Write-Step "!! Check the request outcomes above: a connection failure is expected. If"
            Write-Step "!! something answered on $DeadTarget, the failure was never injected."
        }
    }

    # THE LATENCY CLAIM. Before the circuit opens, an attempt pays the
    # connection failure or the attempt timeout. After, it pays a rejection.
    # The two numbers side by side are the point of a circuit breaker, which is
    # why latency is recorded per request rather than averaged over the run.
    $afterOpen = Invoke-Attempt -Url $targetUrl -RunMode $Mode
    Write-Step ("open-circuit request: {0} in {1} ms (the first request of the run cost {2} ms)" -f `
        $afterOpen.Outcome, $afterOpen.ElapsedMs, $firstElapsed)

    $breakSeconds = [TimeSpan]::Parse($BreakDuration).TotalSeconds
    Write-Step "Waiting past BreakDuration ($BreakDuration), dependency still dead ..."
    Start-Sleep -Seconds ($breakSeconds + 1)

    # Genuinely concurrent: eight requests in flight at once, so half-open has
    # a real herd to admit exactly one of.
    Write-Step "Bursting 8 concurrent requests -- half-open must admit exactly one ..."
    $tasks = New-Object System.Collections.ArrayList
    for ($i = 0; $i -lt 8; $i++) {
        [void]$tasks.Add($httpClient.GetStringAsync($targetUrl))
    }
    try {
        [System.Threading.Tasks.Task]::WaitAll([System.Threading.Tasks.Task[]]$tasks.ToArray())
    } catch { }

    if ($Mode -eq "Probe") {
        # HOW AN ADMITTED TRIAL IS IDENTIFIED, and why not by exception type.
        #
        # The first two runs of this reported "0 admitted, 8 rejected" while
        # halfOpened and opened both incremented -- which cannot both be true.
        # Printing every response body settled it: one request took 2057ms
        # (exactly one attempt timeout) and the other seven took 11-45ms, yet
        # ALL EIGHT reported outcome "circuit-open".
        #
        # The reason is worth knowing: Polly reports a FAILED HALF-OPEN TRIAL
        # to its caller as BrokenCircuitException -- the same exception a
        # rejected call receives. The trial ran, hit the dead dependency, timed
        # out, re-opened the breaker, and the caller was then told the circuit
        # is broken. So the exception type genuinely cannot distinguish "you
        # were rejected" from "you were the trial and it failed". Only the
        # elapsed time can.
        #
        # This is also why CircuitBreakerLifecycleTests asserts on the STUB
        # HANDLER'S invocation count rather than on what the caller saw: a
        # count of real calls measures the dependency, while the exception type
        # measures only what Polly chose to tell us. The test was right and
        # this script was wrong, which is the correct way round.
        #
        # The threshold is half the attempt timeout: a rejected call returns in
        # milliseconds, an admitted one pays the timeout, and there is nothing
        # in between to be ambiguous about.
        $attemptTimeoutMs = [TimeSpan]::Parse($env:Resilience__AttemptTimeout).TotalMilliseconds
        $admittedThresholdMs = $attemptTimeoutMs / 2

        $rejected = 0
        $admitted = 0
        $faulted = 0
        $index = 0

        foreach ($t in $tasks) {
            $index++

            if ($t.Status -ne "RanToCompletion") {
                $faulted++
                Write-Step ("  burst {0}  TASK {1}: {2}" -f $index, $t.Status,
                    $(if ($t.Exception) { $t.Exception.GetBaseException().Message } else { "no exception" }))
                continue
            }

            $r = $t.Result | ConvertFrom-Json
            $wasAdmitted = ($r.elapsedMs -ge $admittedThresholdMs)

            if ($wasAdmitted) { $admitted++ } else { $rejected++ }

            Write-Step ("  burst {0}  {1,-22} {2,6} ms  {3}" -f `
                $index, $r.outcome, $r.elapsedMs,
                $(if ($wasAdmitted) { "ADMITTED (paid the attempt timeout)" } else { "rejected" }))
        }

        Write-Step ("half-open burst: {0} admitted, {1} rejected, {2} faulted (threshold {3} ms)" -f `
            $admitted, $rejected, $faulted, $admittedThresholdMs)

        if ($admitted -ne 1) {
            Write-Step "!! Expected exactly 1 admitted. Cross-check against the halfOpened counter below."
        }
    }
    else {
        # Entra mode cannot count this. The pipeline's verdict is swallowed by
        # the authentication handler and every request surfaces as the same
        # HTTP status whether it was admitted or rejected, so an admission
        # count here would be invented. Stated rather than approximated -- the
        # transition counters below are the evidence this mode can offer, and
        # CircuitBreakerLifecycleTests is where the one-trial claim is proven.
        Write-Step "half-open burst: 8 requests sent. Admission counts are not observable in Entra"
        Write-Step "mode -- the handler hides the pipeline's verdict. See the halfOpened counter below."
    }

    Write-State "after half-open burst" | Out-Null

    # RECOVERY, with the dependency genuinely repaired.
    #
    # The first version of this closed the circuit through
    # CircuitBreakerManualControl and said in its own output that this was a
    # weaker claim. It was: isolating or closing a breaker by hand
    # demonstrates the manual control, not that a HEALED dependency causes the
    # circuit to close. The exercise asks for half-opening to recovery, so the
    # weaker version is not good enough.
    #
    # So the dependency is actually repaired: a TCP listener is started on the
    # very port that was refusing connections, answering 200 to anything. A
    # raw TcpListener rather than HttpListener because HttpListener needs a URL
    # reservation (admin) for a 127.0.0.1 prefix, and this script should not
    # need elevation. It runs in a background job because the accept loop has
    # to be listening WHILE this script makes its request.
    #
    # The repair happens BEFORE the trial request, which is the ordering that
    # matters: the breaker has no way to know the dependency recovered and must
    # find out by letting one request through.
    $repairPort = ([uri]$DeadTarget).Port
    Write-Step "Repairing the dependency: starting a listener on port $repairPort ..."

    $listenerJob = Start-Job -ScriptBlock {
        param($port)
        $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $port)
        $listener.Start()
        $deadline = (Get-Date).AddSeconds(90)
        $response = [Text.Encoding]::ASCII.GetBytes(
            "HTTP/1.1 200 OK`r`nContent-Length: 2`r`nConnection: close`r`n`r`nok")
        while ((Get-Date) -lt $deadline) {
            if ($listener.Pending()) {
                $client = $listener.AcceptTcpClient()
                $stream = $client.GetStream()
                $buffer = New-Object byte[] 4096
                try {
                    $null = $stream.Read($buffer, 0, $buffer.Length)
                    $stream.Write($response, 0, $response.Length)
                    $stream.Flush()
                } catch { }
                $client.Close()
            }
            else { Start-Sleep -Milliseconds 25 }
        }
        $listener.Stop()
    } -ArgumentList $repairPort

    try {
        Start-Sleep -Milliseconds 750

        # Confirm the repair took, so a failed recovery cannot be misread as a
        # breaker that will not close.
        $probeDirect = $null
        try {
            $probeDirect = (Invoke-WebRequest -Uri $DeadTarget -TimeoutSec 5 -UseBasicParsing).StatusCode
        } catch { }
        Write-Step "Listener check: direct GET $DeadTarget -> $(if ($probeDirect) { $probeDirect } else { 'no answer' })"

        Write-Step "Waiting past BreakDuration ($BreakDuration) with the dependency HEALTHY ..."
        Start-Sleep -Seconds ($breakSeconds + 1)

        $trial = Invoke-Attempt -Url $targetUrl -RunMode $Mode
        Write-Step ("half-open trial against a healthy dependency: {0} in {1} ms" -f $trial.Outcome, $trial.ElapsedMs)

        $recovered = Write-State "after recovery"

        if ($recovered.circuitState -eq "Closed") {
            Write-Step "RECOVERED: the circuit closed because one trial request succeeded."
        }
        else {
            Write-Step "!! The circuit did not close. Check the listener check above: if the direct GET"
            Write-Step "!! did not answer 200, the dependency was never actually repaired."
        }

        # And traffic flows normally afterwards, rather than the circuit
        # reporting Closed while still rejecting.
        $after1 = Invoke-Attempt -Url $targetUrl -RunMode $Mode
        $after2 = Invoke-Attempt -Url $targetUrl -RunMode $Mode
        Write-Step ("post-recovery traffic: {0} ({1} ms), {2} ({3} ms)" -f `
            $after1.Outcome, $after1.ElapsedMs, $after2.Outcome, $after2.ElapsedMs)

        Write-State "after post-recovery traffic" | Out-Null
    }
    finally {
        if ($listenerJob) {
            Stop-Job $listenerJob -ErrorAction SilentlyContinue
            Remove-Job $listenerJob -Force -ErrorAction SilentlyContinue
        }
    }

    Write-Step "Raw run written to $OutFile"
}
finally {
    # EVERY ENVIRONMENT VARIABLE THIS SCRIPT SET IS UNSET AGAIN.
    #
    # Run as `powershell -File ...` these die with the child process, so this
    # is belt and braces -- but the failure it prevents is nasty and silent: a
    # leftover AzureAd__Authority pointing at a dead port breaks local
    # authentication with no obvious cause, and a leftover
    # Resilience__AttemptTimeout that is not smaller than the total timeout
    # now stops the app BOOTING, because ValidateOnStart rejects it. Both look
    # like "my local broke" and neither points at this script.
    #
    # Removing them (null) rather than setting them back to a default is the
    # right restore: environment variables sit above appsettings.json in
    # configuration precedence, so only their absence hands the decision back
    # to the app's own configuration.
    foreach ($key in @(
        "ASPNETCORE_ENVIRONMENT",
        "ASPNETCORE_URLS",
        "AzureAd__Authority",
        "Resilience__TotalTimeout",
        "Resilience__AttemptTimeout",
        "Resilience__Retry__MaxAttempts",
        "Resilience__Retry__BaseDelay",
        "Resilience__CircuitBreaker__MinimumThroughput",
        "Resilience__CircuitBreaker__BreakDuration",
        "Resilience__CircuitBreaker__SamplingDuration"
    )) {
        [Environment]::SetEnvironmentVariable($key, $null)
    }

    if ($httpClient) { $httpClient.Dispose() }

    if ($api -and -not $api.HasExited) {
        Write-Step "Stopping the API (pid $($api.Id))"
        Stop-Process -Id $api.Id -Force
    }
}
