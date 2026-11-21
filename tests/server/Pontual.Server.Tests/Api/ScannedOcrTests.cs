using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Pontual.Server.Assistant;
using Pontual.Server.Data;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace Pontual.Server.Tests.Api;

/// <summary>PDF escaneado na Central Neural: o texto vem do Pontual Tools local (OCR); sem ele, o PDF fica desligado.</summary>
[Collection("ScannedOcr")]
public sealed class ScannedOcrTests
{
    private static byte[] BlankPdf()
    {
        var builder = new PdfDocumentBuilder();
        builder.AddPage(PageSize.A4);
        return builder.Build();
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public async Task Scanned_pdf_uses_ocr_text_from_pontual_tools()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var server = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            var body = Encoding.UTF8.GetBytes("{\"text\":\"--- Página 1 ---\\nCONTRATO DE PRESTAÇÃO DE SERVIÇOS valor total mil reais\",\"ocr_pages\":1}");
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(body);
            ctx.Response.Close();
            return ctx.Request.Url!.AbsolutePath;
        });

        var old = ScannedOcr.BaseUrl;
        try
        {
            ScannedOcr.BaseUrl = $"http://127.0.0.1:{port}";
            var result = DocumentText.Extract("scan.pdf", BlankPdf(), NullLogger.Instance);
            Assert.Equal(FileStatus.Processed, result.Status);
            Assert.Contains("CONTRATO", result.Text);
            Assert.Equal("/api/pdf/read", await server);
        }
        finally
        {
            ScannedOcr.BaseUrl = old;
        }
    }

    [Fact]
    public void Scanned_pdf_stays_off_when_pontual_tools_is_down()
    {
        var old = ScannedOcr.BaseUrl;
        try
        {
            ScannedOcr.BaseUrl = $"http://127.0.0.1:{FreePort()}";
            var result = DocumentText.Extract("scan.pdf", BlankPdf(), NullLogger.Instance);
            Assert.Equal(FileStatus.NoText, result.Status);
            Assert.Equal(DocumentText.ScannedPdf, result.Error);
        }
        finally
        {
            ScannedOcr.BaseUrl = old;
        }
    }
}
