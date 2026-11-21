# Instala o Pontual Tools (ler PDF com OCR, compactar PDF, converter JPG <-> PDF) neste computador,
# para o botao "PDF" do Terminal e para a Central Neural ler PDF escaneado.
# Rodar como ADMINISTRADOR, uma vez por PC (e de novo para atualizar).
#
#   powershell -ExecutionPolicy Bypass -File Instalar-PontualTools.ps1 -Origem "\\servidor\pasta\pontual-tools"
#
# -Origem: pasta do projeto pontual-tools (com backend\ e frontend\dist\). Padrao: pasta "pontual-tools" ao lado deste script.
# O servico escuta so em 127.0.0.1:8787 (nada fica acessivel pela rede) e liga sozinho quando alguem entra no Windows.

param(
    [string]$Origem = (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'pontual-tools')
)

$ErrorActionPreference = 'Stop'
$Destino = Join-Path $env:ProgramFiles 'Pontual\Tools'
$Tarefa = 'Pontual Tools'

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { throw 'Rode este script como administrador.' }
if (-not (Test-Path (Join-Path $Origem 'backend\app\main.py'))) { throw "Nao achei o Pontual Tools em: $Origem" }
if (-not (Test-Path (Join-Path $Origem 'frontend\dist\index.html'))) {
    throw "Falta a interface pronta (frontend\dist). No PC de desenvolvimento rode: cd frontend; npx vite build"
}

# 1. Python (3.10+). Sem ele, instala para todos os usuarios pelo winget.
function Find-PontualPython {
    foreach ($c in @('py -3', 'python')) {
        try {
            $exe = & ([scriptblock]::Create("$c -c `"import sys; print(sys.executable) if sys.version_info >= (3,10) else None`"")) 2>$null
            if ($LASTEXITCODE -eq 0 -and $exe -and (Test-Path $exe.Trim())) { return $exe.Trim() }
        } catch { }
    }
    return $null
}
$python = Find-PontualPython
if (-not $python) {
    Write-Host 'Instalando o Python...'
    winget install --id Python.Python.3.13 --scope machine --silent --accept-package-agreements --accept-source-agreements | Out-Host
    $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
    $python = Find-PontualPython
    if (-not $python) { throw 'Nao consegui instalar o Python. Instale o Python 3.13 (para todos os usuarios) e rode de novo.' }
}

# 2. Para o servico antigo (se houver) e copia os arquivos.
Stop-ScheduledTask -TaskName $Tarefa -ErrorAction SilentlyContinue
Get-NetTCPConnection -LocalPort 8787 -State Listen -ErrorAction SilentlyContinue |
    ForEach-Object { Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }

New-Item -ItemType Directory -Force (Join-Path $Destino 'backend') | Out-Null
New-Item -ItemType Directory -Force (Join-Path $Destino 'frontend') | Out-Null
foreach ($p in @('backend\app', 'frontend\dist')) {
    $alvo = Join-Path $Destino $p
    if (Test-Path $alvo) { Remove-Item -Recurse -Force $alvo }
    Copy-Item -Recurse (Join-Path $Origem $p) $alvo
}
Copy-Item (Join-Path $Origem 'backend\requirements.txt') (Join-Path $Destino 'backend\requirements.txt') -Force
$vendor = Join-Path $Origem 'backend\vendor'
if (Test-Path $vendor) { Copy-Item -Recurse -Force $vendor (Join-Path $Destino 'backend') }  # motor de IA de imagem, se existir

# 3. Ambiente Python proprio.
$venv = Join-Path $Destino 'backend\.venv'
if (-not (Test-Path (Join-Path $venv 'Scripts\python.exe'))) { & $python -m venv $venv }
$vpy = Join-Path $venv 'Scripts\python.exe'
& $vpy -m pip install --quiet --upgrade pip
& $vpy -m pip install --quiet -r (Join-Path $Destino 'backend\requirements.txt')
if ($LASTEXITCODE -ne 0) { throw 'Falha ao instalar as dependencias do Pontual Tools.' }

# 4. Tarefa: liga quando qualquer usuario entra no Windows, sem janela, e reinicia se cair.
$acao = New-ScheduledTaskAction -Execute (Join-Path $venv 'Scripts\pythonw.exe') `
    -Argument '-m uvicorn app.main:app --host 127.0.0.1 --port 8787' -WorkingDirectory (Join-Path $Destino 'backend')
$gatilho = New-ScheduledTaskTrigger -AtLogOn
$usuarios = New-ScheduledTaskPrincipal -GroupId 'S-1-5-32-545' -RunLevel Limited   # grupo Usuarios
$config = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 5 `
    -RestartInterval (New-TimeSpan -Minutes 1) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName $Tarefa -Action $acao -Trigger $gatilho -Principal $usuarios -Settings $config -Force | Out-Null

# Qualquer usuario pode ligar a tarefa (o Terminal usa isso se o servico tiver caido).
$svc = New-Object -ComObject 'Schedule.Service'; $svc.Connect()
$t = $svc.GetFolder('\').GetTask($Tarefa)
$t.SetSecurityDescriptor('D:(A;;FA;;;BA)(A;;FA;;;SY)(A;;GRGX;;;BU)', 0)

Start-ScheduledTask -TaskName $Tarefa
$ok = $false
for ($i = 0; $i -lt 30 -and -not $ok; $i++) {
    Start-Sleep -Seconds 1
    try { $h = Invoke-RestMethod 'http://127.0.0.1:8787/api/health' -TimeoutSec 2; $ok = $true } catch { }
}
if ($ok) {
    Write-Host "Pontual Tools instalado e rodando. OCR (PDF escaneado): $($h.ocr_available)"
} else {
    Write-Host 'Instalado, mas o servico ainda nao respondeu. Ele liga no proximo login; se nao ligar, veja a tarefa "Pontual Tools".'
}
