using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pontual.Server.Api;
using Pontual.Server.Data;

namespace Pontual.Server.Tests.Api;

public sealed class RecordingTests
{
    /// <summary>Um "MP4" mínimo: o servidor só confere a marca ftyp; o resto é conteúdo reconhecível no disco.</summary>
    private static byte[] FakeVideo(string marker)
    {
        var bytes = new List<byte> { 0, 0, 0, 24 };
        bytes.AddRange("ftypisom"u8.ToArray());
        bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(marker));
        return [.. bytes];
    }

    private static Task<HttpResponseMessage> UploadAsync(HttpClient terminal, DateTime startedAt, byte[] video)
    {
        var content = new ByteArrayContent(video);
        content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        var at = Uri.EscapeDataString(startedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        return terminal.PostAsync($"/api/v1/device/recording?startedAt={at}&seconds=60", content);
    }

    [Fact]
    public async Task Terminal_grava_e_a_Central_assiste_com_auditoria()
    {
        using var app = new TestApp();
        var owner = await app.OwnerAsync();

        // Máquina pendente não grava.
        var (_, pendingToken) = await app.EnrollAsync(owner, "PC-01");
        var pending = await app.Terminal(pendingToken).GetAsync("/api/v1/device/recording").Json();
        Assert.False(pending.GetProperty("record").GetBoolean());

        var (deviceId, terminal) = await app.ApprovedDeviceAsync(owner, "PC-02", "Marina Alves");
        var policy = await terminal.GetAsync("/api/v1/device/recording").Json();
        Assert.True(policy.GetProperty("record").GetBoolean());
        Assert.Equal(1, policy.GetProperty("fps").GetInt32());
        Assert.Equal(60, policy.GetProperty("segmentSeconds").GetInt32());

        // Só MP4.
        var notVideo = new ByteArrayContent("isto não é vídeo"u8.ToArray());
        var at = Uri.EscapeDataString(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        Assert.Equal(HttpStatusCode.BadRequest, (await terminal.PostAsync($"/api/v1/device/recording?startedAt={at}&seconds=60", notVideo)).StatusCode);

        var startedAt = DateTime.UtcNow.AddMinutes(-2);
        var video = FakeVideo("CONTEUDO-SECRETO-DA-TELA");
        Assert.True((await UploadAsync(terminal, startedAt, video).Json()).GetProperty("record").GetBoolean());
        // Reenvio do mesmo trecho não duplica.
        await UploadAsync(terminal, startedAt, video).Json();

        // No disco, o vídeo está cifrado.
        var files = Directory.GetFiles(app.RecordingsPath, "*.pgr", SearchOption.AllDirectories);
        var file = Assert.Single(files);
        var onDisk = await File.ReadAllBytesAsync(file);
        Assert.DoesNotContain("CONTEUDO-SECRETO", System.Text.Encoding.ASCII.GetString(onDisk));

        var day = startedAt.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var days = await owner.GetAsync($"/api/v1/devices/{deviceId}/recordings/days").Json();
        var first = Assert.Single(days.EnumerateArray());
        Assert.Equal(day, first.GetProperty("date").GetString());
        Assert.Equal(1, first.GetProperty("parts").GetInt32());

        var parts = await owner.GetAsync($"/api/v1/devices/{deviceId}/recordings?date={day}").Json();
        var part = Assert.Single(parts.EnumerateArray());
        Assert.Equal("Marina Alves", part.GetProperty("attendantName").GetString());

        var played = await owner.GetAsync($"/api/v1/recordings/{part.GetProperty("id").GetGuid()}/video");
        Assert.Equal(HttpStatusCode.OK, played.StatusCode);
        Assert.Equal("video/mp4", played.Content.Headers.ContentType?.MediaType);
        Assert.Equal(video, await played.Content.ReadAsByteArrayAsync());

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PontualDb>();
            Assert.True(await db.Audit.AnyAsync(a => a.Code == "recording.view" && a.TargetId == deviceId));
        }

        // Suporte não assiste.
        var support = await app.AdminAsync(owner, "Rita", "support");
        Assert.Equal(HttpStatusCode.Forbidden, (await support.GetAsync($"/api/v1/devices/{deviceId}/recordings?date={day}")).StatusCode);

        var overview = await owner.GetAsync("/api/v1/recordings").Json();
        Assert.True(overview.GetProperty("enabled").GetBoolean());
        Assert.Equal(3, overview.GetProperty("retentionMonths").GetInt32());
        Assert.True(overview.GetProperty("usedBytes").GetInt64() > 0);
    }

    [Fact]
    public async Task Desligar_a_gravacao_avisa_o_Terminal()
    {
        using var app = new TestApp();
        var owner = await app.OwnerAsync();
        var (_, terminal) = await app.ApprovedDeviceAsync(owner, "PC-03", "Joana Lima");

        Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsJsonAsync("/api/v1/recordings/settings", new { enabled = false })).StatusCode);
        Assert.False((await terminal.GetAsync("/api/v1/device/recording").Json()).GetProperty("record").GetBoolean());

        Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsJsonAsync("/api/v1/recordings/settings", new { enabled = true })).StatusCode);
        Assert.True((await terminal.GetAsync("/api/v1/device/recording").Json()).GetProperty("record").GetBoolean());
    }

    [Fact]
    public async Task Gravacoes_com_mais_de_3_meses_sao_apagadas()
    {
        using var app = new TestApp();
        var owner = await app.OwnerAsync();
        var (deviceId, terminal) = await app.ApprovedDeviceAsync(owner, "PC-04", "Ana Souza");

        var old = DateTime.UtcNow.AddDays(-80);
        var recent = DateTime.UtcNow.AddMinutes(-5);
        await UploadAsync(terminal, old, FakeVideo("antigo")).Json();
        await UploadAsync(terminal, recent, FakeVideo("recente")).Json();
        Assert.Equal(2, Directory.GetFiles(app.RecordingsPath, "*.pgr", SearchOption.AllDirectories).Length);

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PontualDb>();
        var store = scope.ServiceProvider.GetRequiredService<RecordingStore>();

        // Daqui a 20 dias, o trecho de 80 dias atrás passa de 3 meses; o recente fica.
        var removed = await store.PurgeAsync(db, DateTime.UtcNow.AddDays(20), CancellationToken.None);
        Assert.Equal(1, removed);
        var left = Assert.Single(await db.ScreenRecordings.Where(r => r.DeviceId == deviceId).ToListAsync());
        Assert.Equal(recent, left.StartedAt, TimeSpan.FromSeconds(1));
        Assert.Single(Directory.GetFiles(app.RecordingsPath, "*.pgr", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Video_do_dia_chega_em_partes_retoma_e_toca_com_avanco()
    {
        using var app = new TestApp();
        var owner = await app.OwnerAsync();
        var (deviceId, terminal) = await app.ApprovedDeviceAsync(owner, "PC-05", "Bia Rocha");

        // 2,5 MB: passa de um bloco de cifra (1 MB) e chega em 3 partes.
        var video = new byte[(int)(2.5 * 1024 * 1024)];
        new Random(7).NextBytes(video);
        FakeVideo("X").AsSpan(0, 12).CopyTo(video);
        var upload = Guid.NewGuid();
        const int chunk = 1024 * 1024;

        Task<HttpResponseMessage> Send(long offset, int count) =>
            terminal.PostAsync($"/api/v1/device/recording/day/{upload}?offset={offset}&total={video.Length}",
                new ByteArrayContent(video, (int)offset, count));

        Assert.Equal(HttpStatusCode.OK, (await Send(0, chunk)).StatusCode);
        // Parte repetida (a resposta se perdeu): a Central diz quanto já tem.
        var conflict = await Send(0, chunk);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Contains($"\"received\":{chunk}", await conflict.Content.ReadAsStringAsync());

        var started = DateTime.UtcNow.AddHours(-3);
        object Finish() => new { startedAt = started, total = video.Length, parts = new[] { new[] { 0.0, 3600 }, new[] { 5400.0, 1800 } } };
        // Fim antes de tudo chegar: 409 com o que falta.
        Assert.Equal(HttpStatusCode.Conflict, (await terminal.PostAsJsonAsync($"/api/v1/device/recording/day/{upload}/finish", Finish())).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await Send(chunk, chunk)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(2 * chunk, video.Length - 2 * chunk)).StatusCode);
        Assert.True((await terminal.PostAsJsonAsync($"/api/v1/device/recording/day/{upload}/finish", Finish()).Json()).GetProperty("record").GetBoolean());
        // Fim repetido não duplica.
        await terminal.PostAsJsonAsync($"/api/v1/device/recording/day/{upload}/finish", Finish()).Json();

        var file = Assert.Single(Directory.GetFiles(app.RecordingsPath, "*.pgr", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(app.RecordingsPath, "*.part", SearchOption.AllDirectories));
        var onDisk = await File.ReadAllBytesAsync(file);
        Assert.False(onDisk.AsSpan().IndexOf(video.AsSpan(100, 64)) >= 0);

        var day = started.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var list = await owner.GetAsync($"/api/v1/devices/{deviceId}/recordings?date={day}").Json();
        var item = Assert.Single(list.EnumerateArray());
        Assert.Equal(5400, item.GetProperty("seconds").GetDouble());
        Assert.Equal(2, item.GetProperty("parts").GetArrayLength());
        Assert.Equal(upload, item.GetProperty("id").GetGuid());

        var whole = await owner.GetByteArrayAsync($"/api/v1/recordings/{upload}/video");
        Assert.Equal(video, whole);

        // Avançar: pedaço do meio, atravessando a divisa de blocos.
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/recordings/{upload}/video");
        request.Headers.Range = new RangeHeaderValue(chunk - 10, chunk + 9);
        var partial = await owner.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal(video.AsSpan(chunk - 10, 20).ToArray(), await partial.Content.ReadAsByteArrayAsync());

        // Bloco adulterado no disco: a Central não entrega conteúdo falso.
        onDisk[8 + 28 + 5] ^= 0xFF;
        await File.WriteAllBytesAsync(file, onDisk);
        var tampered = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/recordings/{upload}/video");
        tampered.Headers.Range = new RangeHeaderValue(0, 99);
        // Ou a resposta falha, ou chega cortada; nunca o conteúdo adulterado decifrado como se fosse bom.
        try
        {
            var r = await owner.SendAsync(tampered);
            var body = await r.Content.ReadAsByteArrayAsync();
            Assert.False(r.IsSuccessStatusCode && body.Length == 100);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            // Conexão interrompida no meio: o esperado.
        }
    }
}
