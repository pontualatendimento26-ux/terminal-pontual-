# Funcoes comuns ao instalador e ao removedor do Terminal Pontual 2.0 (com bloqueio).
# Carregado com ". Bloqueio-Comum.ps1" por Instalar-Bloqueio.ps1 e Remover-Bloqueio.ps1.
#
# O bloqueio vale so para UMA conta do Windows (a da atendente). Tudo o que ele grava
# fica no registro dessa conta; as outras contas do computador nao sao tocadas.
# Funciona em PowerShell de 32 e de 64 bits (o instalador pode ser aberto por um programa de 32 bits).
# O gerador regrava este arquivo com BOM, para o PowerShell 5.1 ler os acentos.

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$Nome = 'Terminal Pontual'
$ArquivosDeProgramas = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
$Destino = Join-Path $ArquivosDeProgramas 'Terminal Pontual'
$Exe = Join-Path $Destino 'Pontual.Terminal.Shell.exe'
$PowerShellExe = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$ChaveMarca = 'SOFTWARE\Pontual\Terminal'
$ChaveRemocao = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\TerminalPontual'
$Montagem = 'PontualAtendente'

# Programas que a conta bloqueada consegue abrir pelo explorador de arquivos e pelas janelas de "abrir arquivo".
# O proprio Terminal abre os programas liberados na Central por outro caminho, que nao depende desta lista.
$ProgramasFixos = @(
    'Pontual.Terminal.Shell.exe', 'msedgewebview2.exe', 'explorer.exe', 'shutdown.exe',
    # PDF: o Edge (que vem no Windows, travado mais abaixo para so abrir arquivos do computador) ou o Adobe Reader.
    'msedge.exe', 'AcroRd32.exe', 'Acrobat.exe',
    # Planilhas: Excel ou LibreOffice, se estiverem instalados.
    'EXCEL.EXE', 'soffice.exe', 'soffice.bin', 'scalc.exe'
)

function Mensagem([string]$Texto, [string]$Icone = 'Information', [string]$Botoes = 'OK') {
    return [System.Windows.Forms.MessageBox]::Show($Texto, "$Nome 2.0", $Botoes, $Icone)
}

# Se nao for administrador, abre de novo pedindo a permissao (janela do Windows) e espera terminar.
function GarantirAdmin([string]$Script) {
    $identidade = [Security.Principal.WindowsIdentity]::GetCurrent()
    $ehAdmin = (New-Object Security.Principal.WindowsPrincipal($identidade)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if ($ehAdmin) { return }
    try {
        $argumentos = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$Script`""
        $processo = Start-Process -FilePath $PowerShellExe -ArgumentList $argumentos -Verb RunAs -Wait -PassThru
        exit $processo.ExitCode
    }
    catch {
        Mensagem "Esta operação precisa de um administrador do computador.`n`nAbra de novo e, na janela do Windows, escolha Sim (ou informe a senha de um administrador)." 'Warning' | Out-Null
        exit 1
    }
}

function ChaveMaquina([string]$Caminho, [switch]$Criar) {
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
    if ($Criar) { return $base.CreateSubKey($Caminho) }
    return $base.OpenSubKey($Caminho)
}

function ApagarChaveMaquina([string]$Caminho) {
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
    $base.DeleteSubKeyTree($Caminho, $false)
}

# O que uma instalacao anterior deixou anotado: conta, SID, se a conta foi criada pelo instalador e os programas extras.
function LerMarca {
    $chave = ChaveMaquina $ChaveMarca
    if (-not $chave) { return $null }
    try {
        $conta = [string]$chave.GetValue('Conta')
        if (-not $conta) { return $null }
        return @{
            Conta = $conta
            Sid = [string]$chave.GetValue('Sid')
            ContaCriada = ([int]$chave.GetValue('ContaCriada', 0)) -eq 1
            Programas = @(([string]$chave.GetValue('Programas', '')).Split(',') | Where-Object { $_ })
        }
    }
    finally { $chave.Close() }
}

function SidDaConta([string]$Conta) {
    try {
        $nt = New-Object Security.Principal.NTAccount($env:COMPUTERNAME, $Conta)
        return $nt.Translate([Security.Principal.SecurityIdentifier]).Value
    }
    catch { return $null }
}

function NomeDoGrupo([string]$Sid) {
    $nome = (New-Object Security.Principal.SecurityIdentifier($Sid)).Translate([Security.Principal.NTAccount]).Value
    return $nome.Substring($nome.IndexOf('\') + 1)
}

function ContaAberta([string]$Sid) {
    $chave = [Microsoft.Win32.Registry]::Users.OpenSubKey($Sid)
    if ($chave) { $chave.Close(); return $true }
    return $false
}

function PastaDoPerfil([string]$Sid) {
    $chave = ChaveMaquina "SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\$Sid"
    if (-not $chave) { return $null }
    try { return [string]$chave.GetValue('ProfileImagePath') } finally { $chave.Close() }
}

function RegExe([string]$Argumentos) {
    $processo = Start-Process -FilePath (Join-Path $env:WINDIR 'System32\reg.exe') -ArgumentList $Argumentos -Wait -PassThru -WindowStyle Hidden
    return $processo.ExitCode
}

# Executa $Acao com o registro da conta disponivel. $Acao recebe o nome da chave dentro de HKEY_USERS.
# Com a conta fechada, o arquivo NTUSER.DAT dela e carregado e depois liberado.
function ComRegistroDaConta([string]$Sid, [scriptblock]$Acao) {
    if (ContaAberta $Sid) { & $Acao $Sid; return }

    $perfil = PastaDoPerfil $Sid
    if (-not $perfil) { throw 'A conta ainda não tem uma pasta de usuário no Windows.' }
    $arquivo = Join-Path $perfil 'NTUSER.DAT'
    if (-not (Test-Path -LiteralPath $arquivo)) { throw "O arquivo de configuração da conta não foi encontrado em $perfil." }
    if ((RegExe "load `"HKU\$Montagem`" `"$arquivo`"") -ne 0) { throw 'Não foi possível abrir a configuração da conta (o arquivo pode estar em uso).' }
    try { & $Acao $Montagem }
    finally {
        # O Windows so libera o arquivo quando nenhuma chave dele esta aberta neste processo.
        $liberado = $false
        foreach ($tentativa in 1..6) {
            [GC]::Collect(); [GC]::WaitForPendingFinalizers()
            if ((RegExe "unload `"HKU\$Montagem`"") -eq 0) { $liberado = $true; break }
            Start-Sleep -Milliseconds 500
        }
        if (-not $liberado) {
            Mensagem "A configuração da conta ficou aberta pelo Windows.`n`nREINICIE o computador antes de entrar na conta da atendente." 'Warning' | Out-Null
        }
    }
}

# As politicas do bloqueio: chave (dentro do registro da conta), nome, valor.
function PoliticasDoBloqueio([string]$Comando) {
    $sistema = 'Software\Microsoft\Windows\CurrentVersion\Policies\System'
    $explorer = 'Software\Microsoft\Windows\CurrentVersion\Policies\Explorer'
    return @(
        # No lugar da area de trabalho, o Windows abre o Terminal (politica "Interface de usuario personalizada").
        @($sistema, 'Shell', $Comando),
        @('Software\Microsoft\Windows NT\CurrentVersion\Winlogon', 'Shell', $Comando),
        # Tela do Ctrl+Alt+Del: sem Gerenciador de Tarefas e sem troca de senha. Bloquear, trocar de usuario e sair continuam.
        @($sistema, 'DisableTaskMgr', 1),
        @($sistema, 'DisableChangePassword', 1),
        @($sistema, 'DisableRegistryTools', 1),
        @('Software\Policies\Microsoft\Windows\System', 'DisableCMD', 1),
        # Explorador de arquivos e janelas de "abrir arquivo": so abrem os programas da lista RestrictRun.
        @($explorer, 'RestrictRun', 1),
        @($explorer, 'NoRun', 1),
        @($explorer, 'NoWinKeys', 1),
        @($explorer, 'NoControlPanel', 1),
        @($explorer, 'NoDriveTypeAutoRun', 255),
        # O Edge fica liberado so para abrir PDF: qualquer endereco de internet e recusado, so arquivos do computador abrem.
        # (Os sites dos bancos abrem dentro do Terminal, que usa outro componente e nao e afetado.)
        @('Software\Policies\Microsoft\Edge', 'HideFirstRunExperience', 1),
        @('Software\Policies\Microsoft\Edge\URLBlocklist', '1', '*'),
        @('Software\Policies\Microsoft\Edge\URLAllowlist', '1', 'file://*'),
        # Excel: macros desligadas, porque uma macro consegue abrir outros programas.
        @('Software\Policies\Microsoft\Office\16.0\Excel\Security', 'VBAWarnings', 4)
    )
}

$ChaveRestrictRun = 'Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\RestrictRun'

# Grava o bloqueio no registro da conta. $Raiz e o nome da chave dentro de HKEY_USERS.
function GravarBloqueio([string]$Raiz, [string]$Comando, [string[]]$ProgramasExtras) {
    $usuarios = [Microsoft.Win32.Registry]::Users
    foreach ($politica in (PoliticasDoBloqueio $Comando)) {
        $chave = $usuarios.CreateSubKey("$Raiz\$($politica[0])")
        try {
            if ($politica[2] -is [int]) { $chave.SetValue($politica[1], [int]$politica[2], [Microsoft.Win32.RegistryValueKind]::DWord) }
            else { $chave.SetValue($politica[1], [string]$politica[2], [Microsoft.Win32.RegistryValueKind]::String) }
        }
        finally { $chave.Close() }
    }

    $usuarios.DeleteSubKeyTree("$Raiz\$ChaveRestrictRun", $false)
    $lista = $usuarios.CreateSubKey("$Raiz\$ChaveRestrictRun")
    try {
        $numero = 1
        foreach ($programa in (@($ProgramasFixos) + @($ProgramasExtras) | Where-Object { $_ } | Select-Object -Unique)) {
            $lista.SetValue([string]$numero, [string]$programa, [Microsoft.Win32.RegistryValueKind]::String)
            $numero += 1
        }
    }
    finally { $lista.Close() }
}

# Desfaz GravarBloqueio: a conta volta a ser uma conta comum do Windows.
function RemoverBloqueio([string]$Raiz) {
    $usuarios = [Microsoft.Win32.Registry]::Users
    $usuarios.DeleteSubKeyTree("$Raiz\$ChaveRestrictRun", $false)
    foreach ($politica in (PoliticasDoBloqueio '')) {
        $chave = $usuarios.OpenSubKey("$Raiz\$($politica[0])", $true)
        if ($chave) {
            try { $chave.DeleteValue($politica[1], $false) } finally { $chave.Close() }
        }
    }
}

function FecharTerminal {
    Get-Process -Name 'Pontual.Terminal.Shell' -ErrorAction SilentlyContinue | Stop-Process -Force -Confirm:$false -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800
}

function ApagarConta([string]$Conta, [string]$Sid) {
    if ($Sid) {
        Get-CimInstance -ClassName Win32_UserProfile -Filter "SID='$Sid'" -ErrorAction SilentlyContinue | Remove-CimInstance -Confirm:$false -ErrorAction SilentlyContinue
    }
    if (SidDaConta $Conta) {
        ([ADSI]"WinNT://$env:COMPUTERNAME,computer").Delete('user', $Conta)
    }
}
