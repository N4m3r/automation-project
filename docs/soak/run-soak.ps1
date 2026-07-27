<#
.SYNOPSIS
  Soak test de go-live do VMS (critério de aceite §8 #1): N câmeras contínuas
  por T horas sem stall não-alarmado. Dirige o harness VmsSoakHarness com escala
  real via variáveis de ambiente.

.DESCRIPTION
  Usa câmeras SINTÉTICAS (FFmpeg testsrc → MediaMTX isolado), não câmeras reais —
  prova o pipeline de mídia (gateway + gravação concorrente) sob carga sem
  depender de hardware. Requer FFmpeg no PATH e mediamtx.exe na raiz do repo.

  Para o soak com câmeras REAIS em produção, use a API/monitor normalmente e
  colete /metrics (vms_recording_active, vms_recording_gaps_total) com o
  Prometheus — este script cobre o critério sem campo.

.EXAMPLE
  # Smoke rápido (default do harness): 4 cams x 40 s
  ./run-soak.ps1

.EXAMPLE
  # Go-live: 50 câmeras por 24 horas, segmentos de 5 min
  ./run-soak.ps1 -Cams 50 -Hours 24 -SegmentSeconds 300
#>
param(
  [int]$Cams = 8,
  [double]$Hours = 0,
  [int]$Seconds = 0,
  [int]$SegmentSeconds = 30
)

$ErrorActionPreference = 'Stop'

if ($Seconds -le 0) {
  $Seconds = if ($Hours -gt 0) { [int]($Hours * 3600) } else { 120 }
}

$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$proj = Join-Path $repo 'tests\SecurityPlatform.Tests\SecurityPlatform.Tests.csproj'

Write-Host "== VMS Soak =="
Write-Host "Câmeras       : $Cams"
Write-Host "Duração       : $Seconds s (~$([math]::Round($Seconds/3600,2)) h)"
Write-Host "Segmento      : $SegmentSeconds s"
Write-Host "Projeto       : $proj"
Write-Host ""

$env:SP_RELIABILITY = '1'
$env:SP_SOAK_CAMS = "$Cams"
$env:SP_SOAK_SECONDS = "$Seconds"
$env:SP_SOAK_SEGMENT = "$SegmentSeconds"

# Timeout do runner com folga sobre a janela + startup/gravação residual.
$timeoutMs = ($Seconds + 300) * 1000

Get-Process mediamtx, ffmpeg -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

$started = Get-Date
dotnet test $proj `
  --filter 'FullyQualifiedName~Reliability.VmsSoakHarness' `
  -v q --logger 'console;verbosity=normal' `
  -- RunConfiguration.TestSessionTimeout=$timeoutMs
$code = $LASTEXITCODE
$elapsed = (Get-Date) - $started

Write-Host ""
if ($code -eq 0) {
  Write-Host "SOAK OK — $Cams câmeras sem stall por $([math]::Round($elapsed.TotalMinutes,1)) min." -ForegroundColor Green
} else {
  Write-Host "SOAK FALHOU (exit $code) — ver mensagem de stall/ready acima." -ForegroundColor Red
}
exit $code
