using System.Diagnostics;
using System.Drawing;
using System.IO;
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
                "hover" => CmdHover(rest),
                "drag" => CmdDrag(rest),
                "scroll" => CmdScroll(rest),
                "type" => rest.Length < 1 ? Fail("Uso: type <texto>") : PrintOk(TypeText(string.Join(" ", rest))),
                "key" => rest.Length < 1 ? Fail("Uso: key <combo>") : PrintOk(SendKeyCombo(rest[0])),
                "launch" => rest.Length < 1 ? Fail("Uso: launch <ruta> [args...]") : PrintOk(LaunchTarget(rest[0], rest.Length > 1 ? string.Join(" ", rest.Skip(1)) : "")),
                "controls" => rest.Length < 1 ? Fail("Uso: controls <hwnd|titulo>") : PrintOk(GetControlsJson(rest[0])),
                "screens" => PrintOk(GetScreensJson()),
                "uia" => CmdUia(rest),
                "uia-act" => CmdUiaAct(rest),
                "focused" => PrintOk(AccessibilityReader.GetFocusedJson()),
                "at" => CmdElementAt(rest),
                "watch" => CmdWatch(),
                "ocr" => CmdOcr(rest),
                "clipboard" => CmdClipboard(rest),
                "window" => CmdWindowManage(rest),
                "wait" => CmdWaitWindow(rest),
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
win-control v2.0 - Utilidad local y servidor MCP de accesibilidad y control de escritorio Windows

Modos:
  --mcp                                     Inicia como servidor MCP (stdio JSON-RPC 2.0)

Lectura y accesibilidad:
  uia [target] [--depth 6] [--max 250] [--filter txt] [--type ControlType] [--timeout 4000]
  uia-act <invoke|click|set_value|toggle|select|expand|collapse|focus> [--target win] [--name txt] [--id autoId] [--type ControlType] [--value txt] [--index 0]
  focused                                   Elemento actualmente enfocado en Windows
  at [x y]                                  Elemento accesible en (x, y) o bajo el cursor actual
  watch                                     Streaming en tiempo real (NDJSON): foco, ventanas y regiones dinamicas
  ocr [--target win] [--monitor idx] [--region x,y,w,h] [--lang es-ES]

Ventanas y sistema:
  screens                                   Lista monitores y resoluciones fisicas
  windows [--filter txt]                    Lista ventanas visibles con HWND, PID, proceso y bounds
  controls <hwnd|titulo>                    Lista controles hijos Win32 de una ventana
  focus <hwnd|titulo>                       Restaura y enfoca una ventana
  window <target> <minimize|maximize|restore|close|move> [x y w h]
  wait <target> [--timeout 10000]           Espera a que aparezca una ventana
  clipboard [get | set <texto>]             Lee o escribe el portapapeles
  launch <ruta_o_comando> [args...]         Abre app/carpeta/acceso directo via Shell

Captura y entrada fisica:
  screenshot <ruta> [--hwnd id|tit] [--monitor idx] [--region x,y,w,h] [--jpeg] [--quality 80] [--screen]
  click <x> <y> [--right|--double|--middle]
  hover <x> <y>
  drag <x1> <y1> <x2> <y2> [--steps 15]
  scroll <x> <y> <delta>
  type <texto>
  key <combo>
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
            win.ProcessName,
            UipiWarning = Native.GetUipiWarning(win.Hwnd)
        }, JsonOpts);
    }

    internal static string ManageWindowJson(string target, string action, int? x, int? y, int? width, int? height)
    {
        var win = ResolveWindow(target) ?? throw new InvalidOperationException($"No se encontro ventana para: {target}");
        string act = action.Trim().ToLowerInvariant();

        switch (act)
        {
            case "minimize":
                Native.ShowWindow(win.Hwnd, Native.SW_MINIMIZE);
                break;
            case "maximize":
                Native.ShowWindow(win.Hwnd, Native.SW_MAXIMIZE);
                break;
            case "restore":
                Native.ShowWindow(win.Hwnd, Native.SW_RESTORE);
                break;
            case "close":
                Native.PostMessage(win.Hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                break;
            case "move":
            case "move_resize":
                if (!x.HasValue || !y.HasValue || !width.HasValue || !height.HasValue || width <= 0 || height <= 0)
                    throw new ArgumentException("move_resize requiere x, y, width (>0) y height (>0).");
                Native.ShowWindow(win.Hwnd, Native.SW_RESTORE);
                Native.MoveWindow(win.Hwnd, x.Value, y.Value, width.Value, height.Value, true);
                break;
            default:
                throw new ArgumentException($"Accion invalida: '{action}'. Usa minimize, maximize, restore, close o move_resize.");
        }

        return JsonSerializer.Serialize(new
        {
            Success = true,
            Action = act,
            Hwnd = $"0x{win.Hwnd.ToInt64():X}",
            win.Title,
            win.ProcessName,
            UipiWarning = Native.GetUipiWarning(win.Hwnd)
        }, JsonOpts);
    }

    internal static string WaitWindowJson(string target, int timeoutMs)
    {
        timeoutMs = Math.Clamp(timeoutMs, 100, 60000);
        var sw = Stopwatch.StartNew();

        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var win = ResolveWindow(target);
            if (win != null)
            {
                sw.Stop();
                return JsonSerializer.Serialize(new
                {
                    Found = true,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    Window = new
                    {
                        Hwnd = $"0x{win.Hwnd.ToInt64():X}",
                        win.Pid,
                        win.ProcessName,
                        win.Title,
                        win.X,
                        win.Y,
                        win.Width,
                        win.Height
                    }
                }, JsonOpts);
            }
            Thread.Sleep(150);
        }

        sw.Stop();
        return JsonSerializer.Serialize(new
        {
            Found = false,
            Target = target,
            ElapsedMs = sw.ElapsedMilliseconds
        }, JsonOpts);
    }

    internal static string ClipboardActionJson(string action, string? text)
    {
        string act = action.Trim().ToLowerInvariant();
        return act switch
        {
            "get" => RunOnSta(() =>
            {
                string content = Clipboard.ContainsText() ? Clipboard.GetText() : "";
                return JsonSerializer.Serialize(new { Action = "get", Length = content.Length, Text = content }, JsonOpts);
            }),
            "set" => RunOnSta(() =>
            {
                if (text == null) throw new ArgumentException("Falta el parametro 'text' para clipboard set.");
                if (text.Length == 0) Clipboard.Clear();
                else Clipboard.SetText(text);
                return JsonSerializer.Serialize(new { Action = "set", Length = text.Length, Success = true }, JsonOpts);
            }),
            _ => throw new ArgumentException($"Accion de portapapeles invalida: '{action}'. Usa get o set.")
        };
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
        if (args.Length < 1) return Fail("Uso: screenshot <ruta> [--hwnd <id|titulo>] [--monitor <idx>] [--region x,y,w,h] [--jpeg] [--quality 80] [--screen]");
        string outPath = Path.GetFullPath(args[0]);
        string? hwndArg = ParseOpt(args, "--hwnd");
        int? monitorIdx = int.TryParse(ParseOpt(args, "--monitor"), out var m) ? m : null;
        var region = ScreenCapture.ParseRegion(ParseOpt(args, "--region"));
        bool isJpeg = args.Contains("--jpeg") || outPath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || outPath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);
        int quality = int.TryParse(ParseOpt(args, "--quality"), out var q) ? q : 80;
        string mode = args.Contains("--screen") ? "screen" : "auto";

        var capture = ScreenCapture.CaptureBitmap(hwndArg, monitorIdx, region, mode);
        using var bmp = capture.Bitmap;
        var encoded = ScreenCapture.EncodeBitmap(bmp, 0, isJpeg ? "jpeg" : "png", quality);

        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllBytes(outPath, encoded.Bytes);

        return PrintOk(JsonSerializer.Serialize(new
        {
            Saved = outPath,
            Format = encoded.MimeType,
            CaptureMode = capture.ModeUsed,
            Bytes = encoded.Bytes.Length,
            capture.PhysicalBounds.Left,
            capture.PhysicalBounds.Top,
            capture.PhysicalBounds.Width,
            capture.PhysicalBounds.Height
        }, JsonOpts));
    }

    internal static string HoverAt(int x, int y)
    {
        Native.SetCursorPos(x, y);
        return JsonSerializer.Serialize(new { Hovered = true, x, y }, JsonOpts);
    }

    private static int CmdHover(string[] args)
    {
        if (args.Length < 2 || !int.TryParse(args[0], out int x) || !int.TryParse(args[1], out int y))
            return Fail("Uso: hover <x> <y>");
        return PrintOk(HoverAt(x, y));
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

        return JsonSerializer.Serialize(new
        {
            Clicked = true,
            x,
            y,
            button = btn,
            UipiWarning = Native.GetUipiWarning()
        }, JsonOpts);
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
        return JsonSerializer.Serialize(new
        {
            Dragged = true,
            x1,
            y1,
            x2,
            y2,
            steps,
            UipiWarning = Native.GetUipiWarning()
        }, JsonOpts);
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
        return JsonSerializer.Serialize(new
        {
            Scrolled = true,
            x,
            y,
            delta,
            UipiWarning = Native.GetUipiWarning()
        }, JsonOpts);
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
        return JsonSerializer.Serialize(new
        {
            Typed = true,
            Length = text.Length,
            UipiWarning = Native.GetUipiWarning()
        }, JsonOpts);
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

        return JsonSerializer.Serialize(new
        {
            KeySent = combo,
            UipiWarning = Native.GetUipiWarning()
        }, JsonOpts);
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

    internal static IntPtr ResolveTargetHwnd(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return Native.GetForegroundWindow();
        var win = ResolveWindow(target) ?? throw new InvalidOperationException($"No se encontro ventana para: {target}");
        return win.Hwnd;
    }

    private static int CmdUia(string[] args)
    {
        string? target = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : null;
        int depth = int.TryParse(ParseOpt(args, "--depth"), out var d) ? d : 6;
        int maxNodes = int.TryParse(ParseOpt(args, "--max"), out var m) ? m : 250;
        string? filter = ParseOpt(args, "--filter");
        string? cType = ParseOpt(args, "--type");
        int timeout = int.TryParse(ParseOpt(args, "--timeout"), out var t) ? t : 4000;
        return PrintOk(AccessibilityReader.GetUiTreeJson(target, depth, maxNodes, filter, cType, timeout));
    }

    private static int CmdUiaAct(string[] args)
    {
        if (args.Length < 1) return Fail("Uso: uia-act <action> [--target win] [--name txt] [--id autoId] [--type ControlType] [--value txt] [--index 0]");
        string action = args[0];
        string? target = ParseOpt(args, "--target");
        string? name = ParseOpt(args, "--name");
        string? id = ParseOpt(args, "--id");
        string? cType = ParseOpt(args, "--type");
        string? val = ParseOpt(args, "--value");
        int idx = int.TryParse(ParseOpt(args, "--index"), out var i) ? i : 0;
        int timeout = int.TryParse(ParseOpt(args, "--timeout"), out var t) ? t : 4000;
        return PrintOk(AccessibilityReader.PerformUiAction(target, action, name, id, cType, val, idx, timeout));
    }

    private static int CmdElementAt(string[] args)
    {
        int? x = args.Length >= 2 && int.TryParse(args[0], out int px) ? px : null;
        int? y = args.Length >= 2 && int.TryParse(args[1], out int py) ? py : null;
        return PrintOk(AccessibilityReader.GetElementAtPointJson(x, y));
    }

    private static int CmdWatch()
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        return AccessibilityReader.RunWatchCli(cts.Token);
    }

    private static int CmdOcr(string[] args)
    {
        string? target = ParseOpt(args, "--target");
        int? monitorIdx = int.TryParse(ParseOpt(args, "--monitor"), out var m) ? m : null;
        string? region = ParseOpt(args, "--region");
        string? lang = ParseOpt(args, "--lang");
        return PrintOk(OcrReader.RunOcrJson(target, monitorIdx, region, lang));
    }

    private static int CmdClipboard(string[] args)
    {
        string action = args.Length == 0 ? "get" : args[0];
        string? text = args.Length > 1 ? string.Join(" ", args.Skip(1)) : null;
        return PrintOk(ClipboardActionJson(action, text));
    }

    private static int CmdWindowManage(string[] args)
    {
        if (args.Length < 2) return Fail("Uso: window <target> <minimize|maximize|restore|close|move> [x y w h]");
        int? x = args.Length >= 6 && int.TryParse(args[2], out int px) ? px : null;
        int? y = args.Length >= 6 && int.TryParse(args[3], out int py) ? py : null;
        int? w = args.Length >= 6 && int.TryParse(args[4], out int pw) ? pw : null;
        int? h = args.Length >= 6 && int.TryParse(args[5], out int ph) ? ph : null;
        return PrintOk(ManageWindowJson(args[0], args[1], x, y, w, h));
    }

    private static int CmdWaitWindow(string[] args)
    {
        if (args.Length < 1) return Fail("Uso: wait <target> [--timeout 10000]");
        int timeout = int.TryParse(ParseOpt(args, "--timeout"), out var t) ? t : 10000;
        return PrintOk(WaitWindowJson(args[0], timeout));
    }

    internal static WindowInfo? ResolveWindow(string spec)
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

    private static T RunOnSta<T>(Func<T> action)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            return action();

        T? result = default;
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try { result = action(); }
            catch (Exception ex) { captured = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (captured != null) throw new InvalidOperationException(captured.Message, captured);
        return result!;
    }

    private static bool TryParseKey(string name, out ushort vk)
    {
        vk = name.ToUpperInvariant() switch
        {
            "CTRL" or "CONTROL" => 0x11,
            "ALT" or "MENU" => 0x12,
            "SHIFT" => 0x10,
            "WIN" or "LWIN" => 0x5B,
            "RWIN" => 0x5C,
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
            "PRTSC" or "PRINTSCREEN" => 0x2C,
            "CAPSLOCK" => 0x14,
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

    internal sealed record WindowInfo(IntPtr Hwnd, int Pid, string ProcessName, string Title, int X, int Y, int Width, int Height);
}
