# Instalador de teste do Terminal Pontual.
# Instala so para o usuario atual, sem pedir administrador e sem alterar
# nenhuma politica do Windows. Tudo o que ele faz e desfeito por Desinstalar.ps1.
# O gerador regrava este arquivo com BOM, para o PowerShell 5.1 ler os acentos.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms

$Nome = 'Terminal Pontual'
$Destino = Join-Path $env:LOCALAPPDATA 'Programs\Terminal Pontual'
$Origem = Split-Path -Parent $MyInvocation.MyCommand.Path
$Pacote = Join-Path $Origem 'app.zip'
$Exe = Join-Path $Destino 'Pontual.Terminal.Shell.exe'
$Inicio = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Terminal Pontual'
$Desktop = [Environment]::GetFolderPath('Desktop')

function Mensagem([string]$Texto, [string]$Icone = 'Information', [string]$Botoes = 'OK') {
    return [System.Windows.Forms.MessageBox]::Show($Texto, "Instalação do $Nome", $Botoes, $Icone)
}

function Atalho([string]$Caminho, [string]$Alvo, [string]$Argumentos = '', [string]$Descricao = '') {
    $shell = New-Object -ComObject WScript.Shell
    $atalho = $shell.CreateShortcut($Caminho)
    $atalho.TargetPath = $Alvo
    $atalho.Arguments = $Argumentos
    $atalho.WorkingDirectory = Split-Path -Parent $Alvo
    $atalho.Description = $Descricao
    $atalho.Save()
}

function WebView2Instalado {
    $chave = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
    $locais = @(
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\$chave",
        "HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\$chave",
        "HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients\$chave"
    )
    foreach ($local in $locais) {
        $versao = (Get-ItemProperty -Path $local -Name pv -ErrorAction SilentlyContinue).pv
        if ($versao -and $versao -ne '0.0.0.0') { return $true }
    }
    return $false
}

try {
    if (-not (Test-Path $Pacote)) { throw "O arquivo do programa (app.zip) não foi encontrado ao lado do instalador." }

    $resposta = Mensagem "Este instalador coloca o $Nome neste computador, só para o usuário atual ($env:USERNAME).`n`nEle NÃO altera o Windows nem bloqueia nada: é uma versão de teste.`n`nDeseja continuar?" 'Question' 'YesNo'
    if ($resposta -ne 'Yes') { exit 0 }

    # Fecha o programa se estiver aberto, para poder substituir os arquivos.
    Get-Process -Name 'Pontual.Terminal.Shell' -ErrorAction SilentlyContinue | Stop-Process -Force -Confirm:$false
    Start-Sleep -Milliseconds 600

    # Guarda a lista de sites que o TI ja tiver editado numa instalacao anterior.
    $configAntiga = $null
    $caminhoConfig = Join-Path $Destino 'config-local.json'
    if (Test-Path $caminhoConfig) { $configAntiga = Get-Content -Path $caminhoConfig -Raw -Encoding UTF8 }

    if (Test-Path $Destino) { Remove-Item -Path $Destino -Recurse -Force -Confirm:$false }
    New-Item -ItemType Directory -Force -Path $Destino | Out-Null
    Expand-Archive -Path $Pacote -DestinationPath $Destino -Force
    if (-not (Test-Path $Exe)) { throw "A cópia dos arquivos falhou: o programa não está em $Destino." }

    if ($configAntiga) { [System.IO.File]::WriteAllText($caminhoConfig, $configAntiga, (New-Object System.Text.UTF8Encoding($false))) }
    Copy-Item -Path (Join-Path $Origem 'Desinstalar.ps1') -Destination $Destino -Force

    New-Item -ItemType Directory -Force -Path $Inicio | Out-Null
    Atalho (Join-Path $Desktop "$Nome.lnk") $Exe '' 'Abre o Terminal Pontual em tela cheia'
    Atalho (Join-Path $Inicio "$Nome.lnk") $Exe '' 'Abre o Terminal Pontual em tela cheia'
    Atalho (Join-Path $Inicio "$Nome (em janela).lnk") $Exe '--janela' 'Abre o Terminal Pontual numa janela comum'
    Atalho (Join-Path $Inicio "$Nome (trocar o modo).lnk") $Exe '--reconfigurar' 'Escolhe de novo entre usar sem servidor ou conectar a um servidor'
    Atalho (Join-Path $Inicio 'Lista de sites (editar).lnk') "$env:WINDIR\notepad.exe" "`"$caminhoConfig`"" 'Abre a lista de sites no Bloco de Notas'
    Atalho (Join-Path $Inicio 'Desinstalar o Terminal Pontual.lnk') "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$(Join-Path $Destino 'Desinstalar.ps1')`"" 'Remove o Terminal Pontual deste usuário'

    # O programa precisa do WebView2 Runtime, que nem todo Windows 10 tem.
    $avisoWebView = ''
    if (-not (WebView2Instalado)) {
        $instaladorWebView = Join-Path $Origem 'MicrosoftEdgeWebview2Setup.exe'
        if (Test-Path $instaladorWebView) {
            Mensagem "Falta um componente da Microsoft (WebView2 Runtime), necessário para abrir os sites.`n`nEle será baixado e instalado agora. É preciso estar com internet e pode levar alguns minutos." | Out-Null
            Start-Process -FilePath $instaladorWebView -ArgumentList '/silent', '/install' -Wait
        }
        if (-not (WebView2Instalado)) {
            $avisoWebView = "`n`nATENÇÃO: o componente WebView2 Runtime não pôde ser instalado. Sem ele o programa não abre. Instale-o pelo site da Microsoft (procure por `"WebView2 Runtime Evergreen`") e abra o programa de novo."
        }
    }

    $abrir = Mensagem "Instalação concluída.`n`nFoi criado um atalho `"$Nome`" na área de trabalho.`n`nPara sair do programa durante o teste: Ctrl + Shift + Alt + Q.$avisoWebView`n`nAbrir o $Nome agora?" 'Information' 'YesNo'
    if ($abrir -eq 'Yes') { Start-Process -FilePath $Exe }
}
catch {
    Mensagem "A instalação não foi concluída.`n`n$($_.Exception.Message)" 'Error' | Out-Null
    exit 1
}
