# Instalador do Terminal Pontual 2.0 (com bloqueio). Precisa de administrador.
#
# O que ele faz:
#   1. copia o programa para "Arquivos de Programas\Terminal Pontual";
#   2. cria UMA conta comum do Windows para a atendente (sem poder de administrador);
#   3. configura essa conta para abrir o Terminal no lugar da area de trabalho e para
#      nao abrir outros programas.
# As outras contas do computador nao sao alteradas. Tudo e desfeito por Remover-Bloqueio.ps1.
# O gerador regrava este arquivo com BOM, para o PowerShell 5.1 ler os acentos.

$ErrorActionPreference = 'Stop'
$Origem = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $Origem 'Bloqueio-Comum.ps1')
GarantirAdmin $MyInvocation.MyCommand.Path

$Pacote = Join-Path $Origem 'app.zip'
$PadraoDeConta = '^[A-Za-z0-9][A-Za-z0-9 ._-]{0,19}$'
$PadraoDePrograma = '^[A-Za-z0-9 ._-]+\.exe$'

function Atalho([string]$Caminho, [string]$Alvo, [string]$Argumentos = '', [string]$Descricao = '') {
    $shell = New-Object -ComObject WScript.Shell
    $atalho = $shell.CreateShortcut($Caminho)
    $atalho.TargetPath = $Alvo
    $atalho.Arguments = $Argumentos
    $atalho.WorkingDirectory = Split-Path -Parent $Alvo
    $atalho.Description = $Descricao
    $atalho.Save()
}

function WebView2Instalado {
    $chave = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
    $locais = @(
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\$chave",
        "HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\$chave"
    )
    foreach ($local in $locais) {
        $versao = (Get-ItemProperty -Path $local -Name pv -ErrorAction SilentlyContinue).pv
        if ($versao -and $versao -ne '0.0.0.0') { return $true }
    }
    return $false
}

# Janela que pergunta o nome da conta, a senha e os programas extras. Devolve $null se for cancelada.
function PedirDados([string]$Conta, [string]$Programas) {
    $fonte = New-Object System.Drawing.Font('Segoe UI', 10)
    $janela = New-Object System.Windows.Forms.Form
    $janela.Text = "Instalação do $Nome 2.0"
    $janela.Font = $fonte
    $janela.ClientSize = New-Object System.Drawing.Size(500, 418)
    $janela.FormBorderStyle = 'FixedDialog'
    $janela.StartPosition = 'CenterScreen'
    $janela.MaximizeBox = $false
    $janela.MinimizeBox = $false
    $janela.TopMost = $true

    function Rotulo([string]$Texto, [int]$Y, [int]$Altura = 20) {
        $rotulo = New-Object System.Windows.Forms.Label
        $rotulo.Text = $Texto
        $rotulo.SetBounds(20, $Y, 460, $Altura)
        $janela.Controls.Add($rotulo)
    }
    function Campo([int]$Y, [string]$Texto = '', [bool]$Senha = $false) {
        $campo = New-Object System.Windows.Forms.TextBox
        $campo.Text = $Texto
        $campo.SetBounds(20, $Y, 460, 26)
        if ($Senha) { $campo.UseSystemPasswordChar = $true }
        $janela.Controls.Add($campo)
        return $campo
    }

    Rotulo 'Será criada uma conta do Windows só para a atendente. Ao entrar nela, o computador abre direto o Terminal Pontual, sem área de trabalho. As outras contas deste computador não mudam.' 14 62
    Rotulo 'Nome da conta da atendente' 84
    $campoConta = Campo 106 $Conta
    Rotulo 'Senha da conta (pode ficar em branco)' 142
    $campoSenha = Campo 164 '' $true
    Rotulo 'Repita a senha' 200
    $campoConfirma = Campo 222 '' $true
    Rotulo 'Outros programas que podem abrir arquivos (opcional). PDF e planilhas já estão liberados. Informe o nome do arquivo do programa, separado por vírgula. Exemplo: WINWORD.EXE' 258 44
    $campoProgramas = Campo 306 $Programas

    $ok = New-Object System.Windows.Forms.Button
    $ok.Text = 'Instalar'
    $ok.SetBounds(270, 366, 100, 34)
    $ok.DialogResult = 'OK'
    $cancelar = New-Object System.Windows.Forms.Button
    $cancelar.Text = 'Cancelar'
    $cancelar.SetBounds(380, 366, 100, 34)
    $cancelar.DialogResult = 'Cancel'
    $janela.Controls.AddRange(@($ok, $cancelar))
    $janela.AcceptButton = $ok
    $janela.CancelButton = $cancelar

    if ($janela.ShowDialog() -ne 'OK') { return $null }
    return @{ Conta = $campoConta.Text.Trim(); Senha = $campoSenha.Text; Confirma = $campoConfirma.Text; Programas = $campoProgramas.Text }
}

function LerProgramas([string]$Texto) {
    return @($Texto -split '[,;\r\n]+' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

function CriarConta([string]$Conta, [string]$Senha) {
    $computador = [ADSI]"WinNT://$env:COMPUTERNAME,computer"
    $usuario = $computador.Create('user', $Conta)
    $usuario.SetPassword($Senha)
    $usuario.SetInfo()
    $usuario.Put('FullName', $Conta)
    $usuario.Put('Description', 'Conta de atendimento, bloqueada pelo Terminal Pontual')
    # 0x10000: a senha nao expira (a atendente nao teria onde troca-la).
    $usuario.Put('UserFlags', ([int]$usuario.UserFlags.Value) -bor 0x10000)
    $usuario.SetInfo()
    # Grupo "Usuarios" (S-1-5-32-545): conta comum, sem poder de administrador. O nome do grupo muda com o idioma do Windows.
    $grupo = [ADSI]"WinNT://$env:COMPUTERNAME/$(NomeDoGrupo 'S-1-5-32-545'),group"
    try { $grupo.Add("WinNT://$env:COMPUTERNAME/$Conta,user") } catch { }
}

# Cria a pasta de usuario da conta (o Windows so criaria na primeira entrada), para poder configura-la antes.
function CriarPerfil([string]$Sid, [string]$Conta) {
    if (-not ('Pontual.Perfil' -as [type])) {
        Add-Type -Namespace Pontual -Name Perfil -MemberDefinition '[DllImport("userenv.dll", CharSet = CharSet.Unicode)] public static extern int CreateProfile(string pszUserSid, string pszUserName, System.Text.StringBuilder pszProfilePath, uint cchProfilePath);'
    }
    $caminho = New-Object System.Text.StringBuilder(260)
    $resultado = [Pontual.Perfil]::CreateProfile($Sid, $Conta, $caminho, 260)
    # 0x800700B7: a pasta ja existe (reinstalacao).
    if ($resultado -ne 0 -and $resultado -ne -2147024713) {
        throw ('O Windows não conseguiu preparar a pasta da conta (código 0x{0:X8}).' -f $resultado)
    }
}

$contaCriadaAgora = $false
$conta = $null
$sid = $null
try {
    if (-not (Test-Path $Pacote)) { throw 'O arquivo do programa (app.zip) não foi encontrado ao lado do instalador.' }

    $anterior = LerMarca
    $programas = @()
    $senha = ''
    if ($anterior -and (SidDaConta $anterior.Conta)) {
        # Reinstalacao ou atualizacao: a conta ja existe e e mantida.
        $resposta = Mensagem "O $Nome já está instalado neste computador, com a conta `"$($anterior.Conta)`".`n`nAtualizar o programa e manter essa conta?" 'Question' 'YesNo'
        if ($resposta -ne 'Yes') { exit 0 }
        $conta = $anterior.Conta
        $sid = SidDaConta $conta
        $programas = $anterior.Programas
        if (ContaAberta $sid) {
            Mensagem "A conta `"$conta`" está aberta neste computador.`n`nEntre nela, use `"Desligar ou sair`" > `"Sair da conta`" e rode este instalador de novo." 'Warning' | Out-Null
            exit 1
        }
    }
    else {
        $sugestao = 'Atendente'
        $textoProgramas = ''
        while ($true) {
            $dados = PedirDados $sugestao $textoProgramas
            if (-not $dados) { exit 0 }
            $sugestao = $dados.Conta
            $textoProgramas = $dados.Programas
            $programas = LerProgramas $dados.Programas
            $invalidos = @($programas | Where-Object { $_ -notmatch $PadraoDePrograma })

            if ($dados.Conta -notmatch $PadraoDeConta) { Mensagem 'O nome da conta deve ter de 1 a 20 caracteres: letras sem acento, números, espaço, ponto, hífen ou sublinhado, começando por letra ou número.' 'Warning' | Out-Null; continue }
            if (SidDaConta $dados.Conta) { Mensagem "Já existe uma conta ou um grupo chamado `"$($dados.Conta)`" neste computador.`n`nO bloqueio só é aplicado a uma conta nova, para não atingir quem já usa o computador. Escolha outro nome." 'Warning' | Out-Null; continue }
            if ($dados.Senha -cne $dados.Confirma) { Mensagem 'As duas senhas não são iguais.' 'Warning' | Out-Null; continue }
            if ($invalidos.Count -gt 0) { Mensagem "Nome de programa inválido: $($invalidos -join ', ').`n`nInforme só o nome do arquivo, terminado em .exe. Exemplo: EXCEL.EXE" 'Warning' | Out-Null; continue }
            break
        }
        $conta = $dados.Conta
        $senha = $dados.Senha
    }

    # 1. Programa
    FecharTerminal
    $configAntiga = $null
    $caminhoConfig = Join-Path $Destino 'config-local.json'
    if (Test-Path $caminhoConfig) { $configAntiga = Get-Content -Path $caminhoConfig -Raw -Encoding UTF8 }
    if (Test-Path $Destino) { Remove-Item -Path $Destino -Recurse -Force -Confirm:$false }
    New-Item -ItemType Directory -Force -Path $Destino | Out-Null
    Expand-Archive -Path $Pacote -DestinationPath $Destino -Force
    if (-not (Test-Path $Exe)) { throw "A cópia dos arquivos falhou: o programa não está em $Destino." }
    if ($configAntiga) { [System.IO.File]::WriteAllText($caminhoConfig, $configAntiga, (New-Object System.Text.UTF8Encoding($false))) }
    foreach ($script in 'Remover-Bloqueio.ps1', 'Bloqueio-Comum.ps1') { Copy-Item -Path (Join-Path $Origem $script) -Destination $Destino -Force }

    # 2. Componente da Microsoft de que o programa precisa
    $avisoWebView = ''
    if (-not (WebView2Instalado)) {
        $instaladorWebView = Join-Path $Origem 'MicrosoftEdgeWebview2Setup.exe'
        if (Test-Path $instaladorWebView) { Start-Process -FilePath $instaladorWebView -ArgumentList '/silent', '/install' -Wait }
        if (-not (WebView2Instalado)) {
            $avisoWebView = "`n`nATENÇÃO: o componente WebView2 Runtime, da Microsoft, não pôde ser instalado (é preciso internet). Sem ele o Terminal não abre os sites. Instale-o e reinicie o computador."
        }
    }

    # 3. Conta da atendente
    if (-not $sid) {
        # Marcado antes de criar: se a criacao parar no meio, o que sobrou da conta e apagado no fim.
        # (Mais acima ja foi conferido que nao existia conta com esse nome.)
        $contaCriadaAgora = $true
        try { CriarConta $conta $senha }
        catch { throw "O Windows não aceitou criar a conta `"$conta`". Se o computador exige senha forte, informe uma senha mais longa, com letras e números.`n`nDetalhe: $($_.Exception.Message)" }
        $sid = SidDaConta $conta
        if (-not $sid) { throw "A conta `"$conta`" foi criada, mas o Windows não a encontrou em seguida." }
    }
    CriarPerfil $sid $conta

    # 4. Bloqueio, gravado so no registro dessa conta
    $comando = '"' + $Exe + '" --bloqueado'
    ComRegistroDaConta $sid { param($raiz) GravarBloqueio $raiz $comando $programas }

    # 5. Anotacoes para a remocao
    $versao = (Get-Item $Exe).VersionInfo.ProductVersion
    $marca = ChaveMaquina $ChaveMarca -Criar
    try {
        $marca.SetValue('Conta', $conta)
        $marca.SetValue('Sid', $sid)
        if ($contaCriadaAgora -or ($anterior -and $anterior.ContaCriada)) { $marca.SetValue('ContaCriada', 1, [Microsoft.Win32.RegistryValueKind]::DWord) }
        $marca.SetValue('Programas', ($programas -join ','))
        $marca.SetValue('Pasta', $Destino)
        $marca.SetValue('Versao', [string]$versao)
    }
    finally { $marca.Close() }

    $remover = "`"$PowerShellExe`" -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$(Join-Path $Destino 'Remover-Bloqueio.ps1')`""
    $remocao = ChaveMaquina $ChaveRemocao -Criar
    try {
        $remocao.SetValue('DisplayName', "$Nome (com bloqueio)")
        $remocao.SetValue('DisplayVersion', [string]$versao)
        $remocao.SetValue('Publisher', 'Pontual Consig')
        $remocao.SetValue('InstallLocation', $Destino)
        $remocao.SetValue('DisplayIcon', $Exe)
        $remocao.SetValue('UninstallString', $remover)
        $remocao.SetValue('NoModify', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
        $remocao.SetValue('NoRepair', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
    }
    finally { $remocao.Close() }

    $inicio = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) $Nome
    New-Item -ItemType Directory -Force -Path $inicio | Out-Null
    Atalho (Join-Path $inicio "Remover o $Nome (bloqueio).lnk") $PowerShellExe "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$(Join-Path $Destino 'Remover-Bloqueio.ps1')`"" 'Remove o Terminal Pontual e o bloqueio da conta da atendente'

    Mensagem ("Instalação concluída.`n`n" +
        "COMO USAR`n" +
        "1. Saia desta conta do Windows (ou reinicie o computador).`n" +
        "2. Na tela de entrada, escolha a conta `"$conta`".`n" +
        "3. Na primeira vez o Windows prepara a conta (1 a 2 minutos) e o Terminal pede o endereço da Central do TI e o código de cadastro.`n`n" +
        "PARA SAIR DA CONTA BLOQUEADA`n" +
        "Atalho Ctrl + Alt + Espaço + P, ou botão `"Desligar ou sair`" do Terminal, ou Ctrl + Alt + Del > Sair. Depois entre na sua conta de sempre.`n`n" +
        "PARA REMOVER`n" +
        "Entre numa conta de administrador > menu Iniciar > $Nome > Remover.$avisoWebView") | Out-Null
}
catch {
    # Nao deixa para tras uma conta criada pela metade.
    if ($contaCriadaAgora -and $conta) {
        try { ApagarConta $conta $sid } catch { }
    }
    Mensagem "A instalação não foi concluída.`n`n$($_.Exception.Message)" 'Error' | Out-Null
    exit 1
}
