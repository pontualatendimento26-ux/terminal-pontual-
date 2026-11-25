using Microsoft.EntityFrameworkCore;
using Pontual.Domain.Audit;
using Pontual.Server.Api;
using Pontual.Server.Assistant;

namespace Pontual.Server.Data;

public static class DbInitializer
{
    private const string Banks = "Bancos e promotoras";
    private const string Communication = "Comunicação";
    private const string Tools = "Pontual e ferramentas";

    private sealed record Seed(string Name, string Url, string Group, params string[] ExtraHosts);

    // docs/APLICATIVOS-E-SITES.md, seção 2: domínio + página de entrada. O Excel fica de fora (é programa).
    private static readonly Seed[] InitialSites =
    [
        new("Safra", "https://epfweb.safra.com.br/Home/Login", Banks),
        new("Quero Mais Consignado", "https://portaldecredito.queromaisconsignado.com.br/login", Banks),
        new("Teddy Consig360", "https://teddy.consig360.com.br/", Banks),
        new("Nossa Fintech", "https://fintechdocorban.nossafintech.com.br/home/overview", Banks),
        new("Digio", "https://funcaoconsig.digio.com.br/FIMENU/Login/AC.UI.LOGIN.aspx", Banks),
        new("C6 Consig", "https://c6.c6consig.com.br/WebAutorizador/Login/AC.UI.LOGIN.aspx", Banks),
        new("Parceiro Santander", "https://www.parceirosantander.com.br/spa-base/landing-page", Banks),
        new("Red Consig", "https://esteira.redconsig.com.br/solicitacao-saldo-aprovadas", Banks),
        new("Bem Promotora", "https://bemweb.bempromotora.com.br/autenticacao/login", Banks),
        new("Meu CashCard", "https://front.meucashcard.com.br/WebAppBPOCartao/", Banks),
        new("Aki Promotora", "https://akipromotora.app/WebFIMenuMVC/Login/AC.UI.LOGIN.aspx", Banks),
        new("Pan Consig", "https://panconsig.pansolucoes.com.br/WebAutorizador/", Banks),
        new("Banco do Brasil Correspondente", "https://correspondente.bb.com.br/cbo-portal-acesso/", Banks),
        new("Paraná Banco", "https://front.paranabanco.b.br/login", Banks),
        new("Finanto JoinBank", "https://finanto.joinbank.com.br/", Banks),
        new("Daycoval Consignado", "https://consignado.daycoval.com.br/Autorizador/", Banks),
        new("Daycoval Portal de Crédito", "https://portaldecredito.daycoval.com.br/login", Banks),
        new("WhatsApp Web", "https://web.whatsapp.com/", Communication),
        new("Gmail", "https://mail.google.com/", Communication, "accounts.google.com"),
        new("Sistema interno", "http://192.168.0.243:3000/html/login.html", Tools),
        new("Site da Pontual", "https://www.pontualconsig.com.br/index.html", Tools),
    ];

    /// <summary>
    /// O banco é criado por <c>EnsureCreated</c>, que não mexe num banco que já existe. As tabelas que
    /// entraram depois da primeira instalação (as da assistente) são criadas aqui, com o mesmo comando
    /// que o EF geraria, só que com "IF NOT EXISTS". Nada é alterado nas tabelas antigas.
    /// </summary>
    private static async Task CreateMissingTablesAsync(PontualDb db)
    {
        string[] added =
        [
            "TextSettings", "AssistantScripts", "AssistantProfiles", "AssistantMessages",
            "KnowledgeLinks", "KnowledgeSuggestions", "AssistantFeedback", "AttendantInsights", "WhatsAppCaptures", "CoverageReplies",
            "ChromeSites", "ChromeDeviceSites", "AppDecisions", "DeviceApps", "AssistantUsage", "ScreenRecordings", "CrmEvents", "CrmReplies",
            "NexusConsultas", "SavedTexts", "StickyNotes",
        ];

        foreach (var statement in Statements(db))
        {
            string? sql = null;
            if (added.Any(table => statement.StartsWith($"CREATE TABLE \"{table}\"", StringComparison.Ordinal)))
            {
                sql = "CREATE TABLE IF NOT EXISTS" + statement["CREATE TABLE".Length..];
            }
            else if (added.Any(table => statement.Contains($" ON \"{table}\"", StringComparison.Ordinal)))
            {
                foreach (var start in (string[])["CREATE UNIQUE INDEX", "CREATE INDEX"])
                {
                    if (statement.StartsWith(start + " ", StringComparison.Ordinal))
                    {
                        sql = start + " IF NOT EXISTS" + statement[start.Length..];
                        break;
                    }
                }
            }

            if (sql is not null)
            {
                await RunAsync(db, sql);
            }
        }
    }

    private static string[] Statements(PontualDb db) =>
        db.Database.GenerateCreateScript().Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    // Texto gerado pelo próprio EF a partir do modelo, ou fixo deste arquivo; não contém dado de fora.
    private static Task RunAsync(PontualDb db, string sql)
    {
#pragma warning disable EF1002
        return db.Database.ExecuteSqlRawAsync(sql.Replace("{", "{{").Replace("}", "}}"));
#pragma warning restore EF1002
    }

    /// <summary>
    /// A tabela dos roteiros ganhou colunas quando virou a base de conhecimento (tipo, banco, categoria,
    /// arquivo…). Num banco criado antes, cada coluna que falta é acrescentada com um valor padrão; as linhas
    /// e as colunas antigas não são tocadas.
    /// </summary>
    private static async Task AddMissingColumnsAsync(PontualDb db, string table)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info(\"{table}\")";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                existing.Add(reader.GetString(1));
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }

        var create = Statements(db).First(s => s.StartsWith($"CREATE TABLE \"{table}\"", StringComparison.Ordinal));
        var addedNow = new List<string>();
        foreach (var raw in create.Split('\n'))
        {
            var line = raw.Trim().TrimEnd(',');
            if (!line.StartsWith('"'))
            {
                continue;
            }

            var name = line[1..line.IndexOf('"', 1)];
            if (existing.Contains(name))
            {
                continue;
            }

            // O SQLite só aceita acrescentar coluna obrigatória se ela tiver um valor padrão.
            if (line.Contains("NOT NULL", StringComparison.Ordinal) && !line.Contains("DEFAULT", StringComparison.Ordinal))
            {
                line += line.Contains(" INTEGER", StringComparison.Ordinal) ? " DEFAULT 0" : " DEFAULT ''";
            }

            await RunAsync(db, $"ALTER TABLE \"{table}\" ADD COLUMN {line}");
            addedNow.Add(name);
        }

        if (table != "AssistantScripts" || addedNow.Count == 0)
        {
            return;
        }

        if (addedNow.Contains("StaffOnly"))
        {
            // Planilha enviada antes da marca existir (a de senhas): nunca pode chegar à IA que fala com cliente.
            await RunAsync(db, "UPDATE \"AssistantScripts\" SET \"StaffOnly\" = 1 WHERE lower(\"FileName\") LIKE '%.xlsx' OR lower(\"FileName\") LIKE '%.csv'");
        }

        await RunAsync(db, "UPDATE \"AssistantScripts\" SET \"Kind\" = 'script' WHERE \"Kind\" = ''");
        await RunAsync(db, "UPDATE \"AssistantScripts\" SET \"CreatedAt\" = \"UpdatedAt\" WHERE \"CreatedAt\" = ''");
        await RunAsync(db, "UPDATE \"AssistantScripts\" SET \"Source\" = CASE WHEN \"UpdatedByName\" = 'Sistema' THEN 'sistema' ELSE 'manual' END WHERE \"Source\" = ''");
        if (addedNow.Contains("Bank"))
        {
            // Os roteiros que já existiam têm o banco no título ("Finanto - INSS"): só organiza o que já está escrito.
            var scripts = await db.AssistantScripts.Where(s => s.Bank == "" && s.Category == "").ToListAsync();
            foreach (var script in scripts)
            {
                (script.Bank, script.Category) = BankAndCategory(script.Title);
            }

            await db.SaveChangesAsync();
        }
    }

    /// <summary>"Finanto - INSS" → ("Finanto", "INSS"). Sem o separador, nada é deduzido.</summary>
    private static (string Bank, string Category) BankAndCategory(string title)
    {
        var cut = title.LastIndexOf(" - ", StringComparison.Ordinal);
        return cut <= 0 ? ("", "") : (title[..cut].Trim(), title[(cut + 3)..].Trim());
    }

    /// <summary>
    /// Carrega uma única vez os roteiros da pasta <c>roteiros-iniciais</c> (ao lado do executável): cada arquivo
    /// .txt vira um roteiro da assistente, com o nome do arquivo como título. Depois disso quem manda é a Central.
    /// </summary>
    private static async Task SeedScriptsAsync(PontualDb db, IServiceProvider services)
    {
        if (await db.TextSettings.AnyAsync(s => s.Key == TextSetting.AssistantScriptsSeeded))
        {
            return;
        }

        var folder = Path.Combine(AppContext.BaseDirectory, "roteiros-iniciais");
        var files = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.txt").Order(StringComparer.Ordinal).ToArray() : [];
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
        var added = 0;
        foreach (var file in files)
        {
            var title = Input.Truncated(Path.GetFileNameWithoutExtension(file), 80);
            var content = AssistantEndpoints.Multiline(await File.ReadAllTextAsync(file));
            if (title.Length == 0 || content.Length == 0)
            {
                continue;
            }

            var (bank, category) = BankAndCategory(title);
            db.AssistantScripts.Add(new AssistantScript
            {
                Id = Guid.NewGuid(), Title = title, Content = content, Bank = bank, Category = category,
                CreatedAt = now, UpdatedAt = now, UpdatedByName = "Sistema", Source = "sistema",
            });
            added++;
        }

        db.TextSettings.Add(new TextSetting { Key = TextSetting.AssistantScriptsSeeded, Value = "1" });
        if (added == 0)
        {
            await db.SaveChangesAsync();
            return;
        }

        await services.GetRequiredService<AuditWriter>().SaveAsync(
            db,
            new AuditInfo(ActorType.System, null, "Sistema", "assistant.script.seed",
                $"Sistema cadastrou os {added} roteiros iniciais da assistente"));
    }

    /// <summary>
    /// Carrega os aprendizados sobre os sistemas da Pontual da pasta <c>sistemas-iniciais</c>: cada arquivo .txt
    /// vira um item do ramo "Sistemas da Pontual", com o nome do arquivo como título. Cada arquivo entra uma única
    /// vez (o nome fica guardado); um arquivo novo colocado depois entra na próxima vez que a Central iniciar.
    /// </summary>
    private static async Task SeedSystemsAsync(PontualDb db, IServiceProvider services)
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "sistemas-iniciais");
        if (!Directory.Exists(folder))
        {
            return;
        }

        var mark = await db.TextSettings.FirstOrDefaultAsync(s => s.Key == TextSetting.AssistantSystemsSeeded);
        var loaded = new HashSet<string>((mark?.Value ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
        var added = 0;
        foreach (var file in Directory.GetFiles(folder, "*.txt").Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            if (!loaded.Add(name) || await db.AssistantScripts.CountAsync() >= KnowledgeLimits.Items)
            {
                continue;
            }

            var title = Input.Truncated(Path.GetFileNameWithoutExtension(file), 80);
            var content = AssistantEndpoints.Multiline(await File.ReadAllTextAsync(file));
            if (title.Length == 0 || content.Length == 0 || content.Length > KnowledgeLimits.Item)
            {
                continue;
            }

            db.AssistantScripts.Add(new AssistantScript
            {
                Id = Guid.NewGuid(), Kind = KnowledgeKind.System, Title = title, Content = content,
                Bank = title.StartsWith("Nexus", StringComparison.OrdinalIgnoreCase) ? "Nexus" : "",
                Category = "Sistemas da Pontual",
                AiInstructions = "Resumo de um sistema interno da Pontual. Para a operação, explique sem jargão e diga em que tela fica cada coisa. Não invente taxa, prazo, regra de banco nem comissão: isso muda e deve ser confirmado na tela ou no roteiro do banco.",
                CreatedAt = now, UpdatedAt = now, UpdatedByName = "Sistema", Source = "sistema",
            });
            added++;
        }

        var value = string.Join('\n', loaded.Order(StringComparer.Ordinal));
        if (mark is null)
        {
            db.TextSettings.Add(new TextSetting { Key = TextSetting.AssistantSystemsSeeded, Value = value });
        }
        else
        {
            mark.Value = value;
        }

        if (added == 0)
        {
            await db.SaveChangesAsync();
            return;
        }

        await services.GetRequiredService<AuditWriter>().SaveAsync(
            db,
            new AuditInfo(ActorType.System, null, "Sistema", "assistant.system.seed",
                $"Sistema cadastrou {added} aprendizado(s) sobre os sistemas da Pontual"));
    }

    /// <summary>
    /// Pedido do dono (23/11/2026): o site "Teams" (teams.microsoft.com) duplicava o aplicativo Microsoft Teams. Sai uma vez só;
    /// se o TI cadastrar de novo depois, fica.
    /// </summary>
    private static async Task RemoveTeamsSiteOnceAsync(PontualDb db, IServiceProvider services)
    {
        if (await db.TextSettings.AnyAsync(s => s.Key == TextSetting.TeamsSiteRemoved))
        {
            return;
        }

        db.TextSettings.Add(new TextSetting { Key = TextSetting.TeamsSiteRemoved, Value = "1" });
        var sites = await db.Items.Where(i => i.Kind == ItemKind.Site && i.Name == "Teams" && i.Target == "https://teams.microsoft.com/").ToListAsync();
        if (sites.Count == 0)
        {
            await db.SaveChangesAsync();
            return;
        }

        var ids = sites.Select(s => (Guid?)s.Id).ToList();
        foreach (var observed in await db.ObservedHosts.Where(o => ids.Contains(o.ItemId)).ToListAsync())
        {
            observed.ItemId = null;
        }

        db.Items.RemoveRange(sites);
        await services.GetRequiredService<AuditWriter>().SaveAsync(
            db,
            new AuditInfo(ActorType.System, null, "Sistema", "item.delete",
                "Sistema excluiu o site Teams (https://teams.microsoft.com/): fica só o aplicativo Microsoft Teams"),
            bumpConfig: true);
    }

    public static async Task InitializeAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PontualDb>();

        var dataSource = db.Database.GetDbConnection().DataSource;
        if (!string.IsNullOrEmpty(dataSource) && Path.GetDirectoryName(Path.GetFullPath(dataSource)) is { } directory)
        {
            Directory.CreateDirectory(directory);
        }

        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        await CreateMissingTablesAsync(db);
        await AddMissingColumnsAsync(db, "AssistantScripts");
        await AddMissingColumnsAsync(db, "AssistantMessages");
        await AddMissingColumnsAsync(db, "AttendantInsights");
        await AddMissingColumnsAsync(db, "AssistantProfiles");
        await AddMissingColumnsAsync(db, "Devices");
        await AddMissingColumnsAsync(db, "ScreenRecordings");
        await AddMissingColumnsAsync(db, "CrmEvents");
        await AddMissingColumnsAsync(db, "CrmReplies");
        await SeedScriptsAsync(db, scope.ServiceProvider);
        await SeedSystemsAsync(db, scope.ServiceProvider);

        if (!await db.Settings.AnyAsync(s => s.Key == Setting.ConfigVersion))
        {
            db.Settings.Add(new Setting { Key = Setting.ConfigVersion, Value = 1 });
            await db.SaveChangesAsync();
        }

        // A marca "seeded" impede recadastrar os sites se o TI apagar todos de propósito.
        if (await db.Settings.AnyAsync(s => s.Key == Setting.Seeded))
        {
            await RemoveTeamsSiteOnceAsync(db, scope.ServiceProvider);
            return;
        }

        var now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
        var order = 0;
        foreach (var site in InitialSites)
        {
            db.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                Name = site.Name,
                Kind = ItemKind.Site,
                Target = site.Url,
                Group = site.Group,
                ExtraHosts = [.. site.ExtraHosts],
                Order = ++order,
                CreatedAt = now,
            });
        }

        db.Settings.Add(new Setting { Key = Setting.Seeded, Value = 1 });

        await scope.ServiceProvider.GetRequiredService<AuditWriter>().SaveAsync(
            db,
            new AuditInfo(ActorType.System, null, "Sistema", "items.seed",
                $"Sistema cadastrou os {InitialSites.Length} sites iniciais, liberados para todas as máquinas"),
            bumpConfig: true);
    }
}
