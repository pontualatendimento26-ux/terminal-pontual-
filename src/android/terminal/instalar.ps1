# Instala (ou atualiza) o Terminal Pontual de teste no celular ligado pelo cabo USB e abre o app.
# NÃO aplica bloqueio nenhum: o app entra como um aplicativo comum. O bloqueio é o bloquear.ps1.
# Uso:  powershell -ExecutionPolicy Bypass -File instalar.ps1            (celular na mesma rede da Central)
#       powershell -ExecutionPolicy Bypass -File instalar.ps1 -PeloCabo  (celular fala com a Central pelo cabo)
param([switch]$PeloCabo)

$ErrorActionPreference = 'Stop'
$pacote = 'br.com.pontualconsig.terminal'
$raiz = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$apk = Get-ChildItem (Join-Path $raiz 'dist') -Filter 'Terminal-Pontual-Android-*.apk' | Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $apk) { Write-Host 'ERRO: rode antes o compilar.ps1.' -ForegroundColor Red; exit 1 }

$sdk = $env:ANDROID_HOME
if (-not $sdk -or -not (Test-Path $sdk)) { $sdk = Join-Path $env:LOCALAPPDATA 'Android\Sdk' }
$adb = Join-Path $sdk 'platform-tools\adb.exe'

$aparelhos = @(& $adb devices | Select-String "\tdevice$")
if ($aparelhos.Count -ne 1) {
    Write-Host "ERRO: preciso de exatamente 1 celular ligado e autorizado; encontrei $($aparelhos.Count)." -ForegroundColor Red
    exit 1
}

# -t é exigido porque o app é marcado como "de teste" (é isso que permite desfazer o bloqueio pelo cabo).
& $adb install -t -r $apk.FullName
if ($LASTEXITCODE -ne 0) { Write-Host 'ERRO: a instalacao falhou.' -ForegroundColor Red; exit 1 }

# Acessos especiais do vigia de aplicativos: saber qual app está na frente e trazer o Terminal de volta.
& $adb shell appops set $pacote GET_USAGE_STATS allow
& $adb shell appops set $pacote SYSTEM_ALERT_WINDOW allow

if ($PeloCabo) {
    # O endereço http://localhost:5080 do celular passa a cair na Central deste PC. Vale só com o cabo ligado.
    & $adb reverse tcp:5080 tcp:5080 | Out-Null
    & $adb shell am start -n "$pacote/.MainActivity" --es servidor 'http://localhost:5080' | Out-Null
    Write-Host 'Instalado. No celular, o endereco da Central ja esta como http://localhost:5080 (pelo cabo).' -ForegroundColor Green
} else {
    & $adb shell am start -n "$pacote/.MainActivity" | Out-Null
    Write-Host 'Instalado e aberto no celular.' -ForegroundColor Green
}
