# Remove o Terminal Pontual 2.0 (com bloqueio). Precisa de administrador.
# Desfaz o que Instalar-Bloqueio.ps1 fez: o bloqueio da conta da atendente, a propria
# conta (se o administrador quiser), o programa e os atalhos.
# O gerador regrava este arquivo com BOM, para o PowerShell 5.1 ler os acentos.

$ErrorActionPreference = 'Stop'
$Origem = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $Origem 'Bloqueio-Comum.ps1')
GarantirAdmin $MyInvocation.MyCommand.Path

try {
    $marca = LerMarca
    $conta = if ($marca) { $marca.Conta } else { $null }
    # Prefere o SID gravado na marca (vale para conta existente, inclusive conta Microsoft cujo nome o SidDaConta nao resolve).
    $sid = if ($marca -and $marca.Sid) { $marca.Sid } elseif ($conta) { SidDaConta $conta } else { $null }

    $pergunta = if ($sid) { "Remover o $Nome deste computador e tirar o bloqueio da conta `"$conta`"?" } else { "Remover o $Nome deste computador?" }
    if ((Mensagem $pergunta 'Question' 'YesNo') -ne 'Yes') { exit 0 }

    if ($sid -and (ContaAberta $sid)) {
        Mensagem "A conta `"$conta`" está aberta neste computador.`n`nEntre nela, use `"Desligar ou sair`" > `"Sair da conta`" (ou reinicie o computador) e rode a remoção de novo." 'Warning' | Out-Null
        exit 1
    }

    $contaApagada = $false
    if ($sid) {
        $apagar = 'No'
        if ($marca.ContaCriada) {
            $apagar = Mensagem "Apagar também a conta `"$conta`" do Windows, com todos os arquivos que estão nela?`n`nSim: a conta some do computador.`nNão: a conta fica, sem o bloqueio, como uma conta comum." 'Question' 'YesNoCancel'
            if ($apagar -eq 'Cancel') { exit 0 }
        }
        if ($apagar -eq 'Yes') {
            ApagarConta $conta $sid
            $contaApagada = $true
        }
        else {
            ComRegistroDaConta $sid { param($raiz) RemoverBloqueio $raiz }
        }
    }

    FecharTerminal
    RemoverAtualizacao
    $inicio = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) $Nome
    if (Test-Path $inicio) { Remove-Item -Path $inicio -Recurse -Force -Confirm:$false }
    ApagarChaveMaquina $ChaveRemocao
    ApagarChaveMaquina $ChaveMarca

    # Este script esta dentro da pasta que sera apagada; a remocao e feita
    # por um processo separado, depois que este terminar.
    if (Test-Path $Destino) {
        $comando = "Start-Sleep -Seconds 2; Remove-Item -LiteralPath '$Destino' -Recurse -Force -Confirm:`$false"
        Start-Process -FilePath $PowerShellExe -ArgumentList '-NoProfile', '-WindowStyle', 'Hidden', '-Command', $comando -WindowStyle Hidden
    }

    $resto = if ($contaApagada) { "`n`nA conta `"$conta`" foi apagada." } elseif ($sid) { "`n`nA conta `"$conta`" continua no computador, agora sem bloqueio." } else { '' }
    Mensagem "O $Nome foi removido.$resto" | Out-Null
}
catch {
    Mensagem "A remoção não foi concluída.`n`n$($_.Exception.Message)" 'Error' | Out-Null
    exit 1
}
