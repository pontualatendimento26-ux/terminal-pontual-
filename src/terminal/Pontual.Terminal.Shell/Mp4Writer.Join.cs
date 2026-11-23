using System.Runtime.InteropServices;

namespace Pontual.Terminal.Shell;

/// <summary>
/// Junta os pedaços de 1 minuto de um dia num único .mp4, sem recodificar (só copia os quadros já comprimidos).
/// Cada pedaço entra na posição do relógio em que foi gravado: se o computador ficou desligado das 12h às 13h,
/// o vídeo do dia fica parado nesse intervalo (a Central mostra o trecho em cinza e pula).
/// Os pedaços precisam ter o mesmo tamanho de tela; <see cref="TryAppend"/> devolve falso quando muda,
/// e quem chama começa outro arquivo.
/// </summary>
internal sealed class Mp4Joiner : IDisposable
{
    private readonly string _path;
    private Mp4Writer.Remux? _remux;

    public Mp4Joiner(string path) => _path = path;

    /// <summary>Pedaços gravados: início (segundos desde o começo do vídeo) e duração.</summary>
    public List<(double Offset, double Seconds)> Parts { get; } = [];

    public bool IsEmpty => _remux is null;

    /// <summary>Acrescenta um pedaço começando em <paramref name="offset"/> segundos. Falso = tamanho de tela diferente.</summary>
    public bool TryAppend(string segment, double offset)
    {
        var seconds = (_remux ??= new Mp4Writer.Remux(_path)).Append(segment, offset);
        if (seconds is null)
        {
            return false;
        }

        if (seconds > 0)
        {
            // Pedaços colados (gravação contínua) viram um só trecho, para a lista ficar curta.
            if (Parts.Count > 0 && offset - (Parts[^1].Offset + Parts[^1].Seconds) < 2.5)
            {
                Parts[^1] = (Parts[^1].Offset, offset + seconds.Value - Parts[^1].Offset);
            }
            else
            {
                Parts.Add((offset, seconds.Value));
            }
        }

        return true;
    }

    public void Finish() => _remux?.Finish();

    public void Dispose() => _remux?.Dispose();
}

internal sealed partial class Mp4Writer
{
    private const int FirstVideoStream = unchecked((int)0xFFFFFFFC);
    private const int AllStreams = unchecked((int)0xFFFFFFFE);
    private const int EndOfStream = 0x2;

    /// <summary>Cópia dos quadros comprimidos de vários .mp4 para um só (passthrough do Media Foundation).</summary>
    internal sealed class Remux : IDisposable
    {
        private readonly string _path;
        private IMFSinkWriter? _writer;
        private int _stream;
        private ulong _frameSize;
        private IMFSample? _pending;
        private long _pendingTime = -1;
        private bool _finished;

        public Remux(string path)
        {
            _path = path;
            Startup();
        }

        /// <summary>Devolve os segundos copiados, ou null se o tamanho de tela não bate com o do arquivo.</summary>
        public double? Append(string segment, double offset)
        {
            Check(MFCreateSourceReaderFromURL(segment, null, out var reader));
            try
            {
                Check(reader.SetStreamSelection(AllStreams, false));
                Check(reader.SetStreamSelection(FirstVideoStream, true));
                Check(reader.GetCurrentMediaType(FirstVideoStream, out var type));
                try
                {
                    Check(type.GetUINT64(Guids.FrameSize, out var size));
                    if (_writer is null)
                    {
                        Check(MFCreateSinkWriterFromURL(_path, IntPtr.Zero, null!, out var writer));
                        _writer = writer;
                        Check(writer.AddStream(type, out _stream));
                        Check(writer.SetInputMediaType(_stream, type, null));
                        Check(writer.BeginWriting());
                        _frameSize = size;
                    }
                    else if (size != _frameSize)
                    {
                        return null;
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(type);
                }

                var start = (long)(offset * Second);
                long end = start;
                while (true)
                {
                    Check(reader.ReadSample(FirstVideoStream, 0, out _, out var flags, out var timestamp, out var sample));
                    if (sample is null)
                    {
                        if ((flags & EndOfStream) != 0)
                        {
                            break;
                        }

                        continue;
                    }

                    var at = start + timestamp;
                    if (at <= _pendingTime)
                    {
                        Marshal.ReleaseComObject(sample); // relógio voltou (pedaço sobreposto): descarta
                    }
                    else
                    {
                        Check(sample.GetSampleDuration(out var duration));
                        Check(sample.SetSampleTime(at));
                        Push(sample, at);
                        end = Math.Max(end, at + Math.Max(duration, 1));
                    }

                    if ((flags & EndOfStream) != 0)
                    {
                        break;
                    }
                }

                return (end - start) / (double)Second;
            }
            finally
            {
                Marshal.ReleaseComObject(reader);
            }
        }

        // Cada quadro só é gravado quando chega o seguinte: assim a duração dele vai até o próximo,
        // e o tempo do vídeo acompanha o relógio mesmo com intervalos (computador desligado).
        private void Push(IMFSample sample, long at)
        {
            if (_pending is { } previous)
            {
                Check(previous.SetSampleDuration(at - _pendingTime));
                Check(_writer!.WriteSample(_stream, previous));
                Marshal.ReleaseComObject(previous);
            }

            _pending = sample;
            _pendingTime = at;
        }

        public void Finish()
        {
            if (_finished || _writer is null)
            {
                return;
            }

            _finished = true;
            if (_pending is { } last)
            {
                Check(_writer.WriteSample(_stream, last));
                Marshal.ReleaseComObject(last);
                _pending = null;
            }

            Check(_writer.Finalize_());
        }

        public void Dispose()
        {
            if (_pending is not null)
            {
                Marshal.ReleaseComObject(_pending);
                _pending = null;
            }

            if (_writer is not null)
            {
                Marshal.ReleaseComObject(_writer);
                _writer = null;
            }
        }
    }

    [DllImport("mfreadwrite.dll", ExactSpelling = true)]
    private static extern int MFCreateSourceReaderFromURL([MarshalAs(UnmanagedType.LPWStr)] string url, IMFAttributes? attributes, out IMFSourceReader reader);

    [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSourceReader
    {
        void GetStreamSelection();
        [PreserveSig] int SetStreamSelection(int streamIndex, [MarshalAs(UnmanagedType.Bool)] bool selected);
        void GetNativeMediaType();
        [PreserveSig] int GetCurrentMediaType(int streamIndex, out IMFAttributes mediaType);
        void SetCurrentMediaType();
        void SetCurrentPosition();
        [PreserveSig] int ReadSample(int streamIndex, int controlFlags, out int actualStreamIndex, out int streamFlags, out long timestamp, out IMFSample? sample);
    }
}
