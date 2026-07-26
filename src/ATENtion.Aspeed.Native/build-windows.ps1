[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$clang = (Get-Command clang.exe -ErrorAction Stop).Source
$source = Join-Path $PSScriptRoot "decoder.c"
$output = Join-Path $PSScriptRoot "aspeed_codec.dll"

& $clang -shared -O2 -std=c11 "-Wl,/Brepro" -o $output $source
if ($LASTEXITCODE -ne 0) {
    throw "clang failed to build the ASPEED decoder (exit code $LASTEXITCODE)."
}

Write-Host "Built $output"
