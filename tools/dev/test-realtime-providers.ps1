param(
    [int]$Iterations = 5,
    [int]$DelayMilliseconds = 500,
    [int]$TimeoutSeconds = 8,
    [string[]]$Symbols = @("sh600000", "sz000001", "sz300059", "sh601318", "sz002415", "sz002230"),
    [ValidateSet("Tencent", "EastMoney", "Both")]
    [string]$Provider = "Tencent",
    [string]$OutputDir = ".run"
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
[Net.ServicePointManager]::SecurityProtocol =
    [Net.SecurityProtocolType]::Tls12 -bor
    [Net.SecurityProtocolType]::Tls13
Add-Type -AssemblyName System.Net.Http

$Headers = @{
    "User-Agent" = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 AShareRadar/0.1"
    "Accept" = "*/*"
}

function Convert-ToEastMoneySecId {
    param([string]$Symbol)

    if ($Symbol.StartsWith("sh", [System.StringComparison]::OrdinalIgnoreCase)) {
        return "1." + $Symbol.Substring(2)
    }

    if ($Symbol.StartsWith("sz", [System.StringComparison]::OrdinalIgnoreCase)) {
        return "0." + $Symbol.Substring(2)
    }

    if ($Symbol.Length -eq 6) {
        if ($Symbol.StartsWith("6")) {
            return "1." + $Symbol
        }

        return "0." + $Symbol
    }

    return $null
}

function Read-Decimal {
    param($Value, [decimal]$Scale = 1)

    if ($null -eq $Value) {
        return 0
    }

    $text = [string]$Value
    $number = 0.0
    if ([double]::TryParse($text, [System.Globalization.NumberStyles]::Any, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$number)) {
        return [decimal]$number / $Scale
    }

    return 0
}

function Test-EastMoney {
    param([string[]]$Symbols, [int]$TimeoutSeconds)

    $quotes = @()
    foreach ($symbol in $Symbols) {
        $secId = Convert-ToEastMoneySecId $symbol
        if (-not $secId) {
            continue
        }

        $url = "https://push2.eastmoney.com/api/qt/stock/get?secid=$secId&fields=f43,f57,f58,f170,f168,f116"
        $response = Invoke-RestMethod -Uri $url -TimeoutSec $TimeoutSeconds -Headers $Headers
        if ($response.data) {
            $quotes += [pscustomobject]@{
                Symbol = $response.data.f57
                Name = $response.data.f58
                Price = Read-Decimal $response.data.f43 100
                ChangePercent = Read-Decimal $response.data.f170 100
                TurnoverRate = Read-Decimal $response.data.f168 100
                Amount = Read-Decimal $response.data.f116 1
            }
        }
    }

    return $quotes
}

function Test-Tencent {
    param([string[]]$Symbols, [int]$TimeoutSeconds)

    $url = "https://qt.gtimg.cn/q=" + ($Symbols -join ",")
    $client = [System.Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromSeconds($TimeoutSeconds)
    $client.DefaultRequestHeaders.UserAgent.ParseAdd($Headers["User-Agent"])
    $bytes = $client.GetByteArrayAsync($url).GetAwaiter().GetResult()
    $content = [System.Text.Encoding]::GetEncoding("GB18030").GetString($bytes)
    $quotes = @()

    foreach ($line in $content.Split(";", [System.StringSplitOptions]::RemoveEmptyEntries)) {
        $start = $line.IndexOf('"')
        $end = $line.LastIndexOf('"')
        if ($start -lt 0 -or $end -le $start) {
            continue
        }

        $body = $line.Substring($start + 1, $end - $start - 1)
        $parts = $body.Split("~")
        if ($parts.Length -lt 39) {
            continue
        }

        $quotes += [pscustomobject]@{
            Symbol = $parts[2]
            Name = $parts[1]
            Price = Read-Decimal $parts[3] 1
            ChangePercent = Read-Decimal $parts[32] 1
            TurnoverRate = Read-Decimal $parts[38] 1
            Amount = (Read-Decimal $parts[37] 1) * 10000
        }
    }

    return $quotes
}

function Test-Provider {
    param(
        [string]$Name,
        [scriptblock]$Action,
        [string[]]$Symbols,
        [int]$TimeoutSeconds
    )

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $quotes = & $Action $Symbols $TimeoutSeconds
        $stopwatch.Stop()
        $missingName = @($quotes | Where-Object { [string]::IsNullOrWhiteSpace($_.Name) }).Count
        $zeroPrice = @($quotes | Where-Object { $_.Price -le 0 }).Count
        $zeroAmount = @($quotes | Where-Object { $_.Amount -le 0 }).Count

        return [pscustomobject]@{
            Provider = $Name
            Success = $true
            ElapsedMilliseconds = $stopwatch.ElapsedMilliseconds
            QuoteCount = @($quotes).Count
            MissingNameCount = $missingName
            ZeroPriceCount = $zeroPrice
            ZeroAmountCount = $zeroAmount
            Error = $null
            Quotes = $quotes
        }
    }
    catch {
        $stopwatch.Stop()
        return [pscustomobject]@{
            Provider = $Name
            Success = $false
            ElapsedMilliseconds = $stopwatch.ElapsedMilliseconds
            QuoteCount = 0
            MissingNameCount = 0
            ZeroPriceCount = 0
            ZeroAmountCount = 0
            Error = $_.Exception.Message
            Quotes = @()
        }
    }
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$results = @()
for ($i = 1; $i -le $Iterations; $i++) {
    $timestamp = Get-Date

    if ($Provider -eq "EastMoney" -or $Provider -eq "Both") {
        $results += [pscustomobject]@{
            Iteration = $i
            Timestamp = $timestamp.ToString("o")
            Result = Test-Provider "EastMoney" ${function:Test-EastMoney} $Symbols $TimeoutSeconds
        }
    }

    if ($Provider -eq "Tencent" -or $Provider -eq "Both") {
        $results += [pscustomobject]@{
            Iteration = $i
            Timestamp = $timestamp.ToString("o")
            Result = Test-Provider "Tencent" ${function:Test-Tencent} $Symbols $TimeoutSeconds
        }
    }

    if ($i -lt $Iterations) {
        Start-Sleep -Milliseconds $DelayMilliseconds
    }
}

$flatResults = $results | ForEach-Object {
    [pscustomobject]@{
        Iteration = $_.Iteration
        Timestamp = $_.Timestamp
        Provider = $_.Result.Provider
        Success = $_.Result.Success
        ElapsedMilliseconds = $_.Result.ElapsedMilliseconds
        QuoteCount = $_.Result.QuoteCount
        MissingNameCount = $_.Result.MissingNameCount
        ZeroPriceCount = $_.Result.ZeroPriceCount
        ZeroAmountCount = $_.Result.ZeroAmountCount
        Error = $_.Result.Error
    }
}

$summary = $flatResults |
    Group-Object -Property Provider |
    ForEach-Object {
        $items = $_.Group
        $successes = @($items | Where-Object Success)
        [pscustomobject]@{
            Provider = $_.Name
            Attempts = $items.Count
            Successes = $successes.Count
            Failures = $items.Count - $successes.Count
            FailureRate = if ($items.Count -eq 0) { 0 } else { [math]::Round(($items.Count - $successes.Count) * 100.0 / $items.Count, 2) }
            AverageElapsedMilliseconds = if ($successes.Count -eq 0) { $null } else { [math]::Round(($successes | Measure-Object ElapsedMilliseconds -Average).Average, 2) }
            MaxElapsedMilliseconds = if ($successes.Count -eq 0) { $null } else { ($successes | Measure-Object ElapsedMilliseconds -Maximum).Maximum }
            AverageQuoteCount = if ($successes.Count -eq 0) { $null } else { [math]::Round(($successes | Measure-Object QuoteCount -Average).Average, 2) }
            FieldIssues = @($items | Where-Object { $_.MissingNameCount -gt 0 -or $_.ZeroPriceCount -gt 0 -or $_.ZeroAmountCount -gt 0 }).Count
            LastError = ($items | Where-Object { -not $_.Success } | Select-Object -Last 1).Error
        }
    }

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$jsonPath = Join-Path $OutputDir "realtime-provider-test-$stamp.json"
[pscustomobject]@{
    StartedAt = (Get-Date).ToString("o")
    Symbols = $Symbols
    Iterations = $Iterations
    DelayMilliseconds = $DelayMilliseconds
    TimeoutSeconds = $TimeoutSeconds
    Summary = $summary
    Results = $results
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

"Output: $jsonPath"
$summary | Format-Table -AutoSize
