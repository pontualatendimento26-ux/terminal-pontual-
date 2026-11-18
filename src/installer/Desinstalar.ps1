# Remove o Terminal Pontual do usuario atual: programa, atalhos e, se o
# usuario quiser, os dados do teste. Nao ha mais nada a desfazer, porque o
# instalador de teste nao altera politicas do Windows.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms

$Nome = 'Terminal Pontual'
$Destino = Join-Path $env:LOCALAPPDATA 'Programs\Terminal Pontual'
$Dados = Join-Path $env:LOCALAPPDATA 'Pontual\Terminal'
$Inicio = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Terminal Pontual'
$Desktop = [Environment]::GetFolderPath('Desktop')

function Mensagem([string]$Texto, [string]$Icone = 'Information', [string]$Botoes = 'OK') {
    return [System.Windows.Forms.MessageBox]::Show($Texto, "Remoção do $Nome", $Botoes, $Icone)
}

try {
    if ((Mensagem "Remover o $Nome deste usuário ($env:USERNAME)?" 'Question' 'YesNo') -ne 'Yes') { exit 0 }

    Get-Process -Name 'Pontual.Terminal.Shell' -ErrorAction SilentlyContinue | Stop-Process -Force -Confirm:$false
    Start-Sleep -Milliseconds 600

    $apagarDados = $false
    if (Test-Path $Dados) {
        $apagarDados = (Mensagem "Apagar também os dados do teste (registro de uso, escolha do modo e dados de navegação dos sites)?`n`nEscolha Não se quiser guardar o arquivo registro.log para conferir depois." 'Question' 'YesNo') -eq 'Yes'
    }

    Remove-Item -Path (Join-Path $Desktop "$Nome.lnk") -Force -Confirm:$false -ErrorAction SilentlyContinue
    if (Test-Path $Inicio) { Remove-Item -Path $Inicio -Recurse -Force -Confirm:$false }
    if ($apagarDados -and (Test-Path $Dados)) { Remove-Item -Path $Dados -Recurse -Force -Confirm:$false }

    # Este script esta dentro da pasta que sera apagada; a remocao e feita
    # por um processo separado, depois que este terminar.
    if (Test-Path $Destino) {
        $comando = "Start-Sleep -Seconds 2; Remove-Item -LiteralPath '$Destino' -Recurse -Force -Confirm:`$false"
        Start-Process -FilePath 'powershell.exe' -ArgumentList '-NoProfile', '-WindowStyle', 'Hidden', '-Command', $comando -WindowStyle Hidden
    }

    $resto = if ($apagarDados) { '' } else { "`n`nOs dados do teste ficaram em:`n$Dados" }
    Mensagem "O $Nome foi removido.$resto" | Out-Null
}
catch {
    Mensagem "A remoção não foi concluída.`n`n$($_.Exception.Message)" 'Error' | Out-Null
    exit 1
}
