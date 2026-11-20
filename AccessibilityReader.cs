using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Windows.Automation;

namespace WinControl;

internal static class AccessibilityReader
{
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions CompactJson = new() { WriteIndented = false };

    private static readonly object WatchLock = new();
    private static readonly ConcurrentQueue<UiEvent> EventQueue = new();
    private static AutomationFocusChangedEventHandler? _mcpFocusHandler;
    private static CancellationTokenSource? _mcpWatchCts;
    private static bool _mcpWatching;
    private static string _lastSignature = "";
    private const int MaxQueuedEvents = 200;

    public static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static string GetFocusedJson()
    {
        return RunOnMta(() =>
        {
            var el = AutomationElement.FocusedElement;
            if (el == null)
            {
                return JsonSerializer.Serialize(new
                {
                    Elevated = IsElevated(),
                    Focused = (object?)null,
                    Note = "No hay elemento enfocado accesible en este instante."
                }, PrettyJson);
            }

            var procCache = new Dictionary<int, string>();
            var snapshot = ReadElement(el, procCache, includeWindow: true);
            return JsonSerializer.Serialize(new
            {
                Elevated = IsElevated(),
                Timestamp = DateTimeOffset.Now.ToString("O"),
                Element = snapshot
            }, PrettyJson);
        });
    }

    public static string GetUiTreeJson(string? target, int maxDepth, int maxNodes)
    {
        maxDepth = Math.Clamp(maxDepth, 1, 15);
        maxNodes = Math.Clamp(maxNodes, 10, 1500);

        return RunOnMta(() =>
        {
            IntPtr hwnd = Program.ResolveTargetHwnd(target);
            if (hwnd == IntPtr.Zero)
                throw new InvalidOperationException("No se encontro ventana activa o destino.");

            var root = AutomationElement.FromHandle(hwnd)
                       ?? throw new InvalidOperationException($"UI Automation no pudo abrir HWND 0x{hwnd.ToInt64():X}.");

            var procCache = new Dictionary<int, string>();
            var nodes = new List<object>();
            var walker = TreeWalker.ControlViewWalker;

            Traverse(root, walker, 0, maxDepth, maxNodes, procCache, nodes);

            return JsonSerializer.Serialize(new
            {
                Elevated = IsElevated(),
                TargetHwnd = $"0x{hwnd.ToInt64():X}",
                WindowTitle = SafeStr(() => root.Current.Name),
                ProcessName = ResolveProcessName(SafeInt(() => root.Current.ProcessId), procCache),
                NodeCount = nodes.Count,
                Truncated = nodes.Count >= maxNodes,
                Nodes = nodes
            }, PrettyJson);
        });
    }

    public static int RunWatchCli(CancellationToken ct)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Event = "watch_started",
            Elevated = IsElevated(),
            Timestamp = DateTimeOffset.Now.ToString("O"),
            Note = "Presiona Ctrl+C para detener la lectura en tiempo real."
        }, CompactJson));
        Console.Out.Flush();

        return RunOnMta(() =>
        {
            string lastSig = "";
            object outLock = new();

            void Emit(UiEvent ev)
            {
                string sig = $"{ev.ProcessId}|{ev.WindowTitle}|{ev.ControlType}|{ev.Name}|{ev.Value}";
                lock (outLock)
                {
                    if (sig == lastSig) return;
                    lastSig = sig;
                    Console.WriteLine(JsonSerializer.Serialize(ev, CompactJson));
                    Console.Out.Flush();
                }
            }

            AutomationFocusChangedEventHandler handler = (sender, _) =>
            {
                if (sender is AutomationElement el)
                {
                    var ev = BuildEvent("focus", el);
                    if (ev != null) Emit(ev);
                }
            };

            Automation.AddAutomationFocusChangedEventHandler(handler);
            try
            {
                IntPtr lastHwnd = IntPtr.Zero;
                while (!ct.IsCancellationRequested)
                {
                    IntPtr fg = Native.GetForegroundWindow();
                    if (fg != IntPtr.Zero && fg != lastHwnd)
                    {
                        lastHwnd = fg;
                        try
                        {
                            var el = AutomationElement.FromHandle(fg);
                            var ev = el != null ? BuildEvent("window", el) : null;
                            if (ev != null) Emit(ev);
                        }
                        catch { }
                    }
                    ct.WaitHandle.WaitOne(200);
                }
            }
            finally
            {
                try { Automation.RemoveAutomationFocusChangedEventHandler(handler); } catch { }
            }
            return 0;
        });
    }

    public static string ControlWatchMcp(string action)
    {
        switch (action.ToLowerInvariant())
        {
            case "start":
                lock (WatchLock)
                {
                    if (!_mcpWatching)
                    {
                        while (EventQueue.TryDequeue(out _)) { }
                        _lastSignature = "";
                        _mcpWatchCts = new CancellationTokenSource();
                        var token = _mcpWatchCts.Token;

                        _mcpFocusHandler = (sender, _) =>
                        {
                            if (sender is AutomationElement el)
                            {
                                var ev = BuildEvent("focus", el);
                                if (ev != null) EnqueueEvent(ev);
                            }
                        };

                        Task.Run(() =>
                        {
                            Automation.AddAutomationFocusChangedEventHandler(_mcpFocusHandler);
                            IntPtr lastHwnd = IntPtr.Zero;
                            try
                            {
                                while (!token.IsCancellationRequested)
                                {
                                    IntPtr fg = Native.GetForegroundWindow();
                                    if (fg != IntPtr.Zero && fg != lastHwnd)
                                    {
                                        lastHwnd = fg;
                                        try
                                        {
                                            var el = AutomationElement.FromHandle(fg);
                                            var ev = el != null ? BuildEvent("window", el) : null;
                                            if (ev != null) EnqueueEvent(ev);
                                        }
                                        catch { }
                                    }
                                    token.WaitHandle.WaitOne(250);
                                }
                            }
                            finally
                            {
                                if (_mcpFocusHandler != null)
                                    try { Automation.RemoveAutomationFocusChangedEventHandler(_mcpFocusHandler); } catch { }
                            }
                        }, token);

                        _mcpWatching = true;
                    }
                }
                return JsonSerializer.Serialize(new { Watching = true, Elevated = IsElevated() }, PrettyJson);

            case "poll":
                var batch = new List<UiEvent>();
                while (EventQueue.TryDequeue(out var item))
                    batch.Add(item);
                return JsonSerializer.Serialize(new
                {
                    Watching = _mcpWatching,
                    Elevated = IsElevated(),
                    Count = batch.Count,
                    Events = batch
                }, PrettyJson);

            case "stop":
                lock (WatchLock)
                {
                    _mcpWatchCts?.Cancel();
                    _mcpWatchCts?.Dispose();
                    _mcpWatchCts = null;
                    _mcpWatching = false;
                }
                return JsonSerializer.Serialize(new { Watching = false }, PrettyJson);

            default:
                throw new ArgumentException($"Accion invalida: '{action}'. Usa start, poll o stop.");
        }
    }

    private static void EnqueueEvent(UiEvent ev)
    {
        string sig = $"{ev.ProcessId}|{ev.WindowTitle}|{ev.ControlType}|{ev.Name}|{ev.Value}";
        lock (WatchLock)
        {
            if (sig == _lastSignature) return;
            _lastSignature = sig;
        }
        EventQueue.Enqueue(ev);
        while (EventQueue.Count > MaxQueuedEvents)
            EventQueue.TryDequeue(out _);
    }

    private static void Traverse(
        AutomationElement el,
        TreeWalker walker,
        int depth,
        int maxDepth,
        int maxNodes,
        Dictionary<int, string> procCache,
        List<object> output)
    {
        if (output.Count >= maxNodes || depth > maxDepth) return;

        var info = ReadElement(el, procCache, includeWindow: false, depth: depth);
        if (info != null)
            output.Add(info);

        if (depth == maxDepth || output.Count >= maxNodes) return;

        AutomationElement? child = null;
        try { child = walker.GetFirstChild(el); } catch { return; }

        while (child != null && output.Count < maxNodes)
        {
            Traverse(child, walker, depth + 1, maxDepth, maxNodes, procCache, output);
            try { child = walker.GetNextSibling(child); } catch { break; }
        }
    }

    private static UiEvent? BuildEvent(string kind, AutomationElement el)
    {
        try
        {
            int pid = el.Current.ProcessId;
            bool isPassword = el.Current.IsPassword;
            string name = Truncate(el.Current.Name, 300);
            string controlType = CleanControlType(el.Current.ControlType.ProgrammaticName);
            string? value = ReadValue(el, isPassword);
            string? text = ReadTextSnippet(el, isPassword);
            var bounds = ReadBounds(el);
            IntPtr fg = Native.GetForegroundWindow();
            string winTitle = fg != IntPtr.Zero ? Native.GetWindowTextString(fg) : "";

            return new UiEvent(
                DateTimeOffset.Now.ToString("O"),
                kind,
                pid,
                ResolveProcessName(pid, null),
                winTitle,
                controlType,
                name,
                value,
                text,
                isPassword,
                bounds
            );
        }
        catch (ElementNotAvailableException) { return null; }
        catch (COMException) { return null; }
        catch { return null; }
    }

    private static object? ReadElement(
        AutomationElement el,
        Dictionary<int, string> procCache,
        bool includeWindow,
        int depth = 0)
    {
        try
        {
            bool isOffscreen = el.Current.IsOffscreen;
            bool isPassword = el.Current.IsPassword;
            string name = Truncate(el.Current.Name, 300);
            string controlType = CleanControlType(el.Current.ControlType.ProgrammaticName);
            string autoId = el.Current.AutomationId ?? "";
            string helpText = Truncate(el.Current.HelpText, 200);
            string? value = ReadValue(el, isPassword);
            string? text = ReadTextSnippet(el, isPassword);
            string? toggle = ReadToggleState(el);
            bool? selected = ReadSelectionState(el);
            var bounds = ReadBounds(el);
            int pid = el.Current.ProcessId;

            if (depth > 0 && string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(value)
                && string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(autoId))
            {
                return null;
            }

            if (includeWindow)
            {
                IntPtr fg = Native.GetForegroundWindow();
                return new
                {
                    ProcessId = pid,
                    ProcessName = ResolveProcessName(pid, procCache),
                    WindowTitle = fg != IntPtr.Zero ? Native.GetWindowTextString(fg) : "",
                    ControlType = controlType,
                    Name = name,
                    AutomationId = autoId,
                    Value = value,
                    Text = text,
                    HelpText = helpText,
                    IsEnabled = el.Current.IsEnabled,
                    HasKeyboardFocus = el.Current.HasKeyboardFocus,
                    IsOffscreen = isOffscreen,
                    IsPassword = isPassword,
                    ToggleState = toggle,
                    IsSelected = selected,
                    Bounds = bounds
                };
            }

            return new
            {
                Depth = depth,
                ControlType = controlType,
                Name = name,
                AutomationId = autoId,
                Value = value,
                Text = text,
                HelpText = helpText,
                IsEnabled = el.Current.IsEnabled,
                HasKeyboardFocus = el.Current.HasKeyboardFocus,
                IsOffscreen = isOffscreen,
                IsPassword = isPassword,
                ToggleState = toggle,
                IsSelected = selected,
                Bounds = bounds
            };
        }
        catch (ElementNotAvailableException) { return null; }
        catch (COMException) { return null; }
        catch { return null; }
    }

    private static string? ReadValue(AutomationElement el, bool isPassword)
    {
        if (isPassword) return null;
        try
        {
            if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var obj) && obj is ValuePattern vp)
            {
                var v = vp.Current.Value;
                return string.IsNullOrEmpty(v) ? null : Truncate(v, 500);
            }
        }
        catch { }
        return null;
    }

    private static string? ReadTextSnippet(AutomationElement el, bool isPassword)
    {
        if (isPassword) return null;
        try
        {
            if (el.TryGetCurrentPattern(TextPattern.Pattern, out var obj) && obj is TextPattern tp)
            {
                var txt = tp.DocumentRange?.GetText(500);
                return string.IsNullOrWhiteSpace(txt) ? null : Truncate(txt.Trim(), 500);
            }
        }
        catch { }
        return null;
    }

    private static string? ReadToggleState(AutomationElement el)
    {
        try
        {
            if (el.TryGetCurrentPattern(TogglePattern.Pattern, out var obj) && obj is TogglePattern tp)
                return tp.Current.ToggleState.ToString();
        }
        catch { }
        return null;
    }

    private static bool? ReadSelectionState(AutomationElement el)
    {
        try
        {
            if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var obj) && obj is SelectionItemPattern sp)
                return sp.Current.IsSelected;
        }
        catch { }
        return null;
    }

    private static BoundsDto? ReadBounds(AutomationElement el)
    {
        try
        {
            var r = el.Current.BoundingRectangle;
            if (r.IsEmpty || !double.IsFinite(r.X) || !double.IsFinite(r.Y)
                || !double.IsFinite(r.Width) || !double.IsFinite(r.Height))
                return null;
            return new BoundsDto((int)Math.Round(r.X), (int)Math.Round(r.Y),
                (int)Math.Round(r.Width), (int)Math.Round(r.Height));
        }
        catch { return null; }
    }

    private static string ResolveProcessName(int pid, Dictionary<int, string>? cache)
    {
        if (pid <= 0) return "";
        if (cache != null && cache.TryGetValue(pid, out var cached)) return cached;
        string name = "";
        try { name = Process.GetProcessById(pid).ProcessName; } catch { }
        if (cache != null) cache[pid] = name;
        return name;
    }

    private static string CleanControlType(string raw) =>
        raw.StartsWith("ControlType.", StringComparison.Ordinal) ? raw["ControlType.".Length..] : raw;

    private static string Truncate(string? value, int maxLen)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Length <= maxLen ? value : value[..maxLen] + "...";
    }

    private static string SafeStr(Func<string> fn) { try { return fn() ?? ""; } catch { return ""; } }
    private static int SafeInt(Func<int> fn) { try { return fn(); } catch { return 0; } }

    private static T RunOnMta<T>(Func<T> action)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA)
            return action();
        return Task.Run(action).GetAwaiter().GetResult();
    }

    internal sealed record BoundsDto(int X, int Y, int Width, int Height);

    internal sealed record UiEvent(
        string Timestamp,
        string Event,
        int ProcessId,
        string ProcessName,
        string WindowTitle,
        string ControlType,
        string Name,
        string? Value,
        string? Text,
        bool IsPassword,
        BoundsDto? Bounds
    );
}
