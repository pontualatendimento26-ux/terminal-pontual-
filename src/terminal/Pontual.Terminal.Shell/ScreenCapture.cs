using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Pontual.Terminal.Shell;

/// <summary>
/// Fotografa a tela inteira (todos os monitores) para a visualização ao vivo do TI.
/// Só é chamado enquanto o servidor diz que alguém está vendo, e a barra mostra o aviso à atendente nesse período.
/// A imagem vai direto para o servidor: nada é gravado neste computador.
/// </summary>
internal static class ScreenCapture
{
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const int SrcCopy = 0x00CC0020;
    private const int CaptureBlt = 0x40000000;

    /// <summary>JPEG com no máximo <paramref name="maxWidth"/> pixels de largura e, se possível, <paramref name="maxBytes"/> bytes.</summary>
    public static byte[] Jpeg(int maxWidth, int maxBytes)
    {
        var source = Capture();
        var scale = Math.Min(1.0, (double)maxWidth / source.PixelWidth);
        byte[] result = [];
        // Começa com boa qualidade; só baixa se a imagem não couber no limite de tamanho.
        foreach (var (quality, factor) in new[] { (85, 1.0), (75, 1.0), (62, 1.0), (52, 0.8) })
        {
            result = Encode(source, scale * factor, quality);
            if (result.Length <= maxBytes)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>
    /// Reduz uma imagem já capturada (a foto de uma aba, para a assistente) ao limite de largura e de tamanho.
    /// </summary>
    public static byte[] Shrink(byte[] image, int maxWidth, int maxBytes)
    {
        using var input = new MemoryStream(image);
        var source = BitmapFrame.Create(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        source.Freeze();
        var scale = Math.Min(1.0, (double)maxWidth / source.PixelWidth);
        byte[] result = [];
        foreach (var (quality, factor) in new[] { (80, 1.0), (68, 1.0), (58, 0.85), (50, 0.7) })
        {
            result = Encode(source, scale * factor, quality);
            if (result.Length <= maxBytes)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>
    /// Um quadro da tela inteira para a gravação: BGRA de cima para baixo, com largura no máximo
    /// <paramref name="maxWidth"/> e as duas medidas pares (o codificador de vídeo exige).
    /// </summary>
    public static (byte[] Bgra, int Width, int Height) Frame(int maxWidth)
    {
        var source = Capture();
        var scale = Math.Min(1.0, (double)maxWidth / source.PixelWidth);
        var width = Math.Max(2, (int)(source.PixelWidth * scale) & ~1);
        var height = Math.Max(2, (int)(source.PixelHeight * scale) & ~1);
        BitmapSource image = new TransformedBitmap(source, new ScaleTransform((double)width / source.PixelWidth, (double)height / source.PixelHeight));
        if (image.Format != PixelFormats.Bgr32)
        {
            image = new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0);
        }

        // O arredondamento da escala pode dar um pixel a mais ou a menos: o recorte garante o tamanho exato.
        var w = Math.Min(width, image.PixelWidth);
        var h = Math.Min(height, image.PixelHeight);
        var pixels = new byte[width * height * 4];
        image.CopyPixels(new Int32Rect(0, 0, w, h), pixels, width * 4, 0);
        return (pixels, width, height);
    }

    /// <summary>A tela inteira (PrtSc) ou só a janela indicada (Alt+PrtSc), para a área de transferência.</summary>
    public static BitmapSource Image(IntPtr window)
    {
        if (window != IntPtr.Zero && GetWindowRect(window, out var r) && r.Right > r.Left && r.Bottom > r.Top)
        {
            return Capture(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        }

        return Capture();
    }

    private static BitmapSource Capture() => Capture(
        GetSystemMetrics(SmXVirtualScreen),
        GetSystemMetrics(SmYVirtualScreen),
        GetSystemMetrics(SmCxVirtualScreen),
        GetSystemMetrics(SmCyVirtualScreen));

    private static BitmapSource Capture(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("O Windows não informou o tamanho da tela.");
        }

        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, width, height);
        try
        {
            var previous = SelectObject(memory, bitmap);
            BitBlt(memory, 0, 0, width, height, screen, x, y, SrcCopy | CaptureBlt);
            SelectObject(memory, previous);
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static byte[] Encode(BitmapSource source, double scale, int quality)
    {
        BitmapSource image = scale < 0.999 ? new TransformedBitmap(source, new ScaleTransform(scale, scale)) : source;
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out WindowRect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr handle);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, int operation);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);
}
