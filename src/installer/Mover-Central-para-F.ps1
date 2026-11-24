# UMA VEZ, como administrador: passa a pasta de trabalho e as copias de seguranca da Central do C:
# para F:\Pontual-Central (so SISTEMA/administradores) e reinstala o atualizador.
# Cada copia so sai do C: depois de copiada; nada mais e apagado.
$raiz = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Start-Transcript -LiteralPath (Join-Path $raiz 'dist\_mover-central.log') -Force | Out-Null
$base = 'F:\Pontual-Central'
$antiga = Join-Path $env:ProgramData 'Pontual\Central'
$env:TEMP = Join-Path $raiz 'dist'; $env:TMP = $env:TEMP
try {
    New-Item -ItemType Directory -Force -Path $base -ErrorAction Stop | Out-Null
    if ((Get-Item -LiteralPath $base -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "$base e um atalho; recusado." }
    icacls $base /setowner '*S-1-5-32-544' /Q
    icacls $base /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' /Q
    if ($LASTEXITCODE -ne 0) { throw 'Nao foi possivel proteger a pasta do F:.' }
    Get-ChildItem -LiteralPath $antiga -Directory -Force | ForEach-Object {
        robocopy $_.FullName (Join-Path $base $_.Name) /E /MOVE /XJ /R:1 /W:1 /NFL /NDL /NJH /NJS /NP
        "movida $($_.Name): robocopy $LASTEXITCODE"
    }
    & (Join-Path $PSScriptRoot 'Instalar-Atualizador-Central.ps1')
} catch { 'ERRO: ' + $_.Exception.Message }
'C: livre ' + (Get-PSDrive C).Free
'FIM'
Stop-Transcript | Out-Null
