# Importar-Login.ps1  -  Terminal Pontual
# Traz os logins do navegador (Chrome ou Edge) da atendente para o navegador interno do Terminal (WebView2),
# para que na PRIMEIRA vez que o Terminal abrir os sites ja estejam logados.
#
# COMO USAR: rode como ADMINISTRADOR, com a conta da atendente DESLOGADA (tela de entrada do Windows),
# senao os arquivos do navegador ficam travados e a copia falha.
#
# Nao descriptografa nada: copia os arquivos como estao; o proprio Terminal, rodando na conta da atendente,
# abre os logins depois (a chave fica presa ao usuario do Windows dela). Faz uma copia de seguranca antes.
#
# OBS.: WhatsApp Web e Google podem, por seguranca, pedir login de novo mesmo assim (a sessao e presa ao
# navegador/aparelho). Os demais sites normalmente ja entram logados.
# O gerador regrava este arquivo com BOM, para o PowerShell 5.1 ler os acentos.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms

function Msg([string]$Texto, [string]$Icone = 'Information', [string]$Botoes = 'OK') {
    return [System.Windows.Forms.MessageBox]::Show($Texto, 'Importar logins - Terminal Pontual', $Botoes, $Icone)
}

# --- precisa de administrador
$identidade = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not (New-Object Security.Principal.WindowsPrincipal($identidade)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $ps = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    try {
        $p = Start-Process -FilePath $ps -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$($MyInvocation.MyCommand.Path)`"" -Verb RunAs -Wait -PassThru
        exit $p.ExitCode
    } catch {
        Msg 'Esta ferramenta precisa de um administrador. Abra de novo e clique Sim.' 'Warning' | Out-Null
        exit 1
    }
}

try {
    # --- conta da atendente, pela marca do bloqueio
    $marcaKey = 'HKLM:\SOFTWARE\Pontual\Terminal'
    if (-not (Test-Path $marcaKey)) {
        Msg 'Nao encontrei a instalacao do Terminal com bloqueio neste computador. Rode o instalador antes.' 'Warning' | Out-Null
        exit 1
    }
    $marca = Get-ItemProperty $marcaKey
    $sid = [string]$marca.Sid
    $conta = [string]$marca.Conta
    if (-not $sid) { Msg 'A instalacao nao registrou a conta da atendente.' 'Warning' | Out-Null; exit 1 }

    # --- a conta precisa estar DESLOGADA (hive nao carregado), senao os arquivos ficam travados
    $aberta = [Microsoft.Win32.Registry]::Users.OpenSubKey($sid)
    if ($aberta) {
        $aberta.Close()
        Msg "A conta `"$conta`" esta aberta. Saia dela (va para a tela de entrada do Windows) e rode esta ferramenta de novo." 'Warning' | Out-Null
        exit 1
    }

    # --- pasta do perfil da atendente
    $perfil = (Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\$sid" -ErrorAction SilentlyContinue).ProfileImagePath
    if (-not $perfil -or -not (Test-Path $perfil)) { Msg 'Nao encontrei a pasta da conta da atendente.' 'Warning' | Out-Null; exit 1 }
    $localApp = Join-Path $perfil 'AppData\Local'

    # --- navegador de origem: Chrome ou Edge (se os dois, o mais usado recentemente)
    $fontes = @()
    $chrome = Join-Path $localApp 'Google\Chrome\User Data'
    $edge = Join-Path $localApp 'Microsoft\Edge\User Data'
    if (Test-Path (Join-Path $chrome 'Default')) { $fontes += @{ Nome = 'Google Chrome'; Base = $chrome } }
    if (Test-Path (Join-Path $edge 'Default')) { $fontes += @{ Nome = 'Microsoft Edge'; Base = $edge } }
    if ($fontes.Count -eq 0) { Msg 'A conta da atendente nao tem perfil do Chrome nem do Edge para copiar.' 'Warning' | Out-Null; exit 1 }
    $fonte = if ($fontes.Count -eq 1) { $fontes[0] } else {
        $fontes | Sort-Object { $c = Join-Path $_.Base 'Default\Network\Cookies'; if (Test-Path $c) { (Get-Item $c).LastWriteTime } else { [datetime]::MinValue } } -Descending | Select-Object -First 1
    }

    # --- destino: a pasta de perfil do WebView2 do Terminal (EBWebView se ja existir; senao cria)
    $wv = Join-Path $localApp 'Pontual\Terminal\WebView2'
    $eb = $null
    if (Test-Path $wv) {
        $cand = @(Get-ChildItem $wv -Directory -ErrorAction SilentlyContinue | Where-Object { Test-Path (Join-Path $_.FullName 'Default') })
        if ($cand.Count -gt 0) { $eb = $cand[0].FullName }
    }
    if (-not $eb) { $eb = Join-Path $wv 'EBWebView' }

    $ok = Msg ("Trazer os logins do $($fonte.Nome) da conta `"$conta`" para o Terminal?`n`n" +
        "Assim, na primeira vez que o Terminal abrir, os sites ja estarao logados.`n`n" +
        "WhatsApp Web e Google podem pedir login mesmo assim (seguranca); os demais normalmente ja entram.`n`n" +
        "Sera feita uma copia de seguranca antes.") 'Question' 'YesNo'
    if ($ok -ne 'Yes') { exit 0 }

    # fecha o Terminal se por acaso estiver rodando (nao deveria, a conta esta deslogada)
    Get-Process -Name 'Pontual.Terminal.Shell', 'msedgewebview2' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500

    New-Item -ItemType Directory -Force -Path $eb | Out-Null

    # copia de seguranca (fora da pasta WebView2, para o navegador nao varre-la)
    if (Test-Path (Join-Path $eb 'Default')) {
        $bak = Join-Path $localApp ('Pontual\Terminal\Backup-WebView2-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
        Copy-Item $eb $bak -Recurse -Force
    } else {
        $bak = '(sem backup: o Terminal ainda nao tinha navegador)'
    }

    $srcDefault = Join-Path $fonte.Base 'Default'
    $dstDefault = Join-Path $eb 'Default'
    New-Item -ItemType Directory -Force -Path $dstDefault | Out-Null

    $script:srcDefault = $srcDefault
    $script:dstDefault = $dstDefault
    $script:copiados = 0
    function CopiarArquivo([string]$Rel) {
        $s = Join-Path $script:srcDefault $Rel
        if (Test-Path $s) {
            $d = Join-Path $script:dstDefault $Rel
            New-Item -ItemType Directory -Force -Path (Split-Path $d) | Out-Null
            Copy-Item $s $d -Force
            $script:copiados += 1
        }
    }
    function CopiarPasta([string]$Rel) {
        $s = Join-Path $script:srcDefault $Rel
        if (Test-Path $s) {
            $d = Join-Path $script:dstDefault $Rel
            if (Test-Path $d) { Remove-Item $d -Recurse -Force }
            Copy-Item $s $d -Recurse -Force
            $script:copiados += 1
        }
    }

    # Local State guarda a chave que abre os cookies; precisa ir junto.
    $ls = Join-Path $fonte.Base 'Local State'
    if (Test-Path $ls) { Copy-Item $ls (Join-Path $eb 'Local State') -Force; $script:copiados += 1 }

    CopiarArquivo 'Network\Cookies'
    CopiarArquivo 'Login Data'
    CopiarPasta 'Local Storage'
    CopiarPasta 'IndexedDB'
    CopiarPasta 'Session Storage'

    if ($script:copiados -eq 0) {
        Msg 'Nada foi copiado (o navegador da atendente pode estar vazio ou protegido). Nenhuma mudanca foi feita.' 'Warning' | Out-Null
        exit 1
    }

    Msg ("Pronto. Os dados do $($fonte.Nome) foram trazidos para o Terminal ($($script:copiados) itens).`n`n" +
        "Entre na conta `"$conta`" e abra o Terminal: os sites devem ja estar logados (menos os que pedem login por seguranca, como WhatsApp Web).`n`n" +
        "Copia de seguranca: $bak") | Out-Null
} catch {
    Msg "A importacao nao foi concluida.`n`n$($_.Exception.Message)" 'Error' | Out-Null
    exit 1
}
