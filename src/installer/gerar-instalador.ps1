# Gera o instalador de teste do Terminal Pontual (um unico .exe).
# Uso: powershell -ExecutionPolicy Bypass -File src\installer\gerar-instalador.ps1
# Requer o .NET SDK. Usa o IExpress, que ja vem com o Windows.
# Este arquivo e mantido em ASCII para rodar em qualquer PowerShell.

#
# Com -Bloqueio gera a versao 2.0: instala para o computador todo (pede administrador),
# cria a conta da atendente e aplica o bloqueio. Tambem copia o instalador para a
# Central do TI, que passa a oferece-lo em http://<endereco da Central>:5080/baixar/
# Uso: powershell -ExecutionPolicy Bypass -File src\installer\gerar-instalador.ps1 -Bloqueio

#
# Atualizacao pelo botao "Atualizar" do Terminal (so com -Bloqueio): o pacote vai para a Central em
# /baixar/atualizacao/, ASSINADO com a chave guardada neste computador (fora do repositorio):
#   %APPDATA%\Pontual\chave-atualizacao-terminal.xml
# Na primeira vez a chave e criada. GUARDE UMA COPIA DELA: sem ela os computadores ja instalados
# recusam as atualizacoes e cada um tera de ser reinstalado pelo instalador.

param([string]$Versao = '', [switch]$Bloqueio, [string]$Chave = '')

if (-not $Versao) { $Versao = if ($Bloqueio) { '2.0.0' } else { '1.0.0' } }
if (-not $Chave) { $Chave = Join-Path $env:APPDATA 'Pontual\chave-atualizacao-terminal.xml' }
$Scripts = if ($Bloqueio) { 'Instalar-Bloqueio.ps1', 'Remover-Bloqueio.ps1', 'Bloqueio-Comum.ps1', 'Atualizar-Terminal.ps1' } else { 'Instalar.ps1', 'Desinstalar.ps1' }

$ErrorActionPreference = 'Stop'
$Raiz = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$Saida = Join-Path $Raiz 'dist'
$Trabalho = Join-Path $env:TEMP ("pontual-instalador-" + [Guid]::NewGuid().ToString('N'))
$Publicado = Join-Path $Trabalho 'app'
$Pacote = Join-Path $Trabalho 'pacote'
$Dotnet = 'C:\Program Files\dotnet\dotnet.exe'
if (-not (Test-Path $Dotnet)) { $Dotnet = 'dotnet' }

New-Item -ItemType Directory -Force -Path $Publicado, $Pacote, $Saida | Out-Null

Write-Host '1/5 Publicando o programa...'
& $Dotnet publish (Join-Path $Raiz 'src\terminal\Pontual.Terminal.Shell') -c Release -r win-x64 --self-contained true -o $Publicado -p:Version=$Versao --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'A publicacao falhou.' }
if (-not (Test-Path (Join-Path $Publicado 'Pontual.Terminal.Shell.exe'))) { throw 'O executavel nao foi gerado.' }
if (-not (Test-Path (Join-Path $Publicado 'config-local.json'))) { throw 'config-local.json nao esta na publicacao.' }
if (-not (Test-Path (Join-Path $Publicado 'terminal\index.html'))) { throw 'A tela da atendente nao esta na publicacao.' }

Write-Host '2/5 Compactando...'
Compress-Archive -Path (Join-Path $Publicado '*') -DestinationPath (Join-Path $Pacote 'app.zip') -CompressionLevel Optimal

Write-Host '3/5 Preparando os scripts...'
# O PowerShell 5.1 so le acentos corretamente em arquivos UTF-8 com BOM.
$comBom = New-Object System.Text.UTF8Encoding($true)
foreach ($script in $Scripts) {
    $texto = [System.IO.File]::ReadAllText((Join-Path $PSScriptRoot $script), [System.Text.Encoding]::UTF8)
    [System.IO.File]::WriteAllText((Join-Path $Pacote $script), $texto, $comBom)
}

# Pacote de atualizacao assinado (botao "Atualizar" do Terminal).
$Atualizacao = Join-Path $Trabalho 'atualizacao'
if ($Bloqueio) {
    Write-Host '3b/5 Assinando o pacote de atualizacao...'
    $rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider(3072)
    if (Test-Path $Chave) {
        $rsa.FromXmlString([System.IO.File]::ReadAllText($Chave))
        if ($rsa.PublicOnly) { throw "O arquivo $Chave nao tem a parte privada da chave." }
    } else {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Chave) | Out-Null
        [System.IO.File]::WriteAllText($Chave, $rsa.ToXmlString($true), [System.Text.Encoding]::ASCII)
        Write-Warning "Chave de atualizacao criada em $Chave. GUARDE UMA COPIA (pendrive ou cofre): sem ela as atualizacoes param de funcionar."
    }
    [System.IO.File]::WriteAllText((Join-Path $Pacote 'chave-publica.xml'), $rsa.ToXmlString($false), [System.Text.Encoding]::ASCII)

    # Conteudo: o programa publicado, os scripts do bloqueio e a chave publica (o mesmo que o instalador deixa na pasta).
    $conteudo = Join-Path $Trabalho 'atualizacao-conteudo'
    New-Item -ItemType Directory -Force -Path $conteudo, $Atualizacao | Out-Null
    Copy-Item -Path (Join-Path $Publicado '*') -Destination $conteudo -Recurse -Force
    foreach ($arquivo in 'Remover-Bloqueio.ps1', 'Bloqueio-Comum.ps1', 'Atualizar-Terminal.ps1', 'chave-publica.xml') {
        Copy-Item -Path (Join-Path $Pacote $arquivo) -Destination $conteudo -Force
    }
    $zip = Join-Path $Atualizacao 'terminal.zip'
    Compress-Archive -Path (Join-Path $conteudo '*') -DestinationPath $zip -CompressionLevel Optimal
    $hashZip = (Get-FileHash -Path $zip -Algorithm SHA256).Hash.ToUpperInvariant()
    $versaoAssinada = ([version]$Versao).ToString()
    $assinatura = [Convert]::ToBase64String($rsa.SignData([System.Text.Encoding]::UTF8.GetBytes("Terminal Pontual|$versaoAssinada|$hashZip"), 'SHA256'))
    $manifesto = '{"versao":"' + $versaoAssinada + '","sha256":"' + $hashZip + '","assinatura":"' + $assinatura + '"}'
    [System.IO.File]::WriteAllText((Join-Path $Atualizacao 'versao.json'), $manifesto, (New-Object System.Text.UTF8Encoding($false)))
}

Write-Host '4/5 Baixando o instalador do WebView2 Runtime (Microsoft)...'
$webview = Join-Path $Pacote 'MicrosoftEdgeWebview2Setup.exe'
try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $webview -UseBasicParsing
    $assinatura = Get-AuthenticodeSignature -FilePath $webview
    if ($assinatura.Status -ne 'Valid' -or $assinatura.SignerCertificate.Subject -notmatch 'Microsoft Corporation') {
        Remove-Item -Path $webview -Force -Confirm:$false
        throw 'O arquivo baixado nao tem assinatura valida da Microsoft.'
    }
} catch {
    Write-Warning ("WebView2 nao incluido: " + $_.Exception.Message)
    if (Test-Path $webview) { Remove-Item -Path $webview -Force -Confirm:$false }
}

Write-Host '5/5 Gerando o instalador...'
$arquivos = Get-ChildItem -Path $Pacote -File | Sort-Object Name
$alvo = Join-Path $Saida ("Instalar-Terminal-Pontual-" + $Versao + ".exe")
if (Test-Path $alvo) { Remove-Item -Path $alvo -Force -Confirm:$false }

# Formato classico do IExpress: os valores ficam em [Strings] e sao
# referenciados por %NOME%. Valores escritos direto nas opcoes falham.
$linhas = @(
    '[Version]', 'Class=IEXPRESS', 'SEDVersion=3',
    '[Options]', 'PackagePurpose=InstallApp', 'ShowInstallProgramWindow=1', 'HideExtractAnimation=1', 'UseLongFileName=1',
    'InsideCompressed=0', 'CAB_FixedSize=0', 'CAB_ResvCodeSigning=0', 'RebootMode=N',
    'InstallPrompt=%InstallPrompt%', 'DisplayLicense=%DisplayLicense%', 'FinishMessage=%FinishMessage%',
    'TargetName=%TargetName%', 'FriendlyName=%FriendlyName%', 'AppLaunched=%AppLaunched%',
    'PostInstallCmd=%PostInstallCmd%', 'AdminQuietInstCmd=%AdminQuietInstCmd%', 'UserQuietInstCmd=%UserQuietInstCmd%',
    'SourceFiles=SourceFiles',
    '[Strings]', 'InstallPrompt=', 'DisplayLicense=', 'FinishMessage=',
    ('TargetName=' + $alvo), 'FriendlyName=Terminal Pontual',
    ('AppLaunched=cmd.exe /d /c powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ' + $Scripts[0]),
    'PostInstallCmd=<None>', 'AdminQuietInstCmd=', 'UserQuietInstCmd='
)
$indice = 0
foreach ($arquivo in $arquivos) { $linhas += ('FILE' + $indice + '="' + $arquivo.Name + '"'); $indice += 1 }
$linhas += '[SourceFiles]', ('SourceFiles0=' + $Pacote + '\'), '[SourceFiles0]'
for ($i = 0; $i -lt $indice; $i += 1) { $linhas += ('%FILE' + $i + '%=') }
$sed = Join-Path $Trabalho 'instalador.sed'
[System.IO.File]::WriteAllLines($sed, $linhas, [System.Text.Encoding]::Default)

# O IExpress so funciona quando chamado a partir da pasta do arquivo de
# instrucoes, com o nome relativo; com caminho completo ele falha sem explicar.
$processo = Start-Process -FilePath (Join-Path $env:WINDIR 'System32\iexpress.exe') -ArgumentList '/N', '/Q', 'instalador.sed' -WorkingDirectory $Trabalho -Wait -PassThru
if (-not (Test-Path $alvo)) {
    Write-Host ('Arquivo de instrucoes mantido para diagnostico: ' + $sed)
    throw ('O IExpress nao gerou o instalador (codigo ' + $processo.ExitCode + ').')
}

$tamanho = [Math]::Round((Get-Item $alvo).Length / 1MB, 1)
$hash = (Get-FileHash -Path $alvo -Algorithm SHA256).Hash

# Versao com bloqueio: a Central do TI oferece o instalador por link (pagina /baixar/).
# O nome do arquivo na Central e fixo, para a pagina nao depender do numero da versao.
if ($Bloqueio) {
    $paginaOrigem = Join-Path $Raiz 'src\server\Pontual.Server\wwwroot\baixar'
    $pastaCentral = Join-Path $Saida 'Central-do-TI\wwwroot\baixar'
    if (Test-Path (Join-Path $Saida 'Central-do-TI\wwwroot')) {
        New-Item -ItemType Directory -Force -Path $pastaCentral | Out-Null
        Copy-Item -Path (Join-Path $paginaOrigem 'index.html') -Destination $pastaCentral -Force
        Copy-Item -Path $alvo -Destination (Join-Path $pastaCentral 'Instalar-Terminal-Pontual.exe') -Force
        # App do celular: usa o .apk mais novo que estiver em dist\ (gerado por src\android\terminal\compilar.ps1).
        $apk = Get-ChildItem -Path $Saida -Filter 'Terminal-Pontual-Android-*.apk' -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
        if ($apk) {
            Copy-Item -Path $apk.FullName -Destination (Join-Path $pastaCentral 'Terminal-Pontual-Android.apk') -Force
            Write-Host ('App do celular na Central: ' + $apk.Name)
        } else {
            Write-Warning 'Nenhum .apk em dist\; o app do celular nao foi copiado para a Central (rode src\android\terminal\compilar.ps1).'
        }
        Write-Host ('Link na Central: /baixar/  (' + $pastaCentral + ')')
        # Botao "Atualizar" dos Terminais: o pacote assinado. O versao.json vai por ultimo, para
        # nenhum Terminal ver a versao nova antes de o pacote estar inteiro no lugar.
        $pastaAtualizacao = Join-Path $pastaCentral 'atualizacao'
        New-Item -ItemType Directory -Force -Path $pastaAtualizacao | Out-Null
        Copy-Item -Path (Join-Path $Atualizacao 'terminal.zip') -Destination $pastaAtualizacao -Force
        Copy-Item -Path (Join-Path $Atualizacao 'versao.json') -Destination $pastaAtualizacao -Force
        Write-Host ('Atualizacao ' + $versaoAssinada + ' pronta para o botao Atualizar: /baixar/atualizacao/  (publique a Central para valer)')
    } else {
        Write-Warning 'A pasta dist\Central-do-TI nao existe; o instalador nao foi copiado para a Central.'
    }
}
Remove-Item -Path $Trabalho -Recurse -Force -Confirm:$false

Write-Host ''
Write-Host ('Instalador: ' + $alvo)
Write-Host ('Tamanho: ' + $tamanho + ' MB')
Write-Host ('SHA-256: ' + $hash)
Write-Host ('Arquivos no pacote: ' + (($arquivos | ForEach-Object { $_.Name }) -join ', '))
