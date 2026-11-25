using System.Net;
using System.Text;
using System.Text.Json;
using Pontual.Terminal.Ipc;

namespace Pontual.Terminal.Tests;

public class LocalConfigFileTests
{
    [Fact]
    public void Arquivo_distribuido_com_o_programa_e_valido_e_tem_os_22_sites()
    {
        var path = Path.Combine(AppContext.BaseDirectory, LocalConfigFile.FileName);

        var result = LocalConfigFile.Load(path, "usuario.windows");

        Assert.True(result.Ok, result.Error);
        var config = result.Config!;
        Assert.True(config.ObservationMode);
        Assert.Equal("usuario.windows", config.AttendantName);
        Assert.Equal(22, config.Items.Count);
        Assert.DoesNotContain(config.Items, i => i.IsProgram);
        Assert.Equal(17, config.Items.Count(i => i.Group == "Bancos e promotoras"));
        Assert.Equal(3, config.Items.Count(i => i.Group == "Comunicação"));

        var policy = HostPolicy.FromConfig(config);
        Assert.All(config.Items, i => Assert.Equal(NavigationVerdict.Allowed, policy.Evaluate(i.Target).Verdict));
        Assert.Equal(NavigationVerdict.Allowed, policy.Evaluate("http://192.168.0.243:3000/html/instancias.html").Verdict);
        Assert.Equal(NavigationVerdict.Allowed, policy.Evaluate("https://accounts.google.com/").Verdict);
        Assert.Equal(NavigationVerdict.Allowed, policy.Evaluate("https://login.microsoftonline.com/").Verdict);
        Assert.Equal(NavigationVerdict.Allowed, policy.Evaluate("https://login.live.com/").Verdict);
        Assert.Equal(NavigationVerdict.NotListed, policy.Evaluate("https://www.google.com/").Verdict);
    }

    [Fact]
    public void Nome_do_arquivo_tem_prioridade_e_dominio_do_alvo_entra_sozinho()
    {
        var result = LocalConfigFile.Parse("""
            {
              // comentário do TI
              "version": 4, "attendantName": " Marina Alves ", "observationMode": false,
              "items": [
                { "name": "Portal", "target": "https://portal.exemplo.com.br/login", "hosts": ["login.exemplo.com.br"] },
                { "id": "calc", "name": "Calculadora", "kind": "program", "target": "C:\\Apps\\calc.exe", "hosts": ["x.com"] },
              ]
            }
            """, "usuario");

        Assert.True(result.Ok, result.Error);
        var config = result.Config!;
        Assert.Equal(4, config.Version);
        Assert.Equal("Marina Alves", config.AttendantName);
        Assert.False(config.ObservationMode);
        Assert.Equal("item-1", config.Items[0].Id);
        Assert.Equal("site", config.Items[0].Kind);
        Assert.Equal(["portal.exemplo.com.br", "login.exemplo.com.br"], config.Items[0].Hosts);
        Assert.Equal("Pontual e ferramentas", config.Items[0].Group);
        Assert.True(config.Items[1].IsProgram);
        Assert.Empty(config.Items[1].Hosts);
    }

    [Theory]
    [InlineData("{ \"items\": [ { \"name\": \"A\", \"target\": \"https://a.com\" } ", "JSON válido")]
    [InlineData("{ }", "\"items\"")]
    [InlineData("null", "\"items\"")]
    [InlineData("{ \"items\": [ { \"name\": \"A\" } ] }", "obrigatórios")]
    [InlineData("{ \"items\": [ { \"name\": \"A\", \"target\": \"a.com\" } ] }", "http://")]
    [InlineData("{ \"items\": [ { \"name\": \"A\", \"target\": \"file:///C:/x\" } ] }", "http://")]
    [InlineData("{ \"items\": [ { \"name\": \"A\", \"kind\": \"outro\", \"target\": \"https://a.com\" } ] }", "\"kind\"")]
    [InlineData("{ \"items\": [ { \"name\": \"A\", \"kind\": \"program\", \"target\": \"C:\\\\Windows\\\\System32\\\\cmd.exe\" } ] }", ".exe")]
    [InlineData("{ \"items\": [ { \"name\": \"A\", \"kind\": \"program\", \"target\": \"calc.exe\" } ] }", ".exe")]
    [InlineData("{ \"items\": [ { \"id\": \"x\", \"name\": \"A\", \"target\": \"https://a.com\" }, { \"id\": \"x\", \"name\": \"B\", \"target\": \"https://b.com\" } ] }", "repetido")]
    [InlineData("{ \"items\": [ { \"name\": \"A\", \"target\": \"https://a.com\", \"hosts\": [\"ftp://b.com\"] } ] }", "hosts")]
    public void Arquivo_invalido_explica_o_erro_sem_lancar(string json, string expectedInError)
    {
        var result = LocalConfigFile.Parse(json, "usuario");

        Assert.False(result.Ok);
        Assert.Contains(expectedInError, result.Error);
    }

    [Fact]
    public void Arquivo_ausente_explica_o_erro()
    {
        var result = LocalConfigFile.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"), "usuario");

        Assert.False(result.Ok);
        Assert.Contains("não foi encontrado", result.Error);
    }
}

public sealed class LocalEventLogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pontual-testes-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Cada_evento_vira_uma_linha_com_data_e_hora()
    {
        var log = new LocalEventLog(Path.Combine(_directory, "sub", LocalEventLog.FileName));
        var at = new DateTimeOffset(new DateTime(2026, 11, 16, 14, 5, 9, DateTimeKind.Local));

        Assert.True(log.Append(new DeviceEvent(DeviceEvent.Opened, "Safra", null, at)));
        Assert.True(log.Append(new DeviceEvent(DeviceEvent.Blocked, "Safra", "evil.com", at)));
        Assert.True(log.Append(new DeviceEvent(DeviceEvent.Help, null, null, at)));

        var lines = File.ReadAllLines(log.FilePath);
        Assert.Equal(
            ["2026-11-16 14:05:09  Aberto  Safra", "2026-11-16 14:05:09  Bloqueado  Safra  evil.com", "2026-11-16 14:05:09  Chamado ao TI"],
            lines);
    }

    [Fact]
    public void Quebra_de_linha_no_texto_nao_cria_linha_falsa()
    {
        var line = LocalEventLog.Format(new DeviceEvent(DeviceEvent.Observed, "Site\r\n2026-01-01 00:00:00  Aberto  Falso", "a.com", DateTimeOffset.Now));

        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
    }

    [Fact]
    public void Falha_ao_gravar_nao_lanca()
    {
        Directory.CreateDirectory(_directory);
        var log = new LocalEventLog(_directory); // o caminho é uma pasta: não dá para gravar

        Assert.False(log.Append(new DeviceEvent(DeviceEvent.Help, null, null, DateTimeOffset.Now)));
    }
}

public class EventQueueTests
{
    private static DeviceEvent Event(string item) => new(DeviceEvent.Opened, item, null, DateTimeOffset.UtcNow);

    [Fact]
    public void Lote_so_sai_da_fila_depois_de_confirmado()
    {
        var queue = new EventQueue();
        queue.Enqueue(Event("a"));
        queue.Enqueue(Event("b"));
        queue.Enqueue(Event("c"));

        var batch = queue.Peek(2);
        Assert.Equal(3, queue.Count);
        queue.Enqueue(Event("d"));
        queue.Acknowledge(batch);

        Assert.Equal(["c", "d"], queue.Peek(10).Select(e => e.Item));
    }

    [Fact]
    public void Fila_cheia_descarta_os_mais_antigos()
    {
        var queue = new EventQueue(capacity: 3);
        foreach (var name in new[] { "a", "b", "c", "d", "e" })
        {
            queue.Enqueue(Event(name));
        }

        Assert.Equal(["c", "d", "e"], queue.Peek(10).Select(e => e.Item));
    }
}

public class ServerApiTests
{
    [Fact]
    public async Task Cadastro_envia_codigo_maquina_e_windows_sem_token()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """{ "deviceId": "dev-1", "token": "tok-1" }""");
        using var api = new ServerApi("http://192.168.0.10:5080/", null, handler);

        var response = await api.EnrollAsync("ABCD-2345", "PC-04", "Windows 10 Pro 22H2");

        Assert.Equal(new EnrollResponse("dev-1", "tok-1"), response);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("http://192.168.0.10:5080/api/v1/device/enroll", handler.Url);
        Assert.Null(handler.Authorization);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("ABCD-2345", body.RootElement.GetProperty("code").GetString());
        Assert.Equal("PC-04", body.RootElement.GetProperty("hostname").GetString());
        Assert.Equal("Windows 10 Pro 22H2", body.RootElement.GetProperty("windows").GetString());
    }

    [Fact]
    public async Task Heartbeat_envia_token_e_itens_em_uso()
    {
        var handler = new FakeHandler(HttpStatusCode.OK,
            """{ "state": "maintenance", "configVersion": 12, "attendantName": "Marina", "maintenance": { "reason": "teclado", "until": "2026-11-16T18:00:00Z" }, "observationMode": true }""");
        using var api = new ServerApi("http://srv:5080", "tok-1", handler);

        var response = await api.HeartbeatAsync(["Safra", "Excel"], new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal("Bearer tok-1", handler.Authorization);
        Assert.Equal("http://srv:5080/api/v1/device/heartbeat", handler.Url);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(["Safra", "Excel"], body.RootElement.GetProperty("appsInUse").EnumerateArray().Select(e => e.GetString()));
        Assert.StartsWith("2026-11-16T12:00:00", body.RootElement.GetProperty("sessionStartedAt").GetString());
        Assert.Equal("maintenance", response.State);
        Assert.Equal(12, response.ConfigVersion);
        Assert.Equal("teclado", response.Maintenance?.Reason);
        Assert.True(response.ObservationMode);
    }

    [Fact]
    public async Task Configuracao_e_lida_e_limpa()
    {
        var handler = new FakeHandler(HttpStatusCode.OK,
            """{ "version": 5, "attendantName": "Marina", "observationMode": false, "items": [ { "id": "1", "name": "Safra", "kind": "site", "target": "https://epfweb.safra.com.br/", "group": "Bancos e promotoras", "hosts": ["epfweb.safra.com.br"] }, { "id": "2", "name": "Excel", "kind": "program", "target": "C:\\Office\\EXCEL.EXE", "group": "Pontual e ferramentas" }, { "id": "", "name": "sem id", "target": "x" } ] }""");
        using var api = new ServerApi("http://srv:5080", "tok-1", handler);

        var config = await api.GetConfigAsync();

        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal(5, config.Version);
        Assert.Equal(2, config.Items.Count);
        Assert.Empty(config.Items[1].Hosts);
        Assert.True(config.Items[1].IsProgram);
    }

    [Fact]
    public async Task Eventos_omitem_campos_vazios()
    {
        var handler = new FakeHandler(HttpStatusCode.NoContent, "");
        using var api = new ServerApi("http://srv:5080", "tok-1", handler);

        await api.SendEventsAsync([new DeviceEvent(DeviceEvent.Help, null, null, DateTimeOffset.UtcNow), new DeviceEvent(DeviceEvent.Blocked, "Safra", "evil.com", DateTimeOffset.UtcNow)]);

        using var body = JsonDocument.Parse(handler.Body!);
        var events = body.RootElement.GetProperty("events");
        Assert.Equal("help", events[0].GetProperty("type").GetString());
        Assert.False(events[0].TryGetProperty("item", out _));
        Assert.True(events[0].TryGetProperty("at", out _));
        Assert.Equal("evil.com", events[1].GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Erro_do_servidor_traz_o_status_e_a_mensagem()
    {
        using var removed = new ServerApi("http://srv:5080", "tok-1", new FakeHandler(HttpStatusCode.Unauthorized, """{ "error": "Máquina removida" }"""));
        using var broken = new ServerApi("http://srv:5080", "tok-1", new FakeHandler(HttpStatusCode.BadGateway, "<html>erro</html>"));
        using var garbage = new ServerApi("http://srv:5080", "tok-1", new FakeHandler(HttpStatusCode.OK, "não é json"));

        var unauthorized = await Assert.ThrowsAsync<ServerApiException>(() => removed.HeartbeatAsync([], DateTimeOffset.UtcNow));
        var gateway = await Assert.ThrowsAsync<ServerApiException>(() => broken.GetConfigAsync());
        await Assert.ThrowsAsync<ServerApiException>(() => garbage.GetConfigAsync());

        Assert.True(unauthorized.IsUnauthorized);
        Assert.Equal("Máquina removida", unauthorized.Message);
        Assert.False(gateway.IsUnauthorized);
        Assert.Contains("502", gateway.Message);
        Assert.False(ServerApi.IsNetworkFailure(unauthorized));
        Assert.True(ServerApi.IsNetworkFailure(new HttpRequestException("sem rede")));
        Assert.True(ServerApi.IsNetworkFailure(new TaskCanceledException()));
    }

    [Fact]
    public async Task FetchControl_le_os_eventos_do_corpo()
    {
        var json = """
        { "controlling": true, "events": [
          { "type": "move", "x": 0.5, "y": 0.25, "button": 0, "delta": 0, "key": 0, "text": null },
          { "type": "text", "x": 0, "y": 0, "button": 0, "delta": 0, "key": 0, "text": "oi" }
        ] }
        """;
        var handler = new FakeHandler(HttpStatusCode.OK, json);
        using var api = new ServerApi("http://srv:5080", "tok-1", handler);

        var batch = await api.FetchControlAsync();

        Assert.True(batch.Controlling);
        Assert.Equal(2, batch.Events.Count);
        Assert.Equal("move", batch.Events[0].Type);
        Assert.Equal(0.5, batch.Events[0].X, 3);
        Assert.Equal("text", batch.Events[1].Type);
        Assert.Equal("oi", batch.Events[1].Text);
    }

    private sealed class FakeHandler(HttpStatusCode status, string responseBody) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }

        public string? Url { get; private set; }

        public string? Authorization { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Url = request.RequestUri?.ToString();
            Authorization = request.Headers.Authorization?.ToString();
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(responseBody, Encoding.UTF8, "application/json") };
        }
    }
}
