param([string]$cenario='', [string]$saida='foto.png', [int]$altura=900)
$d='F:\Projetos\Terminal-Pontual\.rascunho-central-neural\siape-teste'
$e="${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
Remove-Item "$d\$saida" -ErrorAction SilentlyContinue
& $e --headless --disable-gpu --no-first-run --window-size=1400,$altura --virtual-time-budget=4000 "--user-data-dir=$d\perfil" "--screenshot=$d\$saida" "file:///F:/Projetos/Terminal-Pontual/.rascunho-central-neural/siape-teste/teste.html?$cenario#siape" 2>$null | Out-Null
Start-Sleep 4
Test-Path "$d\$saida"
