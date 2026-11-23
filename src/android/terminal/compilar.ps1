# Compila o Terminal Pontual para Android (versão de teste) e gera o .apk em dist\.
# Não usa Gradle nem baixa nada: só o SDK do Android e o Java já instalados neste PC.
# Uso:  powershell -ExecutionPolicy Bypass -File compilar.ps1

$ErrorActionPreference = 'Stop'
$versao = '1.1.0'
$codigoVersao = 2
$minSdk = 24
$targetSdk = 34

$aqui = $PSScriptRoot
$raiz = (Resolve-Path (Join-Path $aqui '..\..\..')).Path
$saida = Join-Path $aqui 'build'
$apkFinal = Join-Path $raiz "dist\Terminal-Pontual-Android-$versao.apk"

function Falhar($mensagem) { Write-Host "ERRO: $mensagem" -ForegroundColor Red; exit 1 }
function Conferir($etapa) { if ($LASTEXITCODE -ne 0) { Falhar "A etapa '$etapa' falhou (codigo $LASTEXITCODE)." } }

# --- ferramentas
$sdk = $env:ANDROID_HOME
if (-not $sdk -or -not (Test-Path $sdk)) { $sdk = Join-Path $env:LOCALAPPDATA 'Android\Sdk' }
if (-not (Test-Path $sdk)) { Falhar "SDK do Android nao encontrado. Defina ANDROID_HOME." }

$ferramentas = Get-ChildItem (Join-Path $sdk 'build-tools') -Directory | Sort-Object { [version]($_.Name -replace '[^0-9.]', '') } | Select-Object -Last 1
$plataforma = Get-ChildItem (Join-Path $sdk 'platforms') -Directory | Sort-Object { [int]($_.Name -replace '\D', '') } | Select-Object -Last 1
if (-not $ferramentas -or -not $plataforma) { Falhar "Faltam 'build-tools' ou 'platforms' no SDK do Android." }
$androidJar = Join-Path $plataforma.FullName 'android.jar'
$aapt2 = Join-Path $ferramentas.FullName 'aapt2.exe'
$zipalign = Join-Path $ferramentas.FullName 'zipalign.exe'
$d8Jar = Join-Path $ferramentas.FullName 'lib\d8.jar'
$apksignerJar = Join-Path $ferramentas.FullName 'lib\apksigner.jar'

$java = 'java'; $javac = 'javac'; $keytool = 'keytool'
if ($env:JAVA_HOME -and (Test-Path (Join-Path $env:JAVA_HOME 'bin\javac.exe'))) {
    $java = Join-Path $env:JAVA_HOME 'bin\java.exe'
    $javac = Join-Path $env:JAVA_HOME 'bin\javac.exe'
    $keytool = Join-Path $env:JAVA_HOME 'bin\keytool.exe'
}

Write-Host "SDK: $($plataforma.Name), build-tools $($ferramentas.Name)"

# --- pasta de trabalho (só dentro de build\, que é descartável)
if (Test-Path $saida) { Remove-Item -Recurse -Force $saida -Confirm:$false }
New-Item -ItemType Directory -Force (Join-Path $saida 'gen'), (Join-Path $saida 'classes'), (Join-Path $saida 'dex') | Out-Null

# --- 1. recursos (ícones, textos) e manifesto
& $aapt2 compile --dir (Join-Path $aqui 'res') -o (Join-Path $saida 'res.zip')
Conferir 'recursos'
& $aapt2 link -o (Join-Path $saida 'base.apk') -I $androidJar --manifest (Join-Path $aqui 'AndroidManifest.xml') `
    --java (Join-Path $saida 'gen') --min-sdk-version $minSdk --target-sdk-version $targetSdk `
    --version-code $codigoVersao --version-name $versao (Join-Path $saida 'res.zip')
Conferir 'manifesto'

# --- 2. código Java
$fontes = @(Get-ChildItem (Join-Path $aqui 'java') -Recurse -Filter *.java) + @(Get-ChildItem (Join-Path $saida 'gen') -Recurse -Filter *.java)
$lista = Join-Path $saida 'fontes.txt'
[IO.File]::WriteAllLines($lista, ($fontes | ForEach-Object { '"' + ($_.FullName -replace '\\', '/') + '"' }))
& $javac -encoding UTF-8 -source 17 -target 17 -XDstringConcat=inline -Xlint:-options -nowarn `
    -classpath $androidJar -d (Join-Path $saida 'classes') "@$lista"
Conferir 'javac'

# --- 3. formato do Android (dex)
$classes = Get-ChildItem (Join-Path $saida 'classes') -Recurse -Filter *.class | ForEach-Object { $_.FullName }
& $java -cp $d8Jar com.android.tools.r8.D8 --release --lib $androidJar --min-api $minSdk --output (Join-Path $saida 'dex') $classes
Conferir 'd8'

# --- 4. junta tudo no .apk
$semAssinar = Join-Path $saida 'sem-assinar.apk'
Copy-Item (Join-Path $saida 'base.apk') $semAssinar
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::Open($semAssinar, [IO.Compression.ZipArchiveMode]::Update)
try {
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $saida 'dex\classes.dex'), 'classes.dex') | Out-Null
} finally { $zip.Dispose() }

$alinhado = Join-Path $saida 'alinhado.apk'
& $zipalign -f -p 4 $semAssinar $alinhado
Conferir 'zipalign'

# --- 5. assinatura de teste (a mesma chave "debug" que o Android Studio usa; não serve para produção)
$chave = Join-Path $env:USERPROFILE '.android\debug.keystore'
if (-not (Test-Path $chave)) {
    New-Item -ItemType Directory -Force (Split-Path $chave) | Out-Null
    & $keytool -genkeypair -keystore $chave -storepass android -keypass android -alias androiddebugkey `
        -keyalg RSA -keysize 2048 -validity 10000 -dname 'CN=Android Debug,O=Android,C=US'
    Conferir 'chave de teste'
}

New-Item -ItemType Directory -Force (Split-Path $apkFinal) | Out-Null
& $java -jar $apksignerJar sign --ks $chave --ks-pass pass:android --key-pass pass:android `
    --ks-key-alias androiddebugkey --out $apkFinal $alinhado
Conferir 'assinatura'
& $java -jar $apksignerJar verify $apkFinal
Conferir 'conferencia da assinatura'

$tamanho = [math]::Round((Get-Item $apkFinal).Length / 1KB)
Write-Host "Pronto: $apkFinal ($tamanho KB)" -ForegroundColor Green
