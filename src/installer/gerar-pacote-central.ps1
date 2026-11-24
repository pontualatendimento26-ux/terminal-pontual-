# Publica a Central do TI e gera o pacote ASSINADO em dist\_central-nova (central.zip + versao.json).
# Depois dispare a tarefa:  schtasks /Run /TN "Atualizar a Central do TI"
# Usa a MESMA chave do Terminal (%APPDATA%\Pontual\chave-atualizacao-terminal.xml); o texto assinado
# comeca com "Central do TI|", entao um pacote do Terminal nunca vale como pacote da Central e vice-versa.
# A versao e automatica (data e hora: AAAA.MMDD.HHmm), sempre maior que a anterior.
# Uso: powershell -ExecutionPolicy Bypass -File src\installer\gerar-pacote-central.ps1

param([string]$Versao = '', [string]$Chave = '')

$ErrorActionPreference = 'Stop'
if (-not $Chave) { $Chave = Join-Path $env:APPDATA 'Pontual\chave-atualizacao-terminal.xml' }
if (-not $Versao) { $Versao = (Get-Date -Format 'yyyy.MMdd.HHmmss') }  # com segundos: a instalada já usa esse formato, e HHmm ficaria "menor"
$versaoAssinada = ([version]$Versao).ToString()
$Raiz = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$Saida = Join-Path $Raiz 'dist\_central-nova'
$Trabalho = Join-Path $Raiz ('dist_trabalho-' + 'pontual-central-' + [Guid]::NewGuid().ToString('N'))
$Publicado = Join-Path $Trabalho 'app'
$Dotnet = 'C:\Program Files\dotnet\dotnet.exe'
if (-not (Test-Path $Dotnet)) { $Dotnet = 'dotnet' }

if (-not (Test-Path $Chave)) { throw "Chave de assinatura nao encontrada em $Chave." }
$rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider
$rsa.FromXmlString([IO.File]::ReadAllText($Chave))
if ($rsa.PublicOnly) { throw "O arquivo $Chave nao tem a parte privada da chave." }

try {
    Write-Host '1/3 Publicando a Central...'
    & $Dotnet publish (Join-Path $Raiz 'src\server\Pontual.Server') -c Release -r win-x64 --self-contained true -o $Publicado --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'A publicacao falhou.' }
    if (-not (Test-Path (Join-Path $Publicado 'Pontual.Server.exe'))) { throw 'Pontual.Server.exe nao foi gerado.' }
    # O banco fica na Central em uso; nunca vai no pacote.
    Remove-Item -LiteralPath (Join-Path $Publicado 'data') -Recurse -Force -ErrorAction SilentlyContinue

    Write-Host '2/3 Compactando...'
    $zip = Join-Path $Trabalho 'central.zip'
    Compress-Archive -Path (Join-Path $Publicado '*') -DestinationPath $zip -CompressionLevel Optimal
    $hash = (Get-FileHash -Path $zip -Algorithm SHA256).Hash.ToUpperInvariant()

    Write-Host '3/3 Assinando...'
    $assinatura = [Convert]::ToBase64String($rsa.SignData([Text.Encoding]::UTF8.GetBytes("Central do TI|$versaoAssinada|$hash"), 'SHA256'))
    $manifesto = '{"versao":"' + $versaoAssinada + '","sha256":"' + $hash + '","assinatura":"' + $assinatura + '"}'

    if (Test-Path $Saida) { Remove-Item -LiteralPath $Saida -Recurse -Force -Confirm:$false }
    New-Item -ItemType Directory -Force -Path $Saida | Out-Null
    Copy-Item -LiteralPath $zip -Destination $Saida -Force
    # versao.json por ultimo: o atualizador so ve o pacote quando ele esta inteiro.
    [IO.File]::WriteAllText((Join-Path $Saida 'versao.json'), $manifesto, (New-Object Text.UTF8Encoding($false)))
}
finally { Remove-Item -LiteralPath $Trabalho -Recurse -Force -Confirm:$false -ErrorAction SilentlyContinue }

Write-Host ''
Write-Host "Pacote $versaoAssinada assinado em $Saida"
Write-Host 'Aplicar:  schtasks /Run /TN "Atualizar a Central do TI"   (registro em dist\_atualizacao.log)'
