using System.Runtime.InteropServices;
using Pontual.Terminal.Ipc;

namespace Pontual.Terminal.Shell;

/// <summary>
/// Aplica no Windows os comandos do mouse e do teclado que o TI mandou pela Central (visualização ao vivo).
/// Usa SendInput, a mesma porta que o próprio Windows usa para o mouse e o teclado de verdade, então o que
/// o TI faz vale onde a atendente poderia fazer — e para onde ela não pode (janela do UAC, tela de login,
/// Ctrl+Alt+Del) o Windows ignora, igual ao resto do bloqueio.
/// Nada é guardado: cada comando é aplicado na hora e esquecido.
/// </summary>
internal static class RemoteControl
{
    private static int _ok, _falhas, _total;
    private static int _ultimoErro;
    private static DateTime _ultimoLog = DateTime.MinValue;

    /// <summary>Resumo curto para a Central mostrar ao TI, sem precisar abrir o log da máquina.</summary>
    public static string DebugLine() => $"total={_total} ok={_ok} falhas={_falhas} erroWin32={_ultimoErro}";

    /// <summary>Aplica a lista na ordem em que o TI fez.</summary>
    public static void Apply(IReadOnlyList<InputCommand> events)
    {
        foreach (var command in events)
        {
            try
            {
                _total++;
                Apply(command);
            }
            catch (Exception ex)
            {
                _falhas++;
                Log.Error("controle-aplicar", ex);
            }
        }

        // Resumo no log a cada ~2 s: quantos comandos chegaram, quantos o SendInput aceitou e o último erro do Windows.
        // Serve para o diagnóstico: se chegam comandos mas sent=0, o Windows está recusando a injeção nesta máquina.
        if (DateTime.UtcNow - _ultimoLog > TimeSpan.FromSeconds(2))
        {
            _ultimoLog = DateTime.UtcNow;
            Log.Info($"controle: total={_total} enviados_ok={_ok} falhas={_falhas} ultimoErroWin32={_ultimoErro}");
        }
    }

    private static void Apply(InputCommand command)
    {
        switch (command.Type)
        {
            case "move":
                Mouse(MouseeventfMove | MouseeventfAbsolute | MouseeventfVirtualDesk, AbsoluteX(command.X), AbsoluteY(command.Y), 0);
                break;
            case "down":
                Mouse(DownFlag(command.Button), 0, 0, 0);
                break;
            case "up":
                Mouse(UpFlag(command.Button), 0, 0, 0);
                break;
            case "wheel":
                Mouse(MouseeventfWheel, 0, 0, command.Delta);
                break;
            case "key":
                Key((ushort)command.Key, down: true);
                break;
            case "keyup":
                Key((ushort)command.Key, down: false);
                break;
            case "text":
                foreach (var ch in command.Text ?? "")
                {
                    Unicode(ch);
                }

                break;
        }
    }

    // Com MOUSEEVENTF_VIRTUALDESK, as coordenadas de 0 a 65535 cobrem a área de todos os monitores por inteiro.
    // A Central manda a fração (0 a 1) sobre a mesma imagem da tela cheia, então a conversão é direta.
    private static int AbsoluteX(double x) => (int)Math.Round(Math.Clamp(x, 0, 1) * 65535.0);

    private static int AbsoluteY(double y) => (int)Math.Round(Math.Clamp(y, 0, 1) * 65535.0);

    private static uint DownFlag(int button) => button switch
    {
        1 => MouseeventfMiddleDown,
        2 => MouseeventfRightDown,
        _ => MouseeventfLeftDown,
    };

    private static uint UpFlag(int button) => button switch
    {
        1 => MouseeventfMiddleUp,
        2 => MouseeventfRightUp,
        _ => MouseeventfLeftUp,
    };

    private static void Dispatch(Input[] inputs)
    {
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent == inputs.Length)
        {
            _ok++;
        }
        else
        {
            _falhas++;
            _ultimoErro = Marshal.GetLastWin32Error();
        }
    }

    private static void Mouse(uint flags, int x, int y, int data)
    {
        var input = new Input
        {
            Type = InputMouse,
            U = new InputUnion { Mouse = new MouseInput { Dx = x, Dy = y, MouseData = (uint)data, DwFlags = flags } },
        };
        Dispatch([input]);
    }

    private static void Key(ushort vk, bool down)
    {
        var input = new Input
        {
            Type = InputKeyboard,
            U = new InputUnion { Keyboard = new KeybdInput { WVk = vk, DwFlags = down ? 0 : KeyeventfKeyup } },
        };
        Dispatch([input]);
    }

    private static void Unicode(char ch)
    {
        // KEYEVENTF_UNICODE digita o caractere direto, sem depender do layout de teclado da máquina.
        Input Make(uint flags) => new()
        {
            Type = InputKeyboard,
            U = new InputUnion { Keyboard = new KeybdInput { WVk = 0, WScan = ch, DwFlags = KeyeventfUnicode | flags } },
        };
        Dispatch([Make(0), Make(KeyeventfKeyup)]);
    }

    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;

    private const uint MouseeventfMove = 0x0001;
    private const uint MouseeventfLeftDown = 0x0002;
    private const uint MouseeventfLeftUp = 0x0004;
    private const uint MouseeventfRightDown = 0x0008;
    private const uint MouseeventfRightUp = 0x0010;
    private const uint MouseeventfMiddleDown = 0x0020;
    private const uint MouseeventfMiddleUp = 0x0040;
    private const uint MouseeventfWheel = 0x0800;
    private const uint MouseeventfAbsolute = 0x8000;
    private const uint MouseeventfVirtualDesk = 0x4000;

    private const uint KeyeventfKeyup = 0x0002;
    private const uint KeyeventfUnicode = 0x0004;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeybdInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint DwFlags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeybdInput
    {
        public ushort WVk;
        public ushort WScan;
        public uint DwFlags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }
}
