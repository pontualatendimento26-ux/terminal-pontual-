using Pontual.Terminal.Ipc;

namespace Pontual.Terminal.Tests;

public class ProgramPolicyTests
{
    private const string Excel = @"C:\Program Files\Microsoft Office\root\Office16\EXCEL.EXE";

    private static TerminalConfig ConfigWith(params string[] programs) => new(1, null, true,
        programs.Select((p, i) => new ConfigItem($"p{i}", $"Programa {i}", "program", p, "Pontual e ferramentas", [])).ToArray());

    [Theory]
    [InlineData("cmd")]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    [InlineData("wscript")]
    [InlineData("cscript")]
    [InlineData("mshta")]
    [InlineData("regedit")]
    [InlineData("rundll32")]
    [InlineData("msiexec")]
    [InlineData("bash")]
    [InlineData("wsl")]
    [InlineData("wt")]
    public void Todos_os_nomes_da_lista_do_contrato_sao_recusados(string name)
    {
        Assert.True(ProgramPolicy.IsRefused($@"C:\Windows\System32\{name}.exe"));
        Assert.True(ProgramPolicy.IsRefused($@"C:\Windows\System32\{name.ToUpperInvariant()}.EXE"));
        Assert.True(ProgramPolicy.IsRefused($"C:/Windows/System32/{name}.exe"));
        Assert.True(ProgramPolicy.IsRefused($"{name}.exe"));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\cmd.exe.")]
    [InlineData(@"C:\Windows\System32\cmd.exe ")]
    [InlineData(@" C:\Windows\System32\cmd.exe")]
    [InlineData(@"C:\Windows\System32\cmd .exe")]
    [InlineData(@"C:\Pasta\planilha.exe:cmd.exe")]
    [InlineData(@"C:\Pasta\script.bat")]
    [InlineData(@"C:\Pasta\script.ps1")]
    [InlineData(@"C:\Pasta\atalho.lnk")]
    [InlineData(@"C:\Pasta\instalador.msi")]
    [InlineData(@"C:\Pasta\programa")]
    [InlineData(@"C:\Pasta\programa.exe /c calc")]
    [InlineData("\"C:\\Pasta\\programa.exe\"")]
    [InlineData(@"C:\Pasta\.exe")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Caminho_estranho_ou_que_nao_termina_em_exe_e_recusado(string? path)
    {
        Assert.True(ProgramPolicy.IsRefused(path));
    }

    [Theory]
    [InlineData(Excel)]
    [InlineData(@"C:\Program Files\Calculadora\calc.exe")]
    [InlineData(@"C:\Ferramentas\cmdlets.exe")]
    [InlineData(@"D:\Apps\wtools.exe")]
    public void Programa_comum_nao_e_recusado(string path)
    {
        Assert.False(ProgramPolicy.IsRefused(path));
    }

    [Fact]
    public void So_inicia_o_que_consta_na_configuracao()
    {
        var config = ConfigWith(Excel);

        Assert.True(ProgramPolicy.CanStart(Excel, config));
        Assert.True(ProgramPolicy.CanStart(Excel.ToLowerInvariant(), config));
        Assert.False(ProgramPolicy.CanStart(@"C:\Program Files\Outro\outro.exe", config));
        Assert.False(ProgramPolicy.CanStart(Excel, ConfigWith()));
        Assert.False(ProgramPolicy.CanStart(Excel, null));
        Assert.False(ProgramPolicy.CanStart(null, config));
    }

    [Fact]
    public void Recusado_nao_inicia_mesmo_se_o_servidor_mandar()
    {
        const string cmd = @"C:\Windows\System32\cmd.exe";

        Assert.False(ProgramPolicy.CanStart(cmd, ConfigWith(cmd)));
    }

    [Fact]
    public void Caminho_incompleto_nao_inicia()
    {
        Assert.False(ProgramPolicy.CanStart("excel.exe", ConfigWith("excel.exe")));
        Assert.False(ProgramPolicy.CanStart(@"..\excel.exe", ConfigWith(@"..\excel.exe")));
    }

    [Fact]
    public void Um_site_com_o_mesmo_alvo_nao_libera_o_programa()
    {
        var config = new TerminalConfig(1, null, true, [new ConfigItem("s", "Falso", "site", Excel, "Comunicação", [])]);

        Assert.False(ProgramPolicy.CanStart(Excel, config));
    }
}
