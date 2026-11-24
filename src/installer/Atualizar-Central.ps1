# Atualizacao da Central do TI com pacote ASSINADO (mesmo esquema do botao "Atualizar" do Terminal).
#
# Roda como SISTEMA, pela tarefa "Atualizar a Central do TI" que Instalar-Atualizador-Central.ps1 cria.
# Este arquivo e a chave publica ficam numa pasta so de SISTEMA/administradores, entao ninguem muda
# o que a tarefa executa. O pacote vem de dist\_central-nova (pasta gravavel), por isso:
#   1. copia central.zip e versao.json para uma pasta de trabalho so do SISTEMA ANTES de conferir;
#   2. confere a assinatura de "Central do TI|<versao>|<sha256>" com a chave publica instalada;
#   3. so aceita versao MAIOR que a instalada (anotada em HKLM\SOFTWARE\Pontual\Central);
#   4. confere o SHA-256 do zip e descompacta na pasta de trabalho;
#   5. para a Central, faz copia de seguranca protegida, copia a versao nova, religa e confere.
# Pacote sem assinatura valida e recusado e nada muda.

param([string]$Dist = '')

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$Origem = Split-Path -Parent $MyInvocation.MyCommand.Path
$Texto = New-Object System.Text.UTF8Encoding($false)

$Config = Join-Path $Origem 'config.txt'   # primeira linha: pasta dist
if (-not $Dist) { $Dist = ([IO.File]::ReadAllText($Config)).Trim() }
$Viva = Join-Path $Dist 'Central-do-TI'
$Nova = Join-Path $Dist '_central-nova'
# Registro, trabalho e copias de seguranca NUNCA na pasta gravavel (um atalho plantado ali faria o
# SISTEMA escrever onde o atacante quisesse). A pasta Base e protegida pelo instalador do atualizador.
$Log = Join-Path $Origem 'atualizacao.log'
$Base = Join-Path (Split-Path -Qualifier $Dist) 'Pontual-Central'
$Trabalho = Join-Path $Base 'trabalho'
$ChaveVersao = 'SOFTWARE\Pontual\Central'
$Tarefa = 'Central do TI'

function Anotar([string]$m) {
    try { [IO.File]::AppendAllText($Log, ((Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + ' ' + $m + "`r`n"), $Texto) } catch { }
}

# Pasta nova so para SISTEMA e administradores (apaga o que houver, inclusive atalhos de pasta).
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

function ChaveMaquina([switch]$Criar) {
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
    if ($Criar) { return $base.CreateSubKey($ChaveVersao) }
    return $base.OpenSubKey($ChaveVersao)
}

function Copiar([string]$De, [string]$Para) {
    # Nunca segue atalhos de pasta/arquivo deixados na pasta gravavel.
    $item = Get-Item -LiteralPath $De -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "$De e um atalho; recusado." }
    [IO.File]::Copy($De, $Para, $true)
}

$parou = $false
$temTarefa = $false
$falhou = $false
try {
    Anotar '===== atualizacao assinada da Central'

    # 1. Pacote para a pasta do SISTEMA.
    $chavePublica = Join-Path $Origem 'chave-publica.xml'
    if (-not (Test-Path -LiteralPath $chavePublica)) { throw 'Chave publica ausente; reinstale o atualizador.' }
    foreach ($a in 'central.zip', 'versao.json') {
        if (-not (Test-Path -LiteralPath (Join-Path $Nova $a))) { throw "Falta $a em _central-nova. Gere com gerar-pacote-central.ps1." }
    }
    PastaProtegida $Trabalho
    $zip = Join-Path $Trabalho 'central.zip'
    $manifestoArquivo = Join-Path $Trabalho 'versao.json'
    Copiar (Join-Path $Nova 'versao.json') $manifestoArquivo
    Copiar (Join-Path $Nova 'central.zip') $zip

    # 2. Assinatura.
    $manifesto = [IO.File]::ReadAllText($manifestoArquivo) | ConvertFrom-Json
    $versaoNova = [version][string]$manifesto.versao
    $hash = ([string]$manifesto.sha256).ToUpperInvariant()
    if ($hash -notmatch '^[0-9A-F]{64}$') { throw 'Manifesto sem SHA-256 valido.' }
    $rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider
    $rsa.FromXmlString([IO.File]::ReadAllText($chavePublica))
    $assinatura = [Convert]::FromBase64String([string]$manifesto.assinatura)
    if (-not $rsa.VerifyData($Texto.GetBytes("Central do TI|$versaoNova|$hash"), 'SHA256', $assinatura)) {
        throw 'ASSINATURA INVALIDA. Nada foi alterado.'
    }

    # 3. Nao volta versao.
    $marca = ChaveMaquina
    $versaoAtual = [version]'0.0'
    if ($marca) { try { $v = [string]$marca.GetValue('Versao', ''); if ($v) { $versaoAtual = [version]$v } } finally { $marca.Close() } }
    if ($versaoNova -le $versaoAtual) { throw "Versao $versaoNova nao e maior que a instalada ($versaoAtual). Nada foi alterado." }

    # 4. Conteudo.
    if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $hash) { throw 'O zip nao confere com o SHA-256 assinado.' }
    $novo = Join-Path $Trabalho 'novo'
    Expand-Archive -LiteralPath $zip -DestinationPath $novo -Force
    if (-not (Test-Path -LiteralPath (Join-Path $novo 'Pontual.Server.exe'))) { throw 'O pacote nao tem Pontual.Server.exe.' }
    if (-not (Test-Path -LiteralPath (Join-Path $Viva 'data\pontual.db'))) { throw 'Banco de dados nao encontrado em Central-do-TI\data.' }
    Anotar "assinatura ok, versao $versaoAtual -> $versaoNova"

    # 5. Parar (tarefa desativada antes, senao o Windows religa no meio da copia).
    schtasks /Query /TN $Tarefa 2>&1 | Out-Null
    $temTarefa = ($LASTEXITCODE -eq 0)
    if ($temTarefa) { schtasks /Change /TN $Tarefa /DISABLE 2>&1 | Out-Null; schtasks /End /TN $Tarefa 2>&1 | Out-Null }
    $parou = $true
    Get-Process -Name 'Pontual.Server' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 3
    if (Get-Process -Name 'Pontual.Server' -ErrorAction SilentlyContinue) { throw 'O servidor nao parou. Nada foi alterado.' }

    # Copia de seguranca (tem o banco): pasta protegida.
    $copia = Join-Path $Base ('backup-central-' + (Get-Date -Format 'yyyy-MM-dd-HHmmss'))
    PastaProtegida $copia
    robocopy $Viva $copia /E /XJ /R:2 /W:2 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8 -or -not (Test-Path -LiteralPath (Join-Path $copia 'data\pontual.db'))) { throw 'A copia de seguranca falhou. A versao antiga foi mantida.' }
    Anotar "copia de seguranca em $copia"

    robocopy $novo $Viva /E /XJ /R:2 /W:2 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Erro ao copiar a versao nova (robocopy $LASTEXITCODE). Restaure $copia." }

    $marca = ChaveMaquina -Criar
    try { $marca.SetValue('Versao', [string]$versaoNova) } finally { $marca.Close() }
    Anotar 'arquivos da versao nova copiados'
}
catch {
    Anotar ('ERRO: ' + $_.Exception.Message)
    $falhou = $true
}
finally {
    if ($parou) {
        if ($temTarefa) { schtasks /Change /TN $Tarefa /ENABLE 2>&1 | Out-Null; schtasks /Run /TN $Tarefa 2>&1 | Out-Null }
        $noAr = $false; $neural = $false
        foreach ($i in 1..30) {
            try {
                Invoke-WebRequest -Uri 'http://localhost:5080/api/v1/health' -UseBasicParsing -TimeoutSec 3 | Out-Null
                $noAr = $true
                try { Invoke-WebRequest -Uri 'http://localhost:5080/central/neural/' -UseBasicParsing -TimeoutSec 3 | Out-Null; $neural = $true } catch { }
                break
            } catch { Start-Sleep -Seconds 1 }
        }
        Anotar ('Central no ar: ' + $noAr + ' | Central Neural: ' + $neural)
    }
    Remove-Item -LiteralPath $Trabalho -Recurse -Force -Confirm:$false -ErrorAction SilentlyContinue
}
if ($falhou) { exit 1 }
Anotar 'PRONTO'
exit 0
