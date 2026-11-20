using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace WinControl;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    [STAThread]
    private static int Main(string[] args)
    {
        Native.SetDpiAware();

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return 0;
        }

        if (args[0] is "--mcp" or "mcp")
        {
            return McpServer.Run();
        }

        try
        {
            var cmd = args[0].ToLowerInvariant();
            var rest = args.Skip(1).ToArray();

            return cmd switch
            {
                "windows" => PrintOk(GetWindowsJson(ParseOpt(rest, "--filter"))),
                "screenshot" => CmdScreenshot(rest),
                "focus" => rest.Length < 1 ? Fail("Uso: focus <hwnd|titulo>") : PrintOk(FocusTarget(rest[0])),
                "click" => CmdClick(rest),
                "drag" => CmdDrag(rest),
                "scroll" => CmdScroll(rest),
                "type" => rest.Length < 1 ? Fail("Uso: type <texto>") : PrintOk(TypeText(string.Join(" ", rest))),
                "key" => rest.Length < 1 ? Fail("Uso: key <combo>") : PrintOk(SendKeyCombo(rest[0])),
                "launch" => rest.Length < 1 ? Fail("Uso: launch <ruta> [args...]") : PrintOk(LaunchTarget(rest[0], rest.Length > 1 ? string.Join(" ", rest.Skip(1)) : "")),
                "controls" => rest.Length < 1 ? Fail("Uso: controls <hwnd|titulo>") : PrintOk(GetControlsJson(rest[0])),
                "screens" => PrintOk(GetScreensJson()),
                _ => Fail($"Comando desconocido: {cmd}")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
    }

    private static int PrintOk(string json)
    {
        Console.WriteLine(json);
        return 0;
    }

    private static int Fail(string msg)
    {
        Console.Error.WriteLine($"ERROR: {msg}");
        return 1;
    }

    private static string? ParseOpt(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == flag) return args[i + 1];
        return null;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
win-control - Utilidad local y servidor MCP de inspeccion y control de escritorio Windows

Modos:
  --mcp                                    Inicia como servidor MCP (stdio JSON-RPC 2.0)

Comandos CLI:
  screens                                  Lista monitores y resoluciones fisicas
  windows [--filter <texto>]               Lista ventanas visibles con HWND, PID, proceso y bounds
  controls <hwnd|titulo>                   Lista controles hijos Win32 de una ventana
  focus <hwnd|titulo>                      Restaura y enfoca una ventana
  screenshot <ruta.png> [--hwnd <id|tit>]  Captura pantalla completa (o ventana) a PNG
  click <x> <y> [--right|--double|--middle] Clic en coordenadas fisicas de pantalla
  drag <x1> <y1> <x2> <y2> [--steps 15]    Arrastra el cursor entre dos puntos
  scroll <x> <y> <delta>                   Rueda del raton (+ arriba, - abajo; ej: -360)
  type <texto>                             Escribe texto Unicode en la ventana activa
  key <combo>                              Envia teclas/atajos (ej: Enter, Ctrl+S, Alt+F4)
  launch <ruta_o_comando> [args...]        Abre app/carpeta/acceso directo via Shell (persistente)
""");
    }

    internal static string GetScreensJson()
    {
        var list = Screen.AllScreens.Select((s, i) => new
        {
            Index = i,
            s.DeviceName,
            s.Primary,
            s.Bounds.X,
            s.Bounds.Y,
            s.Bounds.Width,
            s.Bounds.Height
        });
        return JsonSerializer.Serialize(list, JsonOpts);
    }

    internal static string GetWindowsJson(string? filter)
    {
        var fg = Native.GetForegroundWindow();
        var wins = GetVisibleWindows()
            .Where(w => string.IsNullOrEmpty(filter)
                        || w.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || w.ProcessName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Select(w => new
            {
                Hwnd = $"0x{w.Hwnd.ToInt64():X}",
                w.Pid,
                w.ProcessName,
                w.Title,
                Active = w.Hwnd == fg,
                Minimized = Native.IsIconic(w.Hwnd),
                Maximized = Native.IsZoomed(w.Hwnd),
                w.X,
                w.Y,
                w.Width,
                w.Height
            });

        return JsonSerializer.Serialize(wins, JsonOpts);
    }

    internal static string GetControlsJson(string target)
    {
        var win = ResolveWindow(target) ?? throw new InvalidOperationException($"No se encontro ventana para: {target}");

        var children = new List<object>();
        Native.EnumChildWindows(win.Hwnd, (hChild, _) =>
        {
            if (!Native.IsWindowVisible(hChild)) return true;
            var cls = Native.GetClassNameText(hChild);
            var txt = Native.GetWindowTextString(hChild);
            Native.GetWindowRect(hChild, out var r);
            if (r.Right - r.Left <= 0 || r.Bottom - r.Top <= 0) return true;
            children.Add(new
            {
                Hwnd = $"0x{hChild.ToInt64():X}",
                Class = cls,
                Text = txt,
                X = r.Left,
                Y = r.Top,
                Width = r.Right - r.Left,
                Height = r.Bottom - r.Top
            });
            return children.Count < 200;
        }, IntPtr.Zero);

        return JsonSerializer.Serialize(new
        {
            Parent = new { Hwnd = $"0x{win.Hwnd.ToInt64():X}", win.Title, win.ProcessName },
            Count = children.Count,
            Controls = children
        }, JsonOpts);
    }

    internal static string FocusTarget(string target)
    {
        var win = ResolveWindow(target) ?? throw new InvalidOperationException($"No se encontro ventana para: {target}");

        if (Native.IsIconic(win.Hwnd))
            Native.ShowWindow(win.Hwnd, Native.SW_RESTORE);
        else
            Native.ShowWindow(win.Hwnd, Native.SW_SHOW);

        var fg = Native.GetForegroundWindow();
        uint curThread = Native.GetCurrentThreadId();
        uint fgThread = fg != IntPtr.Zero ? Native.GetWindowThreadProcessId(fg, out _) : 0;
        uint targetThread = Native.GetWindowThreadProcessId(win.Hwnd, out _);

        bool attachedFg = fgThread != 0 && fgThread != curThread && Native.AttachThreadInput(curThread, fgThread, true);
        bool attachedTarget = targetThread != 0 && targetThread != curThread && Native.AttachThreadInput(curThread, targetThread, true);
        try
        {
            Native.BringWindowToTop(win.Hwnd);
            Native.SetForegroundWindow(win.Hwnd);
        }
        finally
        {
            if (attachedTarget) Native.AttachThreadInput(curThread, targetThread, false);
            if (attachedFg) Native.AttachThreadInput(curThread, fgThread, false);
        }

        return JsonSerializer.Serialize(new
        {
            Focused = true,
            Hwnd = $"0x{win.Hwnd.ToInt64():X}",
            win.Title,
            win.ProcessName
        }, JsonOpts);
    }

    internal static Rectangle ResolveCaptureBounds(string? hwndOrTitle, int? monitorIdx)
    {
        if (!string.IsNullOrEmpty(hwndOrTitle))
        {
            var win = ResolveWindow(hwndOrTitle) ?? throw new InvalidOperationException($"No se encontro ventana para: {hwndOrTitle}");
            return new Rectangle(win.X, win.Y, Math.Max(1, win.Width), Math.Max(1, win.Height));
        }
        if (monitorIdx.HasValue)
        {
            var screens = Screen.AllScreens;
            if (monitorIdx.Value < 0 || monitorIdx.Value >= screens.Length)
                throw new ArgumentOutOfRangeException(nameof(monitorIdx), $"Monitor invalido: {monitorIdx.Value} (hay {screens.Length})");
            return screens[monitorIdx.Value].Bounds;
        }
        return SystemInformation.VirtualScreen;
    }

    private static int CmdScreenshot(string[] args)
    {
        if (args.Length < 1) return Fail("Uso: screenshot <ruta.png> [--hwnd <id|titulo>] [--monitor <idx>]");
        string outPath = Path.GetFullPath(args[0]);
        string? hwndArg = ParseOpt(args, "--hwnd");
        int? monitorIdx = int.TryParse(ParseOpt(args, "--monitor"), out var m) ? m : null;

        Rectangle bounds = ResolveCaptureBounds(hwndArg, monitorIdx);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        using var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        }
        bmp.Save(outPath, ImageFormat.Png);

        return PrintOk(JsonSerializer.Serialize(new
        {
            Saved = outPath,
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height
        }, JsonOpts));
    }

    internal static string ClickAt(int x, int y, string button)
    {
        string btn = button.ToLowerInvariant();
        Native.SetCursorPos(x, y);
        Thread.Sleep(40);

        switch (btn)
        {
            case "right":
                Native.SendMouse(Native.MOUSEEVENTF_RIGHTDOWN);
                Thread.Sleep(25);
                Native.SendMouse(Native.MOUSEEVENTF_RIGHTUP);
                break;
            case "middle":
                Native.SendMouse(Native.MOUSEEVENTF_MIDDLEDOWN);
                Thread.Sleep(25);
                Native.SendMouse(Native.MOUSEEVENTF_MIDDLEUP);
                break;
            case "double":
                for (int i = 0; i < 2; i++)
                {
                    Native.SendMouse(Native.MOUSEEVENTF_LEFTDOWN);
                    Thread.Sleep(25);
                    Native.SendMouse(Native.MOUSEEVENTF_LEFTUP);
                    if (i == 0) Thread.Sleep(60);
                }
                break;
            default:
                Native.SendMouse(Native.MOUSEEVENTF_LEFTDOWN);
                Thread.Sleep(25);
                Native.SendMouse(Native.MOUSEEVENTF_LEFTUP);
                break;
        }

        return JsonSerializer.Serialize(new { Clicked = true, x, y, button = btn }, JsonOpts);
    }

    private static int CmdClick(string[] args)
    {
        if (args.Length < 2 || !int.TryParse(args[0], out int x) || !int.TryParse(args[1], out int y))
            return Fail("Uso: click <x> <y> [--right|--double|--middle]");
        string btn = args.Contains("--right") ? "right"
            : args.Contains("--middle") ? "middle"
            : args.Contains("--double") ? "double"
            : "left";
        return PrintOk(ClickAt(x, y, btn));
    }

    internal static string DragBetween(int x1, int y1, int x2, int y2, int steps)
    {
        steps = Math.Clamp(steps, 2, 100);
        Native.SetCursorPos(x1, y1);
        Thread.Sleep(40);
        Native.SendMouse(Native.MOUSEEVENTF_LEFTDOWN);
        Thread.Sleep(30);

        for (int i = 1; i <= steps; i++)
        {
            int cx = x1 + (x2 - x1) * i / steps;
            int cy = y1 + (y2 - y1) * i / steps;
            Native.SetCursorPos(cx, cy);
            Thread.Sleep(15);
        }

        Native.SendMouse(Native.MOUSEEVENTF_LEFTUP);
        return JsonSerializer.Serialize(new { Dragged = true, x1, y1, x2, y2, steps }, JsonOpts);
    }

    private static int CmdDrag(string[] args)
    {
        if (args.Length < 4
            || !int.TryParse(args[0], out int x1)
            || !int.TryParse(args[1], out int y1)
            || !int.TryParse(args[2], out int x2)
            || !int.TryParse(args[3], out int y2))
            return Fail("Uso: drag <x1> <y1> <x2> <y2> [--steps 15]");
        int steps = int.TryParse(ParseOpt(args, "--steps"), out var s) ? s : 15;
        return PrintOk(DragBetween(x1, y1, x2, y2, steps));
    }

    internal static string ScrollAt(int x, int y, int delta)
    {
        Native.SetCursorPos(x, y);
        Thread.Sleep(30);
        Native.SendMouseWheel(delta);
        return JsonSerializer.Serialize(new { Scrolled = true, x, y, delta }, JsonOpts);
    }

    private static int CmdScroll(string[] args)
    {
        if (args.Length < 3
            || !int.TryParse(args[0], out int x)
            || !int.TryParse(args[1], out int y)
            || !int.TryParse(args[2], out int delta))
            return Fail("Uso: scroll <x> <y> <delta>");
        return PrintOk(ScrollAt(x, y, delta));
    }

    internal static string TypeText(string text)
    {
        Native.SendUnicodeText(text);
        return JsonSerializer.Serialize(new { Typed = true, Length = text.Length }, JsonOpts);
    }

    internal static string SendKeyCombo(string combo)
    {
        var parts = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var vks = new List<ushort>();
        foreach (var p in parts)
        {
            if (!TryParseKey(p, out var vk))
                throw new ArgumentException($"Tecla desconocida: {p}");
            vks.Add(vk);
        }

        foreach (var vk in vks) Native.SendKey(vk, false);
        Thread.Sleep(30);
        for (int i = vks.Count - 1; i >= 0; i--) Native.SendKey(vks[i], true);

        return JsonSerializer.Serialize(new { KeySent = combo }, JsonOpts);
    }

    internal static string LaunchTarget(string target, string launchArgs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = target,
            Arguments = launchArgs,
            UseShellExecute = true
        };
        var proc = Process.Start(psi);
        return JsonSerializer.Serialize(new
        {
            Launched = true,
            Pid = proc?.Id,
            Target = target,
            Args = launchArgs
        }, JsonOpts);
    }

    private static WindowInfo? ResolveWindow(string spec)
    {
        var wins = GetVisibleWindows();
        if (spec.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && long.TryParse(spec[2..], System.Globalization.NumberStyles.HexNumber, null, out var hex))
        {
            var ptr = new IntPtr(hex);
            return wins.FirstOrDefault(w => w.Hwnd == ptr);
        }
        if (long.TryParse(spec, out var dec))
        {
            var ptr = new IntPtr(dec);
            var byPtr = wins.FirstOrDefault(w => w.Hwnd == ptr);
            if (byPtr != null) return byPtr;
        }
        return wins.FirstOrDefault(w => w.Title.Contains(spec, StringComparison.OrdinalIgnoreCase))
               ?? wins.FirstOrDefault(w => w.ProcessName.Contains(spec, StringComparison.OrdinalIgnoreCase));
    }

    private static List<WindowInfo> GetVisibleWindows()
    {
        var result = new List<WindowInfo>();
        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd)) return true;
            var title = Native.GetWindowTextString(hwnd);
            if (string.IsNullOrWhiteSpace(title)) return true;
            Native.GetWindowRect(hwnd, out var r);
            if (r.Right - r.Left <= 0 || r.Bottom - r.Top <= 0) return true;

            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            string procName = "";
            try { procName = Process.GetProcessById((int)pid).ProcessName; } catch { }

            result.Add(new WindowInfo(hwnd, (int)pid, procName, title, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static bool TryParseKey(string name, out ushort vk)
    {
        vk = name.ToUpperInvariant() switch
        {
            "CTRL" or "CONTROL" => 0x11,
            "ALT" or "MENU" => 0x12,
            "SHIFT" => 0x10,
            "WIN" or "LWIN" => 0x5B,
            "ENTER" or "RETURN" => 0x0D,
            "ESC" or "ESCAPE" => 0x1B,
            "TAB" => 0x09,
            "SPACE" => 0x20,
            "BACKSPACE" or "BS" => 0x08,
            "DELETE" or "DEL" => 0x2E,
            "INSERT" or "INS" => 0x2D,
            "HOME" => 0x24,
            "END" => 0x23,
            "PGUP" or "PAGEUP" => 0x21,
            "PGDN" or "PAGEDOWN" => 0x22,
            "UP" => 0x26,
            "DOWN" => 0x28,
            "LEFT" => 0x25,
            "RIGHT" => 0x27,
            "F1" => 0x70, "F2" => 0x71, "F3" => 0x72, "F4" => 0x73,
            "F5" => 0x74, "F6" => 0x75, "F7" => 0x76, "F8" => 0x77,
            "F9" => 0x78, "F10" => 0x79, "F11" => 0x7A, "F12" => 0x7B,
            _ => (ushort)0
        };
        if (vk != 0) return true;
        if (name.Length == 1)
        {
            char c = char.ToUpperInvariant(name[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                vk = c;
                return true;
            }
        }
        return false;
    }

    private sealed record WindowInfo(IntPtr Hwnd, int Pid, string ProcessName, string Title, int X, int Y, int Width, int Height);
}

internal static class Native
{
    public const int SW_SHOW = 5;
    public const int SW_RESTORE = 9;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    public const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    public const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    public static void SetDpiAware()
    {
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
    }

    public static string GetWindowTextString(IntPtr hWnd)
    {
        int len = GetWindowTextLength(hWnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string GetClassNameText(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static void SendMouse(uint flags)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags } }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    public static void SendMouseWheel(int delta)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_WHEEL, mouseData = unchecked((uint)delta) } }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    public static void SendKey(ushort vk, bool keyUp)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    dwFlags = keyUp ? KEYEVENTF_KEYUP : 0
                }
            }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    public static void SendUnicodeText(string text)
    {
        var list = new List<INPUT>(text.Length * 2);
        foreach (char c in text)
        {
            list.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE } }
            });
            list.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } }
            });
        }
        if (list.Count > 0)
            SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf<INPUT>());
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL, wParamH;
    }
}
