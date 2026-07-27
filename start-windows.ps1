# Sobe a plataforma no Windows sem Docker (topologia All-in-One).
# Pre-requisitos: .NET 8 SDK, FFmpeg no PATH e mediamtx.exe nesta pasta.
# Encoding: ASCII-only (evita parse error do PowerShell com acentos).

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
Set-Location $root

foreach ($cmd in @("dotnet", "ffmpeg")) {
    if (-not (Get-Command $cmd -ErrorAction SilentlyContinue)) {
        throw "'$cmd' nao encontrado no PATH. Veja o README, secao 9.2."
    }
}

$mediamtx = Join-Path $root "mediamtx.exe"
if (Test-Path $mediamtx) {
    if (-not (Get-Process mediamtx -ErrorAction SilentlyContinue)) {
        Write-Host "Iniciando MediaMTX..." -ForegroundColor Cyan
        # NUNCA passar path absoluto com espaco no ArgumentList:
        # WorkingDirectory + "mediamtx.yml" relativo resolve isso.
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $mediamtx
        $psi.Arguments = "mediamtx.yml"
        $psi.WorkingDirectory = $root
        $psi.UseShellExecute = $true
        $psi.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Minimized
        [System.Diagnostics.Process]::Start($psi) | Out-Null
        Start-Sleep -Seconds 2
        if (-not (Get-Process mediamtx -ErrorAction SilentlyContinue)) {
            Write-Warning "MediaMTX nao subiu - live WebRTC/HLS indisponivel."
        } else {
            Write-Host "MediaMTX em execucao." -ForegroundColor Green
        }
    } else {
        Write-Host "MediaMTX ja em execucao." -ForegroundColor Green
    }
} else {
    Write-Warning "mediamtx.exe ausente - live WebRTC/HLS fica indisponivel (gravacao funciona)."
    Write-Warning "Baixe em https://github.com/bluenviron/mediamtx/releases e coloque em $root"
}

# Development carrega appsettings.Development.json (JwtKey local).
# Em producao: $env:Security__JwtKey = "<aleatoria-32+>" e ASPNETCORE_ENVIRONMENT=Production
if (-not $env:ASPNETCORE_ENVIRONMENT) {
    $env:ASPNETCORE_ENVIRONMENT = "Development"
}

# HTTPS opcional: se auto.crt/auto.key existirem, habilita 8443 sem editar appsettings.
$crt = Join-Path $root "auto.crt"
$key = Join-Path $root "auto.key"
if ((Test-Path $crt) -and (Test-Path $key) -and -not $env:Security__Https__Enabled) {
    Write-Host "Certificado auto.crt encontrado - HTTPS em :8443 (alem de HTTP :8080)" -ForegroundColor Cyan
    $env:Security__Https__Enabled = "true"
    $env:Security__Https__CertificatePath = $crt
    $env:Security__Https__KeyPath = $key
    $env:Security__Https__Port = "8443"
    $env:Security__Https__AlsoListenHttp = "true"
    $env:Security__Https__HttpPort = "8080"
}

# Keyring compartilhado (multi-no): aponte Security__KeyRingPath para o mesmo diretorio em todos os nos.
if (-not $env:Security__KeyRingPath) {
    $env:Security__KeyRingPath = Join-Path $root "src\SecurityPlatform.Api\data\keys"
}

Write-Host "Iniciando API ($env:ASPNETCORE_ENVIRONMENT) em http://localhost:8080 ..." -ForegroundColor Cyan
$proj = Join-Path $root "src\SecurityPlatform.Api\SecurityPlatform.Api.csproj"
dotnet run --project $proj -c Release --no-launch-profile
