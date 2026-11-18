# DESFAZ o bloqueio do Terminal Pontual no celular ligado pelo cabo USB.
# Funciona porque o app de teste é marcado como testOnly (documentação oficial do adb:
# "dpm remove-active-admin ... The app must declare android:testOnly in the manifest.
#  This command also removes device and profile owners").
# Uso:  powershell -ExecutionPolicy Bypass -File desbloquear.ps1               (tira o bloqueio, mantém o app)
#       powershell -ExecutionPolicy Bypass -File desbloquear.ps1 -Desinstalar  (tira o bloqueio e apaga o app)
param([switch]$Desinstalar)

$pacote = 'br.com.pontualconsig.terminal'
$admin = "$pacote/.AdminReceiver"
$sdk = $env:ANDROID_HOME
if (-not $sdk -or -not (Test-Path $sdk)) { $sdk = Join-Path $env:LOCALAPPDATA 'Android\Sdk' }
$adb = Join-Path $sdk 'platform-tools\adb.exe'

$aparelhos = @(& $adb devices | Select-String "\tdevice$")
if ($aparelhos.Count -ne 1) {
    Write-Host "ERRO: preciso de exatamente 1 celular ligado e autorizado; encontrei $($aparelhos.Count)." -ForegroundColor Red
    exit 1
}

Write-Host 'Dono do aparelho antes:'
& $adb shell dpm list-owners

# Deixa de ser o dono do aparelho (isso também solta a tela presa).
& $adb shell dpm remove-active-admin $admin

# O botão de início volta a ser a tela de início que havia antes (anotada pelo bloquear.ps1 -Fraco).
$arquivo = Join-Path $PSScriptRoot 'tela-inicio-anterior.txt'
$anterior = if (Test-Path $arquivo) { (Get-Content $arquivo -Raw).Trim() } else { 'com.sec.android.app.launcher' }
& $adb shell cmd role add-role-holder android.app.role.HOME $anterior
& $adb shell cmd role remove-role-holder android.app.role.HOME $pacote 2>$null | Out-Null
& $adb shell pm disable $pacote/.Inicio 2>$null | Out-Null
Write-Host "Tela de inicio agora: $((& $adb shell cmd role get-role-holders android.app.role.HOME | Out-String).Trim())"

if ($Desinstalar) {
    & $adb uninstall $pacote
}

Write-Host 'Dono do aparelho depois (deve vir vazio):'
& $adb shell dpm list-owners
Write-Host 'Se a lista acima veio vazia, o celular voltou a ser um celular comum.' -ForegroundColor Green
Write-Host 'Se ainda aparecer o Terminal Pontual, NAO restaure de fabrica: traga a saida desta janela.' -ForegroundColor Yellow
