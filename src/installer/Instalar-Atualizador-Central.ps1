# Instala UMA VEZ (como administrador) o atualizador assinado da Central do TI.
# Uso (PowerShell como administrador):
#   powershell -ExecutionPolicy Bypass -File src\installer\Instalar-Atualizador-Central.ps1
#
# O que faz:
#   1. copia Atualizar-Central.ps1 e a chave PUBLICA para "Arquivos de Programas\Pontual\Atualizador da Central"
#      (usuarios comuns so leem);
#   2. protege F:Pontual-Central (trabalho e copias de seguranca: so SISTEMA/administradores);
#   3. protege dist\Central-do-TI (o programa que roda como SISTEMA): usuarios so leem.
#      Excecao: wwwroot\baixar continua gravavel, porque gerar-instalador.ps1 copia o instalador para la;
#   4. protege as copias de seguranca antigas dist\backup-central-* (tem o banco);
#   5. recria a tarefa "Atualizar a Central do TI" como SISTEMA: administradores mudam; quem instalou
#      (e os administradores) so dispara;
#   6. troca dist\Atualizar a Central.cmd por um que so dispara a tarefa, e apaga o antigo Atualizar-Central.ps1.

param([string]$Chave = '')

$ErrorActionPreference = 'Stop'
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { throw 'Rode como administrador.' }

if (-not $Chave) { $Chave = 'F:\Pontual-Chaves\chave-atualizacao-terminal.xml' }
$Raiz = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$Dist = Join-Path $Raiz 'dist'
$Viva = Join-Path $Dist 'Central-do-TI'
$ArquivosDeProgramas = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
$Destino = Join-Path $ArquivosDeProgramas 'Pontual\Atualizador da Central'
$Base = Join-Path (Split-Path -Qualifier $Dist) 'Pontual-Central'
$TarefaNome = 'Atualizar a Central do TI'
$Admins = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')
$Sistema = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-18')
$Usuarios = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-545')
$Autenticados = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-11')
$Eu = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value

function Regra($quem, $direito) {
    New-Object System.Security.AccessControl.FileSystemAccessRule($quem, $direito, 'ContainerInherit,ObjectInherit', 'None', 'Allow')
}
# Sem heranca; dono = administradores; SISTEMA/administradores tudo; usuarios opcionalmente leitura.
function Proteger([string]$Caminho, [switch]$UsuariosLeem) {
    $acl = New-Object System.Security.AccessControl.DirectorySecurity
    $acl.SetOwner($Admins)
    $acl.SetAccessRuleProtection($true, $false)
    $acl.AddAccessRule((Regra $Sistema 'FullControl'))
    $acl.AddAccessRule((Regra $Admins 'FullControl'))
    if ($UsuariosLeem) { $acl.AddAccessRule((Regra $Usuarios 'ReadAndExecute')) }
    Set-Acl -LiteralPath $Caminho -AclObject $acl
    # Filhos voltam a herdar (apaga permissoes explicitas que deixariam alguem gravar).
    & icacls.exe "$Caminho\*" /reset /T /C /Q | Out-Null
}

# 0. Chave publica a partir da chave de assinatura.
if (-not (Test-Path $Chave)) { throw "Chave de assinatura nao encontrada em $Chave." }
$rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider
$rsa.FromXmlString([IO.File]::ReadAllText($Chave))
if (-not (Test-Path (Join-Path $Viva 'Pontual.Server.exe'))) { throw "Central nao encontrada em $Viva." }

# 1. Atualizador
Write-Host '1/6 Instalando o atualizador...'
New-Item -ItemType Directory -Force -Path $Destino | Out-Null
Proteger $Destino -UsuariosLeem
$comBom = New-Object System.Text.UTF8Encoding($true)
[IO.File]::WriteAllText((Join-Path $Destino 'Atualizar-Central.ps1'), [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Atualizar-Central.ps1')), $comBom)
[IO.File]::WriteAllText((Join-Path $Destino 'chave-publica.xml'), $rsa.ToXmlString($false), [Text.Encoding]::ASCII)
[IO.File]::WriteAllText((Join-Path $Destino 'config.txt'), $Dist, [Text.Encoding]::ASCII)

# 2. Pasta de trabalho/copias
Write-Host '2/6 Protegendo F:Pontual-Central...'
New-Item -ItemType Directory -Force -Path $Base | Out-Null
if ((Get-Item -LiteralPath $Base -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "$Base e um atalho; recusado." }
Proteger $Base

# 3. Central em uso
Write-Host '3/6 Protegendo dist\Central-do-TI...'
Proteger $Viva -UsuariosLeem
$baixar = Join-Path $Viva 'wwwroot\baixar'
if (Test-Path $baixar) {
    $acl = Get-Acl -LiteralPath $baixar
    $acl.AddAccessRule((Regra $Autenticados 'Modify'))
    Set-Acl -LiteralPath $baixar -AclObject $acl
}

# 4. Copias antigas (tem o banco)
Write-Host '4/6 Protegendo copias de seguranca antigas...'
Get-ChildItem -LiteralPath $Dist -Directory -Filter 'backup-central-*' | ForEach-Object { Proteger $_.FullName }

# 5. Tarefa
Write-Host '5/6 Recriando a tarefa...'
$servico = New-Object -ComObject Schedule.Service
$servico.Connect()
$definicao = $servico.NewTask(0)
$definicao.RegistrationInfo.Description = 'Aplica o pacote ASSINADO de dist\_central-nova na Central do TI.'
$definicao.Settings.Enabled = $true
$definicao.Settings.AllowDemandStart = $true
$definicao.Settings.DisallowStartIfOnBatteries = $false
$definicao.Settings.StopIfGoingOnBatteries = $false
$definicao.Settings.ExecutionTimeLimit = 'PT30M'
$definicao.Settings.MultipleInstances = 2   # ignora novo pedido enquanto um roda
$definicao.Principal.RunLevel = 1
$acao = $definicao.Actions.Create(0)
$acao.Path = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$acao.Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$(Join-Path $Destino 'Atualizar-Central.ps1')`""
$acao.WorkingDirectory = $Destino
$permissoes = "D:P(A;;FA;;;BA)(A;;FA;;;SY)(A;;GRGX;;;$Eu)"
try { $servico.GetFolder('\').DeleteTask($TarefaNome, 0) } catch { }
$servico.GetFolder('\').RegisterTaskDefinition($TarefaNome, $definicao, 6, 'SYSTEM', $null, 5, $permissoes) | Out-Null

# 6. Atalho antigo
Write-Host '6/6 Trocando o atalho em dist...'
$cmd = "@echo off`r`nrem Aplica o pacote assinado de _central-nova (gere com src\installer\gerar-pacote-central.ps1).`r`ntitle Atualizar a Central do TI`r`nschtasks /Run /TN `"$TarefaNome`"`r`necho Registro: `"$Destino\atualizacao.log`"`r`npause`r`n"
[IO.File]::WriteAllText((Join-Path $Dist 'Atualizar a Central.cmd'), $cmd, [Text.Encoding]::ASCII)
Remove-Item -LiteralPath (Join-Path $Dist 'Atualizar-Central.ps1') -Force -ErrorAction SilentlyContinue

Write-Host ''
Write-Host 'PRONTO. Para publicar a Central daqui em diante:'
Write-Host '  powershell -ExecutionPolicy Bypass -File src\installer\gerar-pacote-central.ps1'
Write-Host "  schtasks /Run /TN `"$TarefaNome`""
Write-Host "Registro: $Destino\atualizacao.log"
