# Atualizacao do Terminal Pontual com um clique (botao "Atualizar" do Terminal).
#
# Roda como SISTEMA, pela tarefa agendada "Terminal Pontual - Atualizar" que o instalador cria.
# So a conta da atendente (e os administradores) podem disparar a tarefa; ninguem consegue
# mudar o que ela executa, porque este arquivo fica em "Arquivos de Programas".
#
# Passos:
#   1. le o endereco da Central que o Terminal deixou em ProgramData\Pontual\Atualizacao\pedido.txt;
#   2. baixa /baixar/atualizacao/versao.json e confere a ASSINATURA com a chave publica instalada
#      (chave-publica.xml, ao lado deste arquivo). Pacote sem assinatura valida e recusado, venha de onde vier;
#   3. so aceita versao MAIOR que a instalada (nao volta versao);
#   4. baixa o pacote, confere o SHA-256 assinado, descompacta e confere a versao do programa;
#   5. fecha o Terminal, troca a pasta do programa (a antiga fica como copia ate dar certo),
#      reaplica o bloqueio da conta com as regras da versao nova e REINICIA o computador.
# Se algo falhar antes do passo 5, nada muda no computador e o Terminal mostra o erro.
# Se falhar durante a troca, a pasta antiga volta e o computador reinicia na versao antiga.
# O gerador regrava este arquivo com BOM, para o PowerShell 5.1 ler os acentos.

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$Origem = Split-Path -Parent $MyInvocation.MyCommand.Path

$ArquivosDeProgramas = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
$Destino = Join-Path $ArquivosDeProgramas 'Terminal Pontual'
$Anterior = "$Destino.anterior"
$Exe = Join-Path $Destino 'Pontual.Terminal.Shell.exe'
# Pasta de troca com o Terminal: a atendente escreve o pedido, este script escreve o estado.
$PastaPedido = Join-Path $env:ProgramData 'Pontual\Atualizacao'
$Pedido = Join-Path $PastaPedido 'pedido.txt'
$Estado = Join-Path $PastaPedido 'estado.txt'
$Historico = Join-Path $PastaPedido 'historico.log'
# Pasta de trabalho so do SISTEMA: a atendente nao consegue trocar o pacote depois de conferido.
$Trabalho = Join-Path $env:ProgramData 'Pontual\Atualizacao-sistema'
$ChaveMarca = 'SOFTWARE\Pontual\Terminal'
$ChaveRemocao = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\TerminalPontual'
$Texto = New-Object System.Text.UTF8Encoding($false)

function Anotar([string]$Mensagem) {
    try { [System.IO.File]::AppendAllText($Historico, ((Get-Date).ToString('yyyy-MM-dd HH:mm:ss') + '  ' + $Mensagem + "`r`n"), $Texto) } catch { }
}

# Estado lido pelo Terminal: "etapa|texto". Etapas: verificando, baixando, instalando, reiniciando, atual, erro.
function Estado([string]$Etapa, [string]$Mensagem) {
    Anotar "$Etapa - $Mensagem"
    try { [System.IO.File]::WriteAllText($Estado, "$Etapa|$Mensagem", $Texto) } catch { }
}

function ChaveMaquina([string]$Caminho, [switch]$Criar) {
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
    if ($Criar) { return $base.CreateSubKey($Caminho) }
    return $base.OpenSubKey($Caminho)
}

function VersaoDe([string]$Arquivo) {
    $texto = [string](Get-Item -LiteralPath $Arquivo).VersionInfo.ProductVersion
    $texto = ($texto -split '[+ ]')[0]
    return [version]$texto
}

# Pasta nova, com permissao so para SISTEMA e administradores (apaga o que houver no lugar, inclusive atalhos de pasta).
function PastaProtegida([string]$Caminho) {
    if (Test-Path -LiteralPath $Caminho) {
        $item = Get-Item -LiteralPath $Caminho -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { [IO.Directory]::Delete($Caminho, $false) }
        else { Remove-Item -LiteralPath $Caminho -Recurse -Force -Confirm:$false }
    }
    New-Item -ItemType Directory -Path $Caminho -Force | Out-Null
    $acl = New-Object System.Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in 'S-1-5-18', 'S-1-5-32-544') {
        $quem = New-Object System.Security.Principal.SecurityIdentifier($sid)
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($quem, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    }
    Set-Acl -LiteralPath $Caminho -AclObject $acl
}

function Baixar([string]$Endereco, [string]$Arquivo) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $Endereco -OutFile $Arquivo -UseBasicParsing -TimeoutSec 600
}

function FecharTerminal {
    foreach ($tentativa in 1..5) {
        $abertos = @(Get-Process -Name 'Pontual.Terminal.Shell' -ErrorAction SilentlyContinue)
        if ($abertos.Count -eq 0) { return }
        $abertos | Stop-Process -Force -Confirm:$false -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 800
    }
}

# Move uma pasta com algumas tentativas (o Windows pode demorar a soltar os arquivos de um programa fechado).
function Mover([string]$De, [string]$Para) {
    foreach ($tentativa in 1..10) {
        try { [IO.Directory]::Move($De, $Para); return }
        catch { if ($tentativa -eq 10) { throw }; Start-Sleep -Seconds 1 }
    }
}

function Reiniciar {
    Estado 'reiniciando' 'O computador vai reiniciar agora.'
    Start-Process -FilePath (Join-Path $env:WINDIR 'System32\shutdown.exe') -ArgumentList '/r', '/t', '5', '/f', '/d', 'p:2:4' -WindowStyle Hidden
}

$fechou = $false
try {
    Anotar '----- pedido de atualizacao'
    Estado 'verificando' 'Procurando a versão nova na Central do TI...'

    # 1. Endereco da Central
    if (-not (Test-Path -LiteralPath $Pedido)) { throw 'O pedido de atualização não foi encontrado. Clique em Atualizar de novo.' }
    $central = ([System.IO.File]::ReadAllText($Pedido)).Trim().TrimEnd('/')
    if ($central -notmatch '^https?://[A-Za-z0-9.\-]+(:\d{1,5})?$') { throw "Endereço da Central inválido: $central" }

    # 2. Chave publica e instalacao atual
    $chavePublica = Join-Path $Origem 'chave-publica.xml'
    if (-not (Test-Path -LiteralPath $chavePublica)) { throw 'Este computador não tem a chave de atualização. Instale a versão nova uma vez pelo instalador.' }
    if (-not (Test-Path -LiteralPath $Exe)) { throw "O Terminal não está em $Destino." }
    $versaoAtual = VersaoDe $Exe

    PastaProtegida $Trabalho
    $manifestoArquivo = Join-Path $Trabalho 'versao.json'
    try { Baixar "$central/baixar/atualizacao/versao.json" $manifestoArquivo }
    catch { throw "Não foi possível falar com a Central do TI ($central). Confira a conexão e tente de novo." }
    $manifesto = [System.IO.File]::ReadAllText($manifestoArquivo) | ConvertFrom-Json
    $versaoNova = [version][string]$manifesto.versao
    $hash = ([string]$manifesto.sha256).ToUpperInvariant()
    if ($hash -notmatch '^[0-9A-F]{64}$') { throw 'A Central publicou uma atualização incompleta (sem o código de conferência).' }

    $rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider
    $rsa.FromXmlString([System.IO.File]::ReadAllText($chavePublica))
    $assinado = $Texto.GetBytes("Terminal Pontual|$versaoNova|$hash")
    $assinatura = [Convert]::FromBase64String([string]$manifesto.assinatura)
    if (-not $rsa.VerifyData($assinado, 'SHA256', $assinatura)) { throw 'A assinatura da atualização não confere. Nada foi instalado. Avise o TI.' }

    if ($versaoNova -le $versaoAtual) {
        Estado 'atual' "Este computador já está na versão mais recente ($versaoAtual)."
        exit 0
    }

    # 3. Pacote
    Estado 'baixando' "Baixando a versão $versaoNova..."
    $pacote = Join-Path $Trabalho 'terminal.zip'
    Baixar "$central/baixar/atualizacao/terminal.zip" $pacote
    if ((Get-FileHash -LiteralPath $pacote -Algorithm SHA256).Hash -ne $hash) { throw 'O arquivo baixado veio diferente do publicado. Nada foi instalado. Tente de novo.' }

    $novo = Join-Path $Trabalho 'novo'
    Expand-Archive -LiteralPath $pacote -DestinationPath $novo -Force
    $exeNovo = Join-Path $novo 'Pontual.Terminal.Shell.exe'
    if (-not (Test-Path -LiteralPath $exeNovo)) { throw 'O pacote da atualização não tem o programa.' }
    if ((VersaoDe $exeNovo) -ne $versaoNova) { throw 'A versão do programa no pacote não é a anunciada. Nada foi instalado.' }
    foreach ($arquivo in 'Bloqueio-Comum.ps1', 'Remover-Bloqueio.ps1', 'Atualizar-Terminal.ps1', 'chave-publica.xml') {
        if (-not (Test-Path -LiteralPath (Join-Path $novo $arquivo))) { throw "O pacote da atualização está incompleto (falta $arquivo)." }
    }

    # A lista de sites que o TI editou neste computador continua valendo.
    $config = Join-Path $Destino 'config-local.json'
    if (Test-Path -LiteralPath $config) { Copy-Item -LiteralPath $config -Destination (Join-Path $novo 'config-local.json') -Force }

    # 4. Troca
    Estado 'instalando' "Instalando a versão $versaoNova. O Terminal vai fechar e o computador vai reiniciar."
    Start-Sleep -Seconds 3
    $fechou = $true
    FecharTerminal
    if (Test-Path -LiteralPath $Anterior) { Remove-Item -LiteralPath $Anterior -Recurse -Force -Confirm:$false }
    Mover $Destino $Anterior
    try { Mover $novo $Destino }
    catch {
        Anotar ('ERRO na troca: ' + $_.Exception.Message + ' - voltando a versao anterior')
        if (Test-Path -LiteralPath $Destino) { Remove-Item -LiteralPath $Destino -Recurse -Force -Confirm:$false -ErrorAction SilentlyContinue }
        Mover $Anterior $Destino
        throw 'Não foi possível trocar os arquivos. O computador vai reiniciar na versão anterior.'
    }

    # A pasta movida trouxe as permissoes da pasta de trabalho; volta a herdar as de "Arquivos de Programas".
    & icacls.exe "$Destino" /reset /T /C /Q | Out-Null

    # 5. Regras do bloqueio da versao nova e anotacoes do Windows (Aplicativos instalados).
    $marca = ChaveMaquina $ChaveMarca -Criar
    try {
        $sid = [string]$marca.GetValue('Sid')
        $programas = @(([string]$marca.GetValue('Programas', '')).Split(',') | Where-Object { $_ })
        $marca.SetValue('Versao', [string]$versaoNova)
    }
    finally { $marca.Close() }
    $remocao = ChaveMaquina $ChaveRemocao
    if ($remocao) {
        $remocao.Close()
        $remocao = ChaveMaquina $ChaveRemocao -Criar
        try { $remocao.SetValue('DisplayVersion', [string]$versaoNova) } finally { $remocao.Close() }
    }

    if ($sid) {
        try {
            . (Join-Path $Destino 'Bloqueio-Comum.ps1')
            # Rodando como SISTEMA ninguem ve janelas: os avisos vao para o historico.
            function Mensagem([string]$Texto, [string]$Icone = 'Information', [string]$Botoes = 'OK') { Anotar "aviso: $Texto"; return 'OK' }
            $comando = '"' + $Exe + '" --bloqueado'
            ComRegistroDaConta $sid { param($raiz) GravarBloqueio $raiz $comando $programas $sid }
            Anotar 'regras do bloqueio reaplicadas'
        }
        catch { Anotar ('aviso: regras do bloqueio nao reaplicadas: ' + $_.Exception.Message) }
    }

    Remove-Item -LiteralPath $Anterior -Recurse -Force -Confirm:$false -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $Pedido -Force -Confirm:$false -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $Trabalho -Recurse -Force -Confirm:$false -ErrorAction SilentlyContinue
    Anotar "atualizado de $versaoAtual para $versaoNova"
    Reiniciar
}
catch {
    Estado 'erro' $_.Exception.Message
    # O Terminal ja tinha sido fechado: reinicia para a atendente voltar a trabalhar.
    if ($fechou) { Reiniciar }
    exit 1
}
