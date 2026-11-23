using System.Runtime.InteropServices;

namespace Pontual.Terminal.Shell;

/// <summary>
/// Grava um vídeo H.264 (.mp4) com o codificador que já vem no Windows (Media Foundation): nada de programa extra.
/// Recebe quadros BGRA de tamanho fixo, de cima para baixo. Um arquivo por trecho; <see cref="Finish"/> fecha o arquivo.
/// </summary>
internal sealed partial class Mp4Writer : IDisposable
{
    private const long Second = 10_000_000; // unidades de 100 ns

    private static readonly object StartLock = new();
    private static bool _started;

    private readonly int _width;
    private readonly int _height;
    private IMFSinkWriter? _writer;
    private readonly int _stream;
    private bool _finished;

    /// <param name="quality">0 a 100: qualidade do codificador no modo "qualidade" (o tamanho acompanha o quanto a tela muda).</param>
    public Mp4Writer(string path, int width, int height, int fps, int quality)
    {
        if (width <= 0 || height <= 0 || width % 2 != 0 || height % 2 != 0)
        {
            throw new ArgumentException("Largura e altura precisam ser pares e positivas.");
        }

        _width = width;
        _height = height;
        Startup();

        Check(MFCreateAttributes(out var attributes, 2));
        try
        {
            Check(MFCreateSinkWriterFromURL(path, IntPtr.Zero, attributes, out _writer));
        }
        finally
        {
            Marshal.ReleaseComObject(attributes);
        }

        var size = ((ulong)(uint)width << 32) | (uint)height;
        var rate = ((ulong)(uint)Math.Max(1, fps) << 32) | 1u;

        Check(MFCreateMediaType(out var output));
        Check(MFCreateMediaType(out var input));
        Check(MFCreateAttributes(out var encoding, 3));
        try
        {
            Check(output.SetGUID(Guids.MajorType, Guids.Video));
            Check(output.SetGUID(Guids.SubType, Guids.H264));
            Check(output.SetUINT32(Guids.AvgBitrate, 400_000)); // teto de referência; o modo qualidade manda no tamanho real
            Check(output.SetUINT32(Guids.InterlaceMode, 2)); // progressivo
            Check(output.SetUINT64(Guids.FrameSize, size));
            Check(output.SetUINT64(Guids.FrameRate, rate));
            Check(output.SetUINT64(Guids.PixelAspectRatio, (1UL << 32) | 1));
            Check(_writer.AddStream(output, out _stream));

            Check(input.SetGUID(Guids.MajorType, Guids.Video));
            Check(input.SetGUID(Guids.SubType, Guids.Rgb32));
            Check(input.SetUINT32(Guids.InterlaceMode, 2));
            Check(input.SetUINT64(Guids.FrameSize, size));
            Check(input.SetUINT64(Guids.FrameRate, rate));
            Check(input.SetUINT64(Guids.PixelAspectRatio, (1UL << 32) | 1));
            Check(input.SetUINT32(Guids.DefaultStride, (uint)(width * 4))); // de cima para baixo

            Check(encoding.SetUINT32(Guids.RateControlMode, 3)); // eAVEncCommonRateControlMode_Quality
            Check(encoding.SetUINT32(Guids.Quality, (uint)Math.Clamp(quality, 1, 100)));
            Check(encoding.SetUINT32(Guids.GopSize, (uint)Math.Max(1, fps) * 20)); // um quadro-chave a cada 20 s: avançar no vídeo fica rápido
            Check(_writer.SetInputMediaType(_stream, input, encoding));
        }
        finally
        {
            Marshal.ReleaseComObject(encoding);
            Marshal.ReleaseComObject(input);
            Marshal.ReleaseComObject(output);
        }

        Check(_writer.BeginWriting());
    }

    public int Width => _width;

    public int Height => _height;

    /// <summary>Um quadro BGRA (largura × altura × 4 bytes). <paramref name="time"/> e <paramref name="duration"/> em segundos desde o início do trecho.</summary>
    public void WriteFrame(byte[] bgra, double time, double duration)
    {
        var writer = _writer ?? throw new ObjectDisposedException(nameof(Mp4Writer));
        var length = _width * _height * 4;
        if (bgra.Length < length)
        {
            throw new ArgumentException("Quadro menor que o tamanho do vídeo.");
        }

        Check(MFCreateMemoryBuffer(length, out var buffer));
        Check(MFCreateSample(out var sample));
        try
        {
            Check(buffer.Lock(out var pointer, out _, out _));
            try
            {
                Marshal.Copy(bgra, 0, pointer, length);
            }
            finally
            {
                buffer.Unlock();
            }

            Check(buffer.SetCurrentLength(length));
            Check(sample.AddBuffer(buffer));
            Check(sample.SetSampleTime((long)(time * Second)));
            Check(sample.SetSampleDuration(Math.Max(1, (long)(duration * Second))));
            Check(writer.WriteSample(_stream, sample));
        }
        finally
        {
            Marshal.ReleaseComObject(sample);
            Marshal.ReleaseComObject(buffer);
        }
    }

    /// <summary>Fecha o arquivo (grava o índice do .mp4). Sem isso o vídeo não abre.</summary>
    public void Finish()
    {
        if (_finished || _writer is null)
        {
            return;
        }

        _finished = true;
        Check(_writer.Finalize_());
    }

    public void Dispose()
    {
        if (_writer is not null)
        {
            Marshal.ReleaseComObject(_writer);
            _writer = null;
        }
    }

    private static void Startup()
    {
        lock (StartLock)
        {
            if (!_started)
            {
                Check(MFStartup(0x00020070, 0)); // MF_VERSION, MFSTARTUP_FULL
                _started = true;
            }
        }
    }

    private static void Check(int hr)
    {
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    private static class Guids
    {
        public static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        public static readonly Guid SubType = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        public static readonly Guid Video = new("73646976-0000-0010-8000-00AA00389B71");
        public static readonly Guid H264 = new("34363248-0000-0010-8000-00AA00389B71");
        public static readonly Guid Rgb32 = new("00000016-0000-0010-8000-00AA00389B71");
        public static readonly Guid AvgBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
        public static readonly Guid InterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        public static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
        public static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        public static readonly Guid PixelAspectRatio = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
        public static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
        public static readonly Guid RateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
        public static readonly Guid Quality = new("fcbf57a3-7ea5-4b0c-9644-69b40c39c391");
        public static readonly Guid GopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    }

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFStartup(int version, int flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateAttributes(out IMFAttributes attributes, int initialSize);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMediaType(out IMFAttributes mediaType);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMemoryBuffer(int maxLength, out IMFMediaBuffer buffer);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateSample(out IMFSample sample);

    [DllImport("mfreadwrite.dll", ExactSpelling = true)]
    private static extern int MFCreateSinkWriterFromURL([MarshalAs(UnmanagedType.LPWStr)] string url, IntPtr byteStream, IMFAttributes attributes, out IMFSinkWriter writer);

    // IMFMediaType herda de IMFAttributes; os métodos próprios dele não são usados, então ele é tratado como IMFAttributes.
    // Os métodos não usados ficam como "_N" só para manter a ordem da tabela virtual.
    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFAttributes
    {
        void _01(); void _02(); void _03(); void _04(); void _05();
        [PreserveSig] int GetUINT64([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out ulong value);
        void _07(); void _08(); void _09();
        void _10(); void _11(); void _12(); void _13(); void _14(); void _15(); void _16(); void _17(); void _18();
        [PreserveSig] int SetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, uint value);
        [PreserveSig] int SetUINT64([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, ulong value);
        void _21();
        [PreserveSig] int SetGUID([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, [In, MarshalAs(UnmanagedType.LPStruct)] Guid value);
        void _23(); void _24(); void _25(); void _26(); void _27(); void _28(); void _29(); void _30();
    }

    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSample
    {
        void _01(); void _02(); void _03(); void _04(); void _05(); void _06(); void _07(); void _08(); void _09(); void _10();
        void _11(); void _12(); void _13(); void _14(); void _15(); void _16(); void _17(); void _18(); void _19(); void _20();
        void _21(); void _22(); void _23(); void _24(); void _25(); void _26(); void _27(); void _28(); void _29(); void _30();
        void GetSampleFlags();
        void SetSampleFlags();
        [PreserveSig] int GetSampleTime(out long time);
        [PreserveSig] int SetSampleTime(long time);
        [PreserveSig] int GetSampleDuration(out long duration);
        [PreserveSig] int SetSampleDuration(long duration);
        void GetBufferCount();
        void GetBufferByIndex();
        void ConvertToContiguousBuffer();
        [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
    }

    [ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr buffer, out int maxLength, out int currentLength);
        [PreserveSig] int Unlock();
        void GetCurrentLength();
        [PreserveSig] int SetCurrentLength(int length);
    }

    [ComImport, Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSinkWriter
    {
        [PreserveSig] int AddStream(IMFAttributes targetMediaType, out int streamIndex);
        [PreserveSig] int SetInputMediaType(int streamIndex, IMFAttributes inputMediaType, IMFAttributes? encodingParameters);
        [PreserveSig] int BeginWriting();
        [PreserveSig] int WriteSample(int streamIndex, IMFSample sample);
        void SendStreamTick();
        void PlaceMarker();
        void NotifyEndOfSegment();
        void Flush();
        [PreserveSig] int Finalize_();
    }
}
