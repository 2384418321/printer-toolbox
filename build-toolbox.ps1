#requires -version 2.0
<#
  Build script for PrinterToolbox -- a single-file WinForms exe.

  ASCII-only on purpose: Windows PowerShell 5.1 decodes BOM-less .ps1 files as GBK,
  so any non-ASCII byte in this file would corrupt the script.

  Layout assumed by this script (script location = repo root):
      .\build-toolbox.ps1
      .\src\*.cs
      .\src\app.manifest
      .\src\icon.ico
      .\dist\PrinterToolbox.exe      <- output

  Requires no .NET SDK: it drives the csc.exe that ships with the .NET Framework 4.x
  runtime, which is present on Windows 10 and 11.
#>
param(
    [switch]$Clean,
    [switch]$Run,
    [switch]$Dump
)

$ErrorActionPreference = 'Stop'

# Repo root = directory containing this script (no hardcoded absolute paths).
$root = $PSScriptRoot
if (-not $root) { $root = Split-Path -Parent $MyInvocation.MyCommand.Path }

$src = Join-Path $root 'src'
$dist = Join-Path $root 'dist'
$exe = Join-Path $dist 'PrinterToolbox.exe'

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    # 32-bit fallback (some x86-only Windows installs lack the 64-bit compiler).
    $csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
}

foreach ($p in @($csc, $src)) {
    if (-not (Test-Path $p)) { throw "missing: $p" }
}

if ($Clean -and (Test-Path $dist)) { Remove-Item $dist -Recurse -Force }
if (-not (Test-Path $dist)) { New-Item -ItemType Directory -Path $dist | Out-Null }

$sources = @(Get-ChildItem -Path $src -Filter *.cs | Sort-Object Name)
if ($sources.Count -eq 0) { throw "no .cs files under $src" }
foreach ($s in $sources) { if (-not (Test-Path $s.FullName)) { throw "missing: $($s.FullName)" } }

$manifest = Join-Path $src 'app.manifest'
$icon = Join-Path $src 'icon.ico'
if (-not (Test-Path $icon)) { $icon = $null }

$refs = @(
    '/reference:System.dll',
    '/reference:System.Core.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.Windows.Forms.dll',
    '/reference:System.Management.dll'
)

$argv = @(
    '/nologo',
    '/target:winexe',
    '/codepage:65001',
    '/langversion:5',
    '/optimize+',
    '/warn:2',
    "/out:$exe"
) + $refs

if (Test-Path $manifest) { $argv += "/win32manifest:$manifest" }
if ($icon) { $argv += "/win32icon:$icon" }

foreach ($s in $sources) { $argv += $s.FullName }

Write-Host ("files:  " + (($sources | ForEach-Object { $_.Name }) -join ', '))
Write-Host ("icon:   " + $(if ($icon) { $icon } else { '(none)' }))
Write-Host ''

$out = & $csc @argv 2>&1
$out | ForEach-Object { Write-Host $_ }
if ($LASTEXITCODE -ne 0) { throw "compile failed (exit $LASTEXITCODE)" }
if (-not (Test-Path $exe)) { throw "compiler reported success but $exe is missing" }

# ---- code signing (optional) ----
# Signing is skipped when no code-signing certificate is available, so contributors
# without a certificate can still build. Set PT_SIGN_SUBJECT to the exact CN of a cert
# in Cert:\CurrentUser\My to sign with it.
$signSubject = $env:PT_SIGN_SUBJECT
if (-not $signSubject) { $signSubject = 'CN=PrinterToolbox Local Code Signing' }

$signCert = $null
try {
    $signCert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction Stop |
                Where-Object { $_.Subject -eq $signSubject } | Select-Object -First 1
} catch { $signCert = $null }

if ($signCert) {
    $sig = Set-AuthenticodeSignature -FilePath $exe -Certificate $signCert -HashAlgorithm SHA256
    if ($sig.Status -eq 'Valid') { Write-Host ("signed: " + $signCert.Subject) }
    else { Write-Host ("signed: FAILED -> " + $sig.Status + " " + $sig.StatusMessage) }
} else {
    Write-Host ('signed: skipped (no code signing cert named ' + $signSubject + ')')
}

# ---- verification ----
$fi = Get-Item $exe
Write-Host ''
Write-Host ("OK  -> " + $fi.FullName)
Write-Host ("size: " + $fi.Length + " bytes")

$bytes = [System.IO.File]::ReadAllBytes($exe)
$text = [System.Text.Encoding]::ASCII.GetString($bytes)
$m = [regex]::Match($text, 'requestedExecutionLevel\s+level="(\w+)"')
if ($m.Success) { Write-Host ("manifest level: " + $m.Groups[1].Value) }
else { Write-Host 'manifest level: NOT FOUND (manifest did not embed)' }

$fv = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
Write-Host ("file version: " + $fv.FileVersion)
Write-Host ("product:      " + $fv.ProductName)
Write-Host ("signature:    " + (Get-AuthenticodeSignature $exe).Status)

if ($Dump) {
    Write-Host ''
    Write-Host 'dumping layout (no desktop needed)...'
    & $exe --dump-ui --toolbox
    Write-Host ("dump exit: " + $LASTEXITCODE)
}

if ($Run) {
    Write-Host ''
    Write-Host 'launching...'
    Start-Process -FilePath $exe
}
