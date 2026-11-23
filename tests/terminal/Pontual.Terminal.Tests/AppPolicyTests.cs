using Pontual.Terminal.Ipc;

namespace Pontual.Terminal.Tests;

/// <summary>Lista "Aplicativos" do Terminal: o que abre sem aviso, o que vira pedido, o que o TI bloqueou e o que nunca abre.</summary>
public class AppPolicyTests
{
    private const string Spotify = @"C:\Users\gabi\AppData\Roaming\Spotify\Spotify.exe";
    private const string Excel = @"C:\Program Files\Microsoft Office\root\Office16\EXCEL.EXE";

    private static TerminalConfig Config(string[]? allowed = null, string[]? blocked = null, params string[] programs) => new(1, null, true,
        programs.Select((p, i) => new ConfigItem($"p{i}", $"Programa {i}", "program", p, "Pontual e ferramentas", [])).ToArray(),
        AppsAllowed: allowed, AppsBlocked: blocked);

    [Theory]
    [InlineData(@"C:\Users\gabi\AppData\Roaming\Spotify\Spotify.exe", "spotify.exe")]
    [InlineData(@"C:\Windows\System32\CALC.EXE", "calc.exe")]
    [InlineData("C:/Downloads/Instalador do Jogo.exe", "instalador do jogo.exe")]
    public void Nome_do_arquivo_em_minusculas_e_a_chave(string path, string exe)
    {
        Assert.Equal(exe, AppPolicy.ExeName(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\x\foto.png")]
    [InlineData(@"C:\x\.exe")]
    [InlineData(@"C:\x\planilha.exe:cmd.exe")]
    public void O_que_nao_e_exe_nao_tem_nome(string? path)
    {
        Assert.Null(AppPolicy.ExeName(path));
    }

    [Theory]
    [InlineData("cmd")]
    [InlineData("powershell")]
    [InlineData("regedit")]
    [InlineData("taskmgr")]
    [InlineData("control")]
    [InlineData("mmc")]
    [InlineData("explorer")]
    [InlineData("sethc")]
    [InlineData("git-bash")]
    [InlineData("python")]
    [InlineData("node")]
    [InlineData("Pontual.Terminal.Shell")]
    public void Ferramentas_do_sistema_e_o_proprio_Terminal_nunca_abrem_pela_lista(string name)
    {
        Assert.True(AppPolicy.IsRefused($@"C:\Windows\System32\{name}.exe"));
        Assert.Equal(AppVerdict.Refused, AppPolicy.Decide($@"C:\Windows\System32\{name}.exe", Config(allowed: [$"{name}.exe"])));
    }

    [Fact]
    public void Caminho_relativo_e_recusado()
    {
        Assert.Equal(AppVerdict.Refused, AppPolicy.Decide(@"Spotify\Spotify.exe", Config()));
    }

    [Fact]
    public void Aplicativo_novo_abre_e_vira_pedido()
    {
        Assert.Equal(AppVerdict.Request, AppPolicy.Decide(Spotify, Config()));
        Assert.Equal(AppVerdict.Request, AppPolicy.Decide(Spotify, null));
        Assert.Equal(AppVerdict.Request, AppPolicy.Decide(@"C:\Windows\System32\calc.exe", Config()));
    }

    [Fact]
    public void Programa_cadastrado_e_aplicativo_liberado_abrem_sem_aviso()
    {
        Assert.Equal(AppVerdict.Allowed, AppPolicy.Decide(Excel, Config(programs: Excel)));
        Assert.Equal(AppVerdict.Allowed, AppPolicy.Decide(Excel.ToLowerInvariant(), Config(programs: Excel)));
        Assert.Equal(AppVerdict.Allowed, AppPolicy.Decide(Spotify, Config(allowed: ["spotify.exe"])));
        Assert.Equal(AppVerdict.Allowed, AppPolicy.Decide(Spotify, Config(allowed: ["SPOTIFY.EXE"])));
    }

    [Fact]
    public void Aplicativo_da_Microsoft_abre_liberado_mas_o_bloqueio_do_TI_vale_mais()
    {
        const string Store = @"C:\Program Files\WindowsApps\Microsoft.WindowsStore_22410.1401.5.0_x64__8wekyb3d8bbwe\WinStore.App.exe";
        Assert.Equal(AppVerdict.Allowed, AppPolicy.Decide(Store, Config(), microsoft: true));
        Assert.Equal(AppVerdict.Allowed, AppPolicy.Decide(Store, null, microsoft: true));
        Assert.Equal(AppVerdict.Allowed, AppPolicy.Decide(Excel, Config(), microsoft: true));
        Assert.Equal(AppVerdict.Blocked, AppPolicy.Decide(Store, Config(blocked: ["winstore.app.exe"]), microsoft: true));
        Assert.Equal(AppVerdict.Request, AppPolicy.Decide(Store, Config()));
    }

    [Theory]
    [InlineData("WindowsTerminal")]
    [InlineData("wsl")]
    [InlineData("ubuntu")]
    [InlineData("QuickAssist")]
    [InlineData("SystemSettings")]
    public void Aplicativo_da_Microsoft_que_abre_console_ou_acesso_remoto_continua_recusado(string name)
    {
        Assert.Equal(AppVerdict.Refused, AppPolicy.Decide($@"C:\Program Files\WindowsApps\x\{name}.exe", Config(), microsoft: true));
    }

    [Theory]
    [InlineData("WINWORD.EXE")]
    [InlineData("olk.exe")]
    [InlineData("ms-teams.exe")]
    public void Office_e_Teams_sao_da_Microsoft(string exe)
    {
        Assert.Contains(exe, AppPolicy.MicrosoftExes);
    }

    [Fact]
    public void Bloqueado_pelo_TI_vence_tudo()
    {
        Assert.Equal(AppVerdict.Blocked, AppPolicy.Decide(Spotify, Config(blocked: ["spotify.exe"])));
        Assert.Equal(AppVerdict.Blocked, AppPolicy.Decide(Spotify, Config(allowed: ["spotify.exe"], blocked: ["spotify.exe"])));
        Assert.Equal(AppVerdict.Blocked, AppPolicy.Decide(Excel, Config(blocked: ["excel.exe"], programs: Excel)));
    }
}
