# Move a Central do TI (que roda como SISTEMA) de C:\Projetos\Terminal-Pontual para F:\Projetos\Terminal-Pontual.
# Rode UMA VEZ, como administrador, fora do horario de atendimento:
#   powershell -ExecutionPolicy Bypass -File F:\Projetos\Terminal-Pontual\src\installer\Mover-Central-para-F.ps1
#
# O que faz:
#   1. para a Central (desativa a tarefa "Central do TI" e encerra Pontual.Server);
#   2. copia de seguranca da Central inteira (com o banco) em ProgramData\Pontual\Central\backup-mudanca-*;
#   3. espelha C:\...\dist para F:\...\dist (o F: recebe o banco atualizado);
#   4. aponta a tarefa "Central do TI" e as regras de firewall para o F:;
#   5. reinstala o atualizador a partir do F: (config.txt passa a apontar para F:\...\dist);
#   6. religa e confere http://localhost:5080/api/v1/health.
# Se algo falhar antes de a Central subir no F:, volta a tarefa para o C: e religa como estava.
# NAO apaga nada do C:. Apague C:\Projetos\Terminal-Pontual so depois de conferir a Central.

$ErrorActionPreference = 'Stop'
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { throw 'Rode como administrador.' }

$De = 'C:\Projetos\Terminal-Pontual'
$Para = 'F:\Projetos\Terminal-Pontual'
$Tarefa = 'Central do TI'
$Log = Join-Path $Para 'dist\_mudanca-para-F.log'

function Anotar([string]$m) {
    $l = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + ' ' + $m
    Write-Host $l
    try { Add-Content -LiteralPath $Log -Value $l -Encoding UTF8 } catch { }
}
function Trocar([string]$s) { if ($s) { $s -replace [regex]::Escape($De), $Para } else { $s } }
function NoAr {
    foreach ($i in 1..40) {
        try { Invoke-WebRequest -Uri 'http://localhost:5080/api/v1/health' -UseBasicParsing -TimeoutSec 3 | Out-Null; return $true }
        catch { Start-Sleep -Seconds 1 }
    }
    return $false
}

if (-not (Test-Path "$De\dist\Central-do-TI\data\pontual.db")) { throw "Banco nao encontrado em $De\dist\Central-do-TI\data." }
if (-not (Test-Path "$Para\src\installer\Instalar-Atualizador-Central.ps1")) { throw "Projeto nao encontrado em $Para." }

$t = Get-ScheduledTask -TaskName $Tarefa -ErrorAction SilentlyContinue
if (-not $t) { throw "Tarefa '$Tarefa' nao encontrada. Nada foi alterado." }
$acoesAntigas = @($t.Actions)
Anotar '===== mudanca da Central para o F:'
foreach ($a in $acoesAntigas) { Anotar "tarefa hoje: $($a.Execute) $($a.Arguments) (pasta: $($a.WorkingDirectory))" }

$regras = @(Get-NetFirewallApplicationFilter -ErrorAction SilentlyContinue | Where-Object { $_.Program -like "$De*" })
$tarefaTrocada = $false
$regrasTrocadas = @()

try {
    # 1. Parar
    Anotar '1/6 Parando a Central...'
    Disable-ScheduledTask -TaskName $Tarefa | Out-Null
    Stop-ScheduledTask -TaskName $Tarefa -ErrorAction SilentlyContinue
    Get-Process -Name 'Pontual.Server' -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 3
    if (Get-Process -Name 'Pontual.Server' -ErrorAction SilentlyContinue) { throw 'A Central nao parou.' }

    # 2. Copia de seguranca
    $copia = Join-Path $env:ProgramData ('Pontual\Central\backup-mudanca-' + (Get-Date -Format 'yyyy-MM-dd-HHmmss'))
    Anotar "2/6 Copia de seguranca em $copia ..."
    robocopy "$De\dist\Central-do-TI" $copia /E /XJ /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8 -or -not (Test-Path "$copia\data\pontual.db")) { throw 'A copia de seguranca falhou.' }

    # 3. Espelhar dist
    Anotar '3/6 Copiando dist para o F: ...'
    robocopy "$De\dist" "$Para\dist" /MIR /XJ /XF '_mudanca-para-F.log' /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Erro ao copiar dist (robocopy $LASTEXITCODE)." }
    $hC = (Get-FileHash "$De\dist\Central-do-TI\data\pontual.db").Hash
    $hF = (Get-FileHash "$Para\dist\Central-do-TI\data\pontual.db").Hash
    if ($hC -ne $hF) { throw 'O banco copiado nao confere com o original.' }
    Anotar 'banco conferido (SHA-256 igual)'

    # 4. Tarefa e firewall
    Anotar '4/6 Apontando a tarefa e o firewall para o F: ...'
    $novas = foreach ($a in $acoesAntigas) {
        $p = @{ Execute = (Trocar $a.Execute) }
        if ($a.Arguments) { $p.Argument = (Trocar $a.Arguments) }
        if ($a.WorkingDirectory) { $p.WorkingDirectory = (Trocar $a.WorkingDirectory) }
        New-ScheduledTaskAction @p
    }
    Set-ScheduledTask -TaskName $Tarefa -Action $novas | Out-Null
    $tarefaTrocada = $true
    foreach ($a in (Get-ScheduledTask -TaskName $Tarefa).Actions) { Anotar "tarefa agora: $($a.Execute) $($a.Arguments) (pasta: $($a.WorkingDirectory))" }
    foreach ($r in $regras) {
        $antigo = $r.Program
        $r | Set-NetFirewallApplicationFilter -Program (Trocar $antigo)
        $regrasTrocadas += ,@($r, $antigo)
        Anotar "firewall: $antigo -> $(Trocar $antigo)"
    }

    # 5. Atualizador
    Anotar '5/6 Reinstalando o atualizador a partir do F: ...'
    & "$Para\src\installer\Instalar-Atualizador-Central.ps1"

    # 6. Religar
    Anotar '6/6 Religando a Central...'
    Enable-ScheduledTask -TaskName $Tarefa | Out-Null
    Start-ScheduledTask -TaskName $Tarefa
    if (-not (NoAr)) { throw 'A Central nao respondeu a partir do F:.' }
    $exe = (Get-CimInstance Win32_Process -Filter "Name='Pontual.Server.exe'" | Select-Object -First 1).ExecutablePath
    Anotar "Central NO AR, rodando de: $exe"
    Anotar "PRONTO. Confira a Central; depois pode apagar $De."
}
catch {
    Anotar ('ERRO: ' + $_.Exception.Message)
    Anotar 'Voltando a Central para o C: ...'
    Stop-ScheduledTask -TaskName $Tarefa -ErrorAction SilentlyContinue
    Get-Process -Name 'Pontual.Server' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    if ($tarefaTrocada) { Set-ScheduledTask -TaskName $Tarefa -Action $acoesAntigas | Out-Null }
    foreach ($par in $regrasTrocadas) { $par[0] | Set-NetFirewallApplicationFilter -Program $par[1] }
    $cfg = Join-Path $(if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }) 'Pontual\Atualizador da Central\config.txt'
    if (Test-Path $cfg) { [IO.File]::WriteAllText($cfg, "$De\dist", [Text.Encoding]::ASCII) }
    Enable-ScheduledTask -TaskName $Tarefa | Out-Null
    Start-ScheduledTask -TaskName $Tarefa
    Anotar ('Central de volta no C:, no ar: ' + (NoAr))
    exit 1
}
