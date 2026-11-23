using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Pontual.Server.Assistant;
using Pontual.Server.Data;
using Xunit;

namespace Pontual.Server.Tests.Api;

public class SpreadsheetTextTests
{
    [Fact]
    public void Xlsx_sai_com_o_nome_da_aba_e_das_colunas()
    {
        var result = DocumentText.Extract("acessos.xlsx", Xlsx(), NullLogger.Instance);

        Assert.Equal(FileStatus.Processed, result.Status);
        Assert.Contains("Aba: Acessos", result.Text);
        Assert.Contains("E-mail: atendimento@exemplo.com; Senha: Ab#123; Sistema: Portal", result.Text);
        // Célula vazia no meio da linha não desloca as colunas seguintes.
        Assert.Contains("E-mail: outro@exemplo.com; Sistema: Banco", result.Text);
    }

    [Fact]
    public void Csv_com_ponto_e_virgula_e_aspas()
    {
        var csv = "E-mail;Senha\r\natendimento@exemplo.com;\"a;b\"\"c\"\r\n";
        var result = DocumentText.Extract("acessos.csv", Encoding.UTF8.GetBytes(csv), NullLogger.Instance);

        Assert.Equal(FileStatus.Processed, result.Status);
        Assert.Contains("E-mail: atendimento@exemplo.com; Senha: a;b\"c", result.Text);
    }

    private static byte[] Xlsx()
    {
        const string main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var files = new Dictionary<string, string>
        {
            ["xl/workbook.xml"] =
                $"""<workbook xmlns="{main}" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Acessos" sheetId="1" r:id="rId1"/></sheets></workbook>""",
            ["xl/_rels/workbook.xml.rels"] =
                """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="worksheet" Target="worksheets/sheet1.xml"/></Relationships>""",
            ["xl/sharedStrings.xml"] =
                $"""<sst xmlns="{main}"><si><t>E-mail</t></si><si><t>Senha</t></si><si><t>Sistema</t></si><si><r><t>Ab#</t></r><r><t>123</t></r></si></sst>""",
            ["xl/worksheets/sheet1.xml"] =
                $"""
                <worksheet xmlns="{main}"><sheetData>
                <row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c><c r="C1" t="s"><v>2</v></c></row>
                <row r="2"><c r="A2" t="inlineStr"><is><t>atendimento@exemplo.com</t></is></c><c r="B2" t="s"><v>3</v></c><c r="C2" t="inlineStr"><is><t>Portal</t></is></c></row>
                <row r="3"><c r="A3" t="inlineStr"><is><t>outro@exemplo.com</t></is></c><c r="C3" t="inlineStr"><is><t>Banco</t></is></c></row>
                </sheetData></worksheet>
                """,
        };

        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }

        return memory.ToArray();
    }
}
