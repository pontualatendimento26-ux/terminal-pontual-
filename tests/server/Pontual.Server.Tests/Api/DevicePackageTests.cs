using System.Net;
using Microsoft.AspNetCore.Http;
using Pontual.Server.Api;

namespace Pontual.Server.Tests.Api;

public sealed class DevicePackageTests
{
    private static DefaultHttpContext From(string ip, string path)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        context.Request.Path = path;
        return context;
    }

    private static void Publish(string root, string folder, string version)
    {
        var dir = Directory.CreateDirectory(Path.Combine(root, "baixar", folder)).FullName;
        File.WriteAllText(Path.Combine(dir, "versao.json"), $$"""{"versao":"{{version}}"}""");
        File.WriteAllText(Path.Combine(dir, "terminal.zip"), "x");
    }

    [Fact]
    public void So_a_maquina_escolhida_recebe_o_pacote_ate_a_versao_geral_alcancar()
    {
        var root = Directory.CreateTempSubdirectory("pontual-pacote-").FullName;
        try
        {
            Publish(root, "atualizacao", "2.15.4");
            Publish(root, "so-para/KAMILINHA", "2.15.5");
            DevicePackage.Remember(From("10.99.0.1", "/"), "KAMILINHA");
            DevicePackage.Remember(From("10.99.0.2", "/"), "OUTRA");

            const string version = "/baixar/atualizacao/versao.json";
            Assert.Equal("/baixar/so-para/KAMILINHA/versao.json", DevicePackage.PathFor(From("10.99.0.1", version), root));
            Assert.Equal("/baixar/so-para/KAMILINHA/terminal.zip", DevicePackage.PathFor(From("10.99.0.1", "/baixar/atualizacao/terminal.zip"), root));
            Assert.Null(DevicePackage.PathFor(From("10.99.0.2", version), root));
            Assert.Null(DevicePackage.PathFor(From("10.99.0.3", version), root));
            Assert.Null(DevicePackage.PathFor(From("10.99.0.1", "/baixar/atualizacao/android.json"), root));

            Publish(root, "atualizacao", "2.15.6");
            Assert.Null(DevicePackage.PathFor(From("10.99.0.1", version), root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
