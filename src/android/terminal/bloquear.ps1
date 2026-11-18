# Torna o Terminal Pontual "dono do aparelho" (Device Owner) no celular ligado pelo cabo, o que liga o bloqueio.
# SÓ PARA O APARELHO DE TESTE, e só depois de conferir que o desbloquear.ps1 está nesta mesma pasta.
#
# Condições da documentação oficial do Android para este comando funcionar sem restaurar de fábrica:
#   - nenhuma conta cadastrada no aparelho (Google, Samsung, WhatsApp...);
#   - o app já instalado (instalar.ps1).
# Relatos não oficiais: em Samsung, o "Dual Messenger" (WhatsApp duplicado) cria um segundo usuário e
# também impede o comando.
#
# O bloqueio desta versão de teste NÃO impede a restauração de fábrica e NÃO desliga a depuração USB.
# Uso:  powershell -ExecutionPolicy Bypass -File bloquear.ps1 -Confirmo
#       powershell -ExecutionPolicy Bypass -File bloquear.ps1 -Confirmo -Fraco
#
# -Fraco: NÃO torna o app dono do aparelho. Só faz do Terminal a tela de início (o botão de início volta
# para ele). Funciona com contas e com o WhatsApp duplicado, mas a atendente ainda sai pelos apps recentes
# ou pelas Configurações. É o modo usado enquanto o Dual Messenger estiver no aparelho.
param([switch]$Confirmo, [switch]$Fraco)

$pacote = 'br.com.pontualconsig.terminal'
$admin = "$pacote/.AdminReceiver"
$sdk = $env:ANDROID_HOME
if (-not $sdk -or -not (Test-Path $sdk)) { $sdk = Join-Path $env:LOCALAPPDATA 'Android\Sdk' }
$adb = Join-Path $sdk 'platform-tools\adb.exe'

if (-not $Confirmo) { Write-Host 'Rode com -Confirmo para aplicar o bloqueio.' -ForegroundColor Yellow; exit 1 }
if (-not (Test-Path (Join-Path $PSScriptRoot 'desbloquear.ps1'))) { Write-Host 'ERRO: falta o desbloquear.ps1.' -ForegroundColor Red; exit 1 }

$aparelhos = @(& $adb devices | Select-String "\tdevice$")
if ($aparelhos.Count -ne 1) {
    Write-Host "ERRO: preciso de exatamente 1 celular ligado e autorizado; encontrei $($aparelhos.Count)." -ForegroundColor Red
    exit 1
}

if (-not (& $adb shell pm list packages $pacote | Select-String "package:$pacote$")) {
    Write-Host 'ERRO: o app nao esta instalado. Rode antes o instalar.ps1.' -ForegroundColor Red
    exit 1
}

if ($Fraco) {
    $anterior = (& $adb shell cmd role get-role-holders android.app.role.HOME | Out-String).Trim()
    if ($anterior -and $anterior -ne $pacote) {
        Set-Content -Encoding ascii (Join-Path $PSScriptRoot 'tela-inicio-anterior.txt') $anterior
    }

    & $adb shell pm enable "$pacote/.Inicio" | Out-Null
    & $adb shell cmd role add-role-holder android.app.role.HOME $pacote
    if ($LASTEXITCODE -ne 0) { Write-Host 'O Android recusou trocar a tela de inicio.' -ForegroundColor Red; exit 1 }
    & $adb shell input keyevent KEYCODE_HOME
    Write-Host "Bloqueio fraco ligado: o Terminal e a tela de inicio (antes: $anterior)." -ForegroundColor Green
    Write-Host 'Para desfazer: desbloquear.ps1' -ForegroundColor Green
    exit 0
}

# Conferências antes de tentar (só leitura).
$contas = @(& $adb shell dumpsys account | Select-String 'Account \{')
$usuarios = @(& $adb shell pm list users | Select-String 'UserInfo\{')
Write-Host "Contas no aparelho: $($contas.Count)   Usuarios no aparelho: $($usuarios.Count)"
if ($contas.Count -gt 0 -or $usuarios.Count -gt 1) {
    Write-Host 'Ainda ha contas ou mais de um usuario (Dual Messenger, Pasta Segura). O Android deve recusar.' -ForegroundColor Yellow
    Write-Host 'Remova-os em Configuracoes e rode de novo. Nada foi alterado.' -ForegroundColor Yellow
    exit 1
}

& $adb shell dpm set-device-owner $admin
if ($LASTEXITCODE -ne 0) { Write-Host 'O Android recusou. Nada foi alterado. Traga a mensagem acima.' -ForegroundColor Red; exit 1 }

& $adb shell am start -n "$pacote/.MainActivity" | Out-Null
Write-Host 'Bloqueio ligado. Para desfazer: desbloquear.ps1 (pelo cabo) ou 7 toques na logo > Remover o bloqueio.' -ForegroundColor Green
