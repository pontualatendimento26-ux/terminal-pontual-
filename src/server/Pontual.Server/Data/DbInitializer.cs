using Microsoft.EntityFrameworkCore;
using Pontual.Domain.Audit;
using Pontual.Server.Api;

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
        new("Teams", "https://teams.microsoft.com/", Communication, "login.microsoftonline.com", "login.live.com"),
        new("Gmail", "https://mail.google.com/", Communication, "accounts.google.com"),
        new("Sistema interno", "http://192.168.0.243:3000/html/login.html", Tools),
        new("Site da Pontual", "https://www.pontualconsig.com.br/index.html", Tools),
    ];

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

        if (!await db.Settings.AnyAsync(s => s.Key == Setting.ConfigVersion))
        {
            db.Settings.Add(new Setting { Key = Setting.ConfigVersion, Value = 1 });
            await db.SaveChangesAsync();
        }

        // A marca "seeded" impede recadastrar os sites se o TI apagar todos de propósito.
        if (await db.Settings.AnyAsync(s => s.Key == Setting.Seeded))
        {
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
