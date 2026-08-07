# Builds publish/PayAttention-UserGuide.pdf from docs/UserGuide.md
# Renders Markdown to HTML (pandoc if available, else a small built-in converter),
# then prints to PDF with Microsoft Edge headless.
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$md = Join-Path $root "docs\UserGuide.md"
$publishDir = Join-Path $root "publish"
$pdf = Join-Path $publishDir "PayAttention-UserGuide.pdf"

if (-not (Test-Path $md)) {
    Write-Error "User guide not found: $md"
    exit 1
}

New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

function Find-Edge {
    $candidates = @(
        (Get-Command msedge -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source),
        "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
        "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
        "$env:LOCALAPPDATA\Microsoft\Edge\Application\msedge.exe"
    ) | Where-Object { $_ -and (Test-Path $_) }
    return $candidates | Select-Object -First 1
}

function Format-InlineHtml([string]$text) {
    $t = [System.Net.WebUtility]::HtmlEncode($text)
    $t = [regex]::Replace($t, '\*\*(.+?)\*\*', '<strong>$1</strong>')
    $t = [regex]::Replace($t, '`([^`]+)`', '<code>$1</code>')
    return $t
}

function Convert-MarkdownToHtml([string]$markdownPath, [string]$htmlPath) {
    $lines = Get-Content -LiteralPath $markdownPath -Encoding UTF8
    $chunks = New-Object System.Collections.Generic.List[string]
    $listType = $null
    $inTable = $false
    $tableHeaderDone = $false
    $paraParts = New-Object System.Collections.Generic.List[string]

    foreach ($line in $lines) {
        if ($line -match '^\|') {
            if ($paraParts.Count -gt 0) {
                $chunks.Add("<p>$(Format-InlineHtml (($paraParts -join ' ').Trim()))</p>") | Out-Null
                $paraParts.Clear()
            }
            if ($null -ne $listType) {
                $chunks.Add("</$listType>") | Out-Null
                $listType = $null
            }
            if (-not $inTable) {
                $chunks.Add("<table>") | Out-Null
                $inTable = $true
                $tableHeaderDone = $false
            }
            if ($line -match '^\|\s*-+') { continue }
            $cells = @(($line.Trim().Trim('|') -split '\|') | ForEach-Object { $_.Trim() })
            $tag = if (-not $tableHeaderDone) { "th" } else { "td" }
            $row = ($cells | ForEach-Object { "<$tag>$(Format-InlineHtml $_)</$tag>" }) -join ""
            $chunks.Add("<tr>$row</tr>") | Out-Null
            $tableHeaderDone = $true
            continue
        }

        if ($inTable) {
            $chunks.Add("</table>") | Out-Null
            $inTable = $false
            $tableHeaderDone = $false
        }

        if ($line -match '^# (.+)$') {
            if ($paraParts.Count -gt 0) {
                $chunks.Add("<p>$(Format-InlineHtml (($paraParts -join ' ').Trim()))</p>") | Out-Null
                $paraParts.Clear()
            }
            if ($null -ne $listType) { $chunks.Add("</$listType>") | Out-Null; $listType = $null }
            $chunks.Add("<h1>$(Format-InlineHtml $Matches[1])</h1>") | Out-Null
            continue
        }
        if ($line -match '^## (.+)$') {
            if ($paraParts.Count -gt 0) {
                $chunks.Add("<p>$(Format-InlineHtml (($paraParts -join ' ').Trim()))</p>") | Out-Null
                $paraParts.Clear()
            }
            if ($null -ne $listType) { $chunks.Add("</$listType>") | Out-Null; $listType = $null }
            $chunks.Add("<h2>$(Format-InlineHtml $Matches[1])</h2>") | Out-Null
            continue
        }
        if ($line -match '^-\s+(.+)$') {
            if ($paraParts.Count -gt 0) {
                $chunks.Add("<p>$(Format-InlineHtml (($paraParts -join ' ').Trim()))</p>") | Out-Null
                $paraParts.Clear()
            }
            if ($listType -ne "ul") {
                if ($null -ne $listType) { $chunks.Add("</$listType>") | Out-Null }
                $chunks.Add("<ul>") | Out-Null
                $listType = "ul"
            }
            $chunks.Add("<li>$(Format-InlineHtml $Matches[1])</li>") | Out-Null
            continue
        }
        if ($line -match '^\d+\.\s+(.+)$') {
            if ($paraParts.Count -gt 0) {
                $chunks.Add("<p>$(Format-InlineHtml (($paraParts -join ' ').Trim()))</p>") | Out-Null
                $paraParts.Clear()
            }
            if ($listType -ne "ol") {
                if ($null -ne $listType) { $chunks.Add("</$listType>") | Out-Null }
                $chunks.Add("<ol>") | Out-Null
                $listType = "ol"
            }
            $chunks.Add("<li>$(Format-InlineHtml $Matches[1])</li>") | Out-Null
            continue
        }
        if ([string]::IsNullOrWhiteSpace($line)) {
            if ($paraParts.Count -gt 0) {
                $chunks.Add("<p>$(Format-InlineHtml (($paraParts -join ' ').Trim()))</p>") | Out-Null
                $paraParts.Clear()
            }
            if ($null -ne $listType) { $chunks.Add("</$listType>") | Out-Null; $listType = $null }
            continue
        }

        if ($null -ne $listType) { $chunks.Add("</$listType>") | Out-Null; $listType = $null }
        $paraParts.Add($line.Trim()) | Out-Null
    }

    if ($paraParts.Count -gt 0) {
        $chunks.Add("<p>$(Format-InlineHtml (($paraParts -join ' ').Trim()))</p>") | Out-Null
    }
    if ($null -ne $listType) { $chunks.Add("</$listType>") | Out-Null }
    if ($inTable) { $chunks.Add("</table>") | Out-Null }

    $css = @"
body { font-family: 'Segoe UI', Calibri, sans-serif; font-size: 11pt; line-height: 1.45; max-width: 720px; margin: 0.9in auto; color: #111; }
h1 { font-size: 20pt; margin-top: 0; }
h2 { font-size: 14pt; margin-top: 1.2em; border-bottom: 1px solid #ccc; padding-bottom: 0.2em; }
code { font-family: Consolas, 'Courier New', monospace; font-size: 0.95em; background: #f3f3f3; padding: 0 0.25em; }
table { border-collapse: collapse; width: 100%; margin: 0.8em 0; }
th, td { border: 1px solid #999; padding: 6px 8px; text-align: left; vertical-align: top; }
th { background: #f0f0f0; }
ul, ol { margin: 0.4em 0 0.8em 1.2em; }
"@

    $html = @"
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8" />
<title>Pay Attention User Guide</title>
<style>
$css
</style>
</head>
<body>
$($chunks -join "`n")
</body>
</html>
"@
    Set-Content -LiteralPath $htmlPath -Value $html -Encoding UTF8
}

function New-PdfFromHtml([string]$htmlPath, [string]$pdfPath, [string]$edgePath) {
    if (Test-Path $pdfPath) { Remove-Item -LiteralPath $pdfPath -Force }
    $htmlUri = ([Uri]$htmlPath).AbsoluteUri
    & $edgePath --headless --disable-gpu --no-pdf-header-footer --print-to-pdf="$pdfPath" $htmlUri | Out-Null
    $deadline = (Get-Date).AddSeconds(20)
    while ((-not (Test-Path $pdfPath) -or (Get-Item $pdfPath).Length -lt 100) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }
    if (-not (Test-Path $pdfPath) -or (Get-Item $pdfPath).Length -lt 100) {
        throw "Edge did not produce a PDF at $pdfPath"
    }
}

$edge = Find-Edge
if (-not $edge) {
    Write-Error "Cannot build PDF: Microsoft Edge is required (or install pandoc + a PDF engine)."
    exit 1
}

$tempHtml = Join-Path $env:TEMP ("pa_user_guide_" + [guid]::NewGuid().ToString("N") + ".html")
try {
    $pandoc = Get-Command pandoc -ErrorAction SilentlyContinue
    if ($pandoc) {
        Write-Host "Rendering user guide HTML with pandoc..."
        & pandoc $md -t html5 -s --metadata title="Pay Attention User Guide" -o $tempHtml
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path $tempHtml)) {
            Write-Host "pandoc HTML failed; falling back to built-in Markdown converter..."
            Convert-MarkdownToHtml -markdownPath $md -htmlPath $tempHtml
        }
    }
    else {
        Write-Host "Rendering user guide HTML..."
        Convert-MarkdownToHtml -markdownPath $md -htmlPath $tempHtml
    }

    Write-Host "Printing PDF with Edge..."
    New-PdfFromHtml -htmlPath $tempHtml -pdfPath $pdf -edgePath $edge
    Write-Host "Wrote $pdf"
}
finally {
    if (Test-Path $tempHtml) { Remove-Item -LiteralPath $tempHtml -Force -ErrorAction SilentlyContinue }
}
