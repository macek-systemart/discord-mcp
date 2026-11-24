#Requires -Version 7
<#
.SYNOPSIS
    Offline smoke test of a published DiscordMcp.exe, the file a release ships.

.DESCRIPTION
    The unit tests run the framework-dependent build; this checks the self-contained single-file exe itself: the
    usage text, both parser child verbs (Office from a test fixture, PDF from a tiny generated file) and an MCP
    session (initialize, tools/list, one tool call).

    Nothing here can reach Discord. The data folder is a new, empty folder, so there is no token and every tool
    answers with what to do instead of making a request. The test checks that the folder is still empty afterwards.

.EXAMPLE
    ./.github/scripts/smoke-test.ps1 -Exe out/exe/DiscordMcp.exe -ExpectedVersion 0.1.0
#>
param(
    [Parameter(Mandatory)] [string] $Exe,
    [string] $ExpectedVersion
)

$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
$fixtures = Join-Path $PSScriptRoot '..' '..' 'tests' 'DiscordMcp.Tests' 'Fixtures' 'office'
$work = Join-Path ([IO.Path]::GetTempPath()) "discord-mcp-smoke-$([guid]::NewGuid().ToString('N'))"
$dataDir = Join-Path $work 'data'
New-Item -ItemType Directory -Path $dataDir | Out-Null

function Assert-True([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw "Smoke test failed: $Message" }
}

function New-StartInfo([string[]] $Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($Exe)
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $start.Environment['DISCORD_MCP_HOME'] = $dataDir   # empty: no token, so no request can be made
    $start.Environment['DISCORD_GUILD_ID'] = '100000000000000000'
    $start.Environment['Discord__Language'] = 'en'
    $start.Environment['Discord__TimeZone'] = 'Europe/Prague'
    return $start
}

function Invoke-Exe([string[]] $Arguments) {
    $process = [Diagnostics.Process]::Start((New-StartInfo $Arguments))
    $process.StandardInput.Close()
    $stderr = $process.StandardError.ReadToEndAsync()
    $stdout = $process.StandardOutput.ReadToEndAsync()
    Assert-True $process.WaitForExit(30000) "'$($Arguments[0])' did not finish within 30 s"
    return [pscustomobject]@{ Code = $process.ExitCode; Out = $stdout.Result; Err = $stderr.Result }
}

# A one-page PDF with one line of Helvetica text, so PdfPig has to load its built-in font metrics.
function New-TestPdf([string] $Path) {
    $content = 'BT /F1 18 Tf 20 60 Td (Smoke test PDF) Tj ET'
    $objects = @(
        '<< /Type /Catalog /Pages 2 0 R >>'
        '<< /Type /Pages /Kids [3 0 R] /Count 1 >>'
        '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 144] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>'
        "<< /Length $($content.Length) >>`nstream`n$content`nendstream"
        '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>'
    )
    $pdf = [Text.StringBuilder]::new("%PDF-1.4`n")
    $offsets = foreach ($i in 0..($objects.Count - 1)) {
        $pdf.Length
        [void]$pdf.Append("$($i + 1) 0 obj`n$($objects[$i])`nendobj`n")
    }
    $xref = $pdf.Length
    [void]$pdf.Append("xref`n0 $($objects.Count + 1)`n0000000000 65535 f `n")
    foreach ($offset in $offsets) { [void]$pdf.Append(('{0:D10} 00000 n `n' -f $offset)) }
    [void]$pdf.Append("trailer`n<< /Size $($objects.Count + 1) /Root 1 0 R >>`nstartxref`n$xref`n%%EOF`n")
    [IO.File]::WriteAllText($Path, $pdf.ToString(), [Text.Encoding]::ASCII)
}

$server = $null
try {
    $help = Invoke-Exe @('help')
    Assert-True ($help.Code -eq 0) "help exited with $($help.Code)"
    Assert-True ($help.Err -match 'set-token') 'help does not list set-token'
    Write-Host 'ok  help'

    $docx = Invoke-Exe @('office-extract', (Join-Path $fixtures 'faktura.docx'), 'en')
    Assert-True ($docx.Code -eq 0) "office-extract (docx) exited with $($docx.Code)"
    Assert-True ($docx.Out -match 'Spolek') 'office-extract (docx) did not return the header text'
    $xlsx = Invoke-Exe @('office-extract', (Join-Path $fixtures 'faktury.xlsx'), 'cs')
    Assert-True ($xlsx.Code -eq 0 -and $xlsx.Out.Length -gt 0) "office-extract (xlsx) exited with $($xlsx.Code)"
    Write-Host 'ok  office-extract'

    $pdfPath = Join-Path $work 'smoke.pdf'
    New-TestPdf $pdfPath
    $pdf = Invoke-Exe @('pdf-extract', $pdfPath, '{"Page":null,"ImageBytes":0}')
    Assert-True ($pdf.Code -eq 0) "pdf-extract exited with $($pdf.Code)"
    Assert-True ($pdf.Out -match 'Smoke test PDF') 'pdf-extract did not return the page text'
    Write-Host 'ok  pdf-extract'

    $server = [Diagnostics.Process]::Start((New-StartInfo @()))
    $server.StandardInput.NewLine = "`n"
    $null = $server.StandardError.ReadToEndAsync()   # drained so the server never blocks on a full pipe

    function Send-Message($Message) {
        $server.StandardInput.WriteLine(($Message | ConvertTo-Json -Depth 10 -Compress))
        $server.StandardInput.Flush()
    }

    function Receive-Response([int] $Id) {
        while ($true) {
            $read = $server.StandardOutput.ReadLineAsync()
            Assert-True $read.Wait(30000) "no answer to request $Id within 30 s"
            Assert-True ($null -ne $read.Result) 'the server closed its output'
            $message = $read.Result | ConvertFrom-Json
            if ($message.id -eq $Id) {
                Assert-True ($null -eq $message.error) "request $Id failed: $($message.error | ConvertTo-Json -Compress)"
                return $message.result
            }
        }
    }

    Send-Message @{ jsonrpc = '2.0'; id = 1; method = 'initialize'; params = @{
            protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'smoke-test'; version = '0' } } }
    $init = Receive-Response 1
    Assert-True ($init.serverInfo.name -eq 'discord') "unexpected server name '$($init.serverInfo.name)'"
    if ($ExpectedVersion) {
        Assert-True ($init.serverInfo.version -eq $ExpectedVersion) "server reports version '$($init.serverInfo.version)', expected '$ExpectedVersion'"
    }
    Assert-True ($init.instructions.Contains('[ME]')) 'the instructions do not name the English self marker'
    Assert-True ($init.instructions.Contains('Europe/Prague')) 'the instructions do not name the configured time zone'
    Send-Message @{ jsonrpc = '2.0'; method = 'notifications/initialized' }

    Send-Message @{ jsonrpc = '2.0'; id = 2; method = 'tools/list' }
    $tools = (Receive-Response 2).tools
    Assert-True ($tools.Count -eq 7) "expected 7 tools, got $($tools.Count)"

    Send-Message @{ jsonrpc = '2.0'; id = 3; method = 'tools/call'; params = @{ name = 'server_overview'; arguments = @{} } }
    $answer = (Receive-Response 3).content[0].text
    Assert-True ($answer -match 'set-token') "server_overview without a token did not say to run set-token: $answer"
    Write-Host "ok  MCP session (version $($init.serverInfo.version), $($tools.Count) tools)"

    $server.StandardInput.Close()
    Assert-True $server.WaitForExit(15000) 'the server did not exit after its input closed'
    Assert-True ($server.ExitCode -eq 0) "the server exited with $($server.ExitCode)"
    $left = @(Get-ChildItem -LiteralPath $dataDir -Force -Recurse)
    Assert-True ($left.Count -eq 0) "the data folder is not empty: $($left.Name -join ', ')"
    Write-Host 'ok  clean exit, nothing written'
    Write-Host 'Smoke test passed.'
}
finally {
    if ($server -and -not $server.HasExited) { $server.Kill($true) }
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
