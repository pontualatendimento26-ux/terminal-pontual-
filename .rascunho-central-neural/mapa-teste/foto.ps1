param([string]$hash='', [string]$saida='foto.png')
$d='F:\Projetos\Terminal-Pontual\.rascunho-central-neural\mapa-teste'
$e="${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
Remove-Item "$d\$saida" -ErrorAction SilentlyContinue
& $e --headless --disable-gpu --no-first-run --window-size=1400,900 --virtual-time-budget=3000 "--user-data-dir=$d\perfil" "--screenshot=$d\$saida" "file:///F:/Projetos/Terminal-Pontual/.rascunho-central-neural/mapa-teste/real.html#$hash" 2>$null | Out-Null
Start-Sleep 4
Test-Path "$d\$saida"
