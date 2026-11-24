using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pontual.Terminal.Ipc;

namespace Pontual.Terminal.Shell;

/// <summary>Logo de um site (pelo domínio) ou programa (pelo nome do .exe). Sem logo, a barra mostra as iniciais.</summary>
internal static class Logos
{
    private sealed record Table(Dictionary<string, string> Hosts, Dictionary<string, string> Exes);

    private static readonly Table Map = Load();
    private static readonly Dictionary<string, ImageSource?> Images = [];

    public static ImageSource? For(ConfigItem item)
    {
        if (item.IsProgram)
        {
            return ForExe(Path.GetFileName(item.Target));
        }

        return Uri.TryCreate(item.Target, UriKind.Absolute, out var uri) ? ForHost(uri.Host) : null;
    }

    public static ImageSource? ForExe(string exe) => Image(Map.Exes.GetValueOrDefault(exe.ToLowerInvariant()));

    /// <summary>O domínio ou um domínio acima dele: "a.safra.com.br" acha "safra.com.br".</summary>
    private static ImageSource? ForHost(string host)
    {
        for (var rest = host.ToLowerInvariant(); rest.Length > 0;)
        {
            if (Map.Hosts.TryGetValue(rest, out var logo))
            {
                return Image(logo);
            }

            var dot = rest.IndexOf('.');
            rest = dot < 0 ? "" : rest[(dot + 1)..];
        }

        return null;
    }

    private static ImageSource? Image(string? name)
    {
        if (name is null)
        {
            return null;
        }

        lock (Images)
        {
            if (!Images.TryGetValue(name, out var image))
            {
                try
                {
                    var bitmap = new BitmapImage(new Uri($"pack://application:,,,/Pontual.Terminal.Shell;component/Logos/{name}.png"));
                    bitmap.Freeze();
                    image = bitmap;
                }
                catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException)
                {
                    // Logo ausente ou ilegível: ficam as iniciais.
                }

                Images[name] = image;
            }

            return image;
        }
    }

    private static Table Load()
    {
        using var stream = typeof(Logos).Assembly.GetManifestResourceStream("logos.json");
        return (stream is null ? null : JsonSerializer.Deserialize<Table>(stream, JsonSerializerOptions.Web)) ?? new Table([], []);
    }
}
