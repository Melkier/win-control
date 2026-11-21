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
    private static readonly AutomationEvent? LiveRegionChangedEvent = TryLookupEvent(20024);

    private static readonly HashSet<string> InteractiveTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Button", "Hyperlink", "Edit", "ComboBox", "CheckBox", "RadioButton",
        "MenuItem", "TabItem", "ListItem", "TreeItem", "Slider", "SplitButton"
    };

    private static readonly HashSet<string> TextTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Text", "Document", "Header", "HeaderItem", "StatusBar", "ToolTip"
    };

    private static readonly object WatchLock = new();
    private static readonly ConcurrentQueue<UiEvent> EventQueue = new();
    private static CancellationTokenSource? _mcpWatchCts;
    private static Task? _mcpWatchTask;
    private static bool _mcpWatching;
    private static string _lastSignature = "";
    private const int MaxQueuedEvents = 300;

    private static AutomationEvent? TryLookupEvent(int id)
    {
        try { return AutomationEvent.LookupById(id); } catch { return null; }
    }

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
            var sw = Stopwatch.StartNew();
            var el = AutomationElement.FocusedElement;
            if (el == null)
            {
                return JsonSerializer.Serialize(new
                {
                    Elevated = IsElevated(),
                    UipiWarning = Native.GetUipiWarning(),
                    ElapsedMs = sw.ElapsedMilliseconds,
                    Element = (object?)null,
                    Note = "No hay elemento enfocado accesible en este instante."
                }, PrettyJson);
            }

            var procCache = new Dictionary<int, string>();
            var snapshot = ReadElement(el, procCache, includeWindow: true);
            return JsonSerializer.Serialize(new
            {
                Elevated = IsElevated(),
                UipiWarning = Native.GetUipiWarning(),
                Timestamp = DateTimeOffset.Now.ToString("O"),
                ElapsedMs = sw.ElapsedMilliseconds,
                Element = snapshot
            }, PrettyJson);
        });
    }

    public static string GetElementAtPointJson(int? x, int? y)
    {
        return RunOnMta(() =>
        {
            var sw = Stopwatch.StartNew();
            int px, py;
            if (x.HasValue && y.HasValue)
            {
                px = x.Value;
                py = y.Value;
            }
            else
            {
                Native.GetCursorPos(out var pt);
                px = pt.X;
                py = pt.Y;
            }

            var el = AutomationElement.FromPoint(new System.Windows.Point(px, py));
            var procCache = new Dictionary<int, string>();
            var snapshot = el != null ? ReadElement(el, procCache, includeWindow: true) : null;

            return JsonSerializer.Serialize(new
            {
                Elevated = IsElevated(),
                Point = new { X = px, Y = py },
                Timestamp = DateTimeOffset.Now.ToString("O"),
                ElapsedMs = sw.ElapsedMilliseconds,
                Element = snapshot
            }, PrettyJson);
        });
    }

    public static string GetAccessibleSummaryJson(string? target, int maxItems = 40, int maxTexts = 30, int timeoutMs = 3000)
    {
        maxItems = Math.Clamp(maxItems, 5, 200);
        maxTexts = Math.Clamp(maxTexts, 5, 200);
        timeoutMs = Math.Clamp(timeoutMs, 200, 15000);

        return RunOnMta(() =>
        {
            var sw = Stopwatch.StartNew();
            IntPtr hwnd = Program.ResolveTargetHwnd(target);
            if (hwnd == IntPtr.Zero)
                throw new InvalidOperationException("No se encontro ventana activa o destino.");

            var root = AutomationElement.FromHandle(hwnd)
                       ?? throw new InvalidOperationException($"UI Automation no pudo abrir HWND 0x{hwnd.ToInt64():X}.");

            var procCache = new Dictionary<int, string>();
            var interactive = new List<object>();
            var texts = new List<string>();
            var seenInteractive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenTexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool timedOut = false;

            CollectSummary(root, TreeWalker.ControlViewWalker, 0, 8, maxItems, maxTexts, sw, timeoutMs,
                ref timedOut, interactive, texts, seenInteractive, seenTexts);

            object? focusedSnapshot = null;
            try
            {
                var foc = AutomationElement.FocusedElement;
                if (foc != null) focusedSnapshot = ReadElement(foc, procCache, includeWindow: false);
            }
            catch { }

            sw.Stop();
            return JsonSerializer.Serialize(new
            {
                Elevated = IsElevated(),
                UipiWarning = Native.GetUipiWarning(hwnd),
                TargetHwnd = $"0x{hwnd.ToInt64():X}",
                WindowTitle = SafeStr(() => root.Current.Name),
                ProcessName = ResolveProcessName(SafeInt(() => root.Current.ProcessId), procCache),
                ElapsedMs = sw.ElapsedMilliseconds,
                TimedOut = timedOut,
                Focused = focusedSnapshot,
                InteractiveCount = interactive.Count,
                Interactive = interactive,
                TextCount = texts.Count,
                ReadableTexts = texts
            }, PrettyJson);
        });
    }

    private static void CollectSummary(
        AutomationElement el,
        TreeWalker walker,
        int depth,
        int maxDepth,
        int maxItems,
        int maxTexts,
        Stopwatch sw,
        int timeoutMs,
        ref bool timedOut,
        List<object> interactive,
        List<string> texts,
        HashSet<string> seenInteractive,
        HashSet<string> seenTexts)
    {
        if (depth > maxDepth) return;
        if (interactive.Count >= maxItems && texts.Count >= maxTexts) return;
        if (sw.ElapsedMilliseconds >= timeoutMs) { timedOut = true; return; }

        try
        {
            bool isOffscreen = el.Current.IsOffscreen;
            if (!isOffscreen)
            {
                string cType = CleanControlType(el.Current.ControlType.ProgrammaticName);
                bool isPassword = el.Current.IsPassword;
                string name = Truncate(el.Current.Name, 200).Trim();
                string autoId = el.Current.AutomationId ?? "";
                string? value = ReadValue(el, isPassword);

                if (interactive.Count < maxItems && InteractiveTypes.Contains(cType) && el.Current.IsEnabled)
                {
                    if (!string.IsNullOrWhiteSpace(name) || !string.IsNullOrWhiteSpace(autoId) || !string.IsNullOrWhiteSpace(value))
                    {
                        var bounds = ReadBounds(el);
                        string sig = $"{cType}|{name}|{autoId}|{bounds?.X},{bounds?.Y}";
                        if (seenInteractive.Add(sig))
                        {
                            interactive.Add(new
                            {
                                ControlType = cType,
                                Name = name,
                                AutomationId = autoId,
                                Value = value,
                                Bounds = bounds
                            });
                        }
                    }
                }
                else if (texts.Count < maxTexts && TextTypes.Contains(cType))
                {
                    string candidate = !string.IsNullOrWhiteSpace(name)
                        ? name
                        : (ReadTextSnippet(el, isPassword) ?? value ?? "");
                    if (!string.IsNullOrWhiteSpace(candidate) && seenTexts.Add(candidate))
                    {
                        texts.Add(candidate);
                    }
                }
            }
        }
        catch { }

        if (depth == maxDepth) return;

        AutomationElement? child = null;
        try { child = walker.GetFirstChild(el); } catch { return; }

        while (child != null && (interactive.Count < maxItems || texts.Count < maxTexts))
        {
            if (sw.ElapsedMilliseconds >= timeoutMs) { timedOut = true; return; }
            CollectSummary(child, walker, depth + 1, maxDepth, maxItems, maxTexts, sw, timeoutMs,
                ref timedOut, interactive, texts, seenInteractive, seenTexts);
            try { child = walker.GetNextSibling(child); } catch { break; }
        }
    }

    public static string GetUiTreeJson(
        string? target,
        int maxDepth,
        int maxNodes,
        string? filter = null,
        string? controlType = null,
        int timeoutMs = 4000)
    {
        maxDepth = Math.Clamp(maxDepth, 1, 15);
        maxNodes = Math.Clamp(maxNodes, 10, 1500);
        timeoutMs = Math.Clamp(timeoutMs, 200, 15000);

        return RunOnMta(() =>
        {
            var sw = Stopwatch.StartNew();
            IntPtr hwnd = Program.ResolveTargetHwnd(target);
            if (hwnd == IntPtr.Zero)
                throw new InvalidOperationException("No se encontro ventana activa o destino.");

            var root = AutomationElement.FromHandle(hwnd)
                       ?? throw new InvalidOperationException($"UI Automation no pudo abrir HWND 0x{hwnd.ToInt64():X}.");

            var procCache = new Dictionary<int, string>();
            var nodes = new List<object>();
            var walker = TreeWalker.ControlViewWalker;
            bool timedOut = false;

            Traverse(root, walker, 0, maxDepth, maxNodes, filter, controlType, sw, timeoutMs, ref timedOut, procCache, nodes);
            sw.Stop();

            return JsonSerializer.Serialize(new
            {
                Elevated = IsElevated(),
                UipiWarning = Native.GetUipiWarning(hwnd),
                TargetHwnd = $"0x{hwnd.ToInt64():X}",
                WindowTitle = SafeStr(() => root.Current.Name),
                ProcessName = ResolveProcessName(SafeInt(() => root.Current.ProcessId), procCache),
                ElapsedMs = sw.ElapsedMilliseconds,
                TimedOut = timedOut,
                NodeCount = nodes.Count,
                Truncated = nodes.Count >= maxNodes,
                Nodes = nodes
            }, PrettyJson);
        });
    }

    public static string PerformUiAction(
        string? target,
        string action,
        string? name,
        string? automationId,
        string? controlType,
        string? value,
        int index = 0,
        int timeoutMs = 4000)
    {
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(automationId) && string.IsNullOrWhiteSpace(controlType))
            throw new ArgumentException("Especifica al menos uno de: name, automation_id o control_type.");

        string act = action.Trim().ToLowerInvariant();
        if (act == "set_value" && value == null)
            throw new ArgumentException("La accion 'set_value' requiere el parametro 'value'.");

        timeoutMs = Math.Clamp(timeoutMs, 200, 15000);

        return RunOnMta(() =>
        {
            var sw = Stopwatch.StartNew();
            IntPtr hwnd = Program.ResolveTargetHwnd(target);
            if (hwnd == IntPtr.Zero)
                throw new InvalidOperationException("No se encontro ventana activa o destino.");

            var root = AutomationElement.FromHandle(hwnd)
                       ?? throw new InvalidOperationException($"UI Automation no pudo abrir HWND 0x{hwnd.ToInt64():X}.");

            var exactMatches = new List<AutomationElement>();
            var partialMatches = new List<AutomationElement>();
            bool timedOut = false;
            var walker = TreeWalker.ControlViewWalker;

            FindMatches(root, walker, 0, 12, name, automationId, controlType, sw, timeoutMs, ref timedOut, exactMatches, partialMatches);

            var candidates = exactMatches.Count > 0 ? exactMatches : partialMatches;
            if (candidates.Count == 0)
            {
                throw new InvalidOperationException(
                    $"No se encontro elemento (name='{name}', automation_id='{automationId}', control_type='{controlType}', timedOut={timedOut}).");
            }

            if (index < 0 || index >= candidates.Count)
                throw new ArgumentOutOfRangeException(nameof(index), $"Indice {index} fuera de rango (coincidencias: {candidates.Count}).");

            var el = candidates[index];
            string methodUsed;
            string? extra = null;

            switch (act)
            {
                case "invoke":
                    if (el.TryGetCurrentPattern(InvokePattern.Pattern, out var invObj) && invObj is InvokePattern inv)
                    {
                        inv.Invoke();
                        methodUsed = "InvokePattern";
                    }
                    else if (el.TryGetCurrentPattern(TogglePattern.Pattern, out var togObj) && togObj is TogglePattern tog)
                    {
                        tog.Toggle();
                        methodUsed = "TogglePattern";
                        extra = tog.Current.ToggleState.ToString();
                    }
                    else if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selObj) && selObj is SelectionItemPattern sel)
                    {
                        sel.Select();
                        methodUsed = "SelectionItemPattern";
                    }
                    else if (TryClickBoundsCenter(el))
                    {
                        methodUsed = "PhysicalClickFallback";
                    }
                    else
                    {
                        throw new InvalidOperationException("El elemento no soporta Invoke/Toggle/Select ni tiene coordenadas visibles.");
                    }
                    break;

                case "click":
                    if (!TryClickBoundsCenter(el))
                        throw new InvalidOperationException("El elemento no tiene coordenadas visibles en pantalla para hacer clic.");
                    methodUsed = "PhysicalClickCenter";
                    break;

                case "set_value":
                    if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var valObj) && valObj is ValuePattern vp && !vp.Current.IsReadOnly)
                    {
                        vp.SetValue(value!);
                        methodUsed = "ValuePattern";
                    }
                    else
                    {
                        el.SetFocus();
                        Thread.Sleep(50);
                        Native.SendUnicodeText(value!);
                        methodUsed = "FocusAndSendInputFallback";
                    }
                    break;

                case "toggle":
                    if (el.TryGetCurrentPattern(TogglePattern.Pattern, out var tObj) && tObj is TogglePattern tp)
                    {
                        tp.Toggle();
                        methodUsed = "TogglePattern";
                        extra = tp.Current.ToggleState.ToString();
                    }
                    else
                    {
                        throw new InvalidOperationException("El elemento no soporta TogglePattern.");
                    }
                    break;

                case "select":
                    if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sObj) && sObj is SelectionItemPattern sp)
                    {
                        sp.Select();
                        methodUsed = "SelectionItemPattern";
                    }
                    else
                    {
                        throw new InvalidOperationException("El elemento no soporta SelectionItemPattern.");
                    }
                    break;

                case "expand":
                    if (el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var eObj) && eObj is ExpandCollapsePattern ep)
                    {
                        ep.Expand();
                        methodUsed = "ExpandCollapsePattern.Expand";
                    }
                    else
                    {
                        throw new InvalidOperationException("El elemento no soporta ExpandCollapsePattern.");
                    }
                    break;

                case "collapse":
                    if (el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var cObj) && cObj is ExpandCollapsePattern cp)
                    {
                        cp.Collapse();
                        methodUsed = "ExpandCollapsePattern.Collapse";
                    }
                    else
                    {
                        throw new InvalidOperationException("El elemento no soporta ExpandCollapsePattern.");
                    }
                    break;

                case "focus":
                    el.SetFocus();
                    methodUsed = "AutomationElement.SetFocus";
                    break;

                default:
                    throw new ArgumentException($"Accion UIA invalida: '{action}'. Usa: invoke, click, set_value, toggle, select, expand, collapse o focus.");
            }

            sw.Stop();
            var procCache = new Dictionary<int, string>();
            return JsonSerializer.Serialize(new
            {
                Success = true,
                Action = act,
                MethodUsed = methodUsed,
                State = extra,
                MatchCount = candidates.Count,
                SelectedIndex = index,
                ElapsedMs = sw.ElapsedMilliseconds,
                UipiWarning = Native.GetUipiWarning(hwnd),
                Element = ReadElement(el, procCache, includeWindow: false)
            }, PrettyJson);
        });
    }

    public static int RunWatchCli(CancellationToken ct)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Event = "watch_started",
            Elevated = IsElevated(),
            LiveRegionsSupported = LiveRegionChangedEvent != null,
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
                string sig = $"{ev.Event}|{ev.ProcessId}|{ev.WindowTitle}|{ev.ControlType}|{ev.Name}|{ev.Value}";
                lock (outLock)
                {
                    if (sig == lastSig) return;
                    lastSig = sig;
                    Console.WriteLine(JsonSerializer.Serialize(ev, CompactJson));
                    Console.Out.Flush();
                }
            }

            RunWatchLoop(ct, Emit);
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

                        _mcpWatchTask = Task.Run(() => RunWatchLoop(token, EnqueueEvent), token);
                        _mcpWatching = true;
                    }
                }
                return JsonSerializer.Serialize(new
                {
                    Watching = true,
                    Elevated = IsElevated(),
                    LiveRegionsSupported = LiveRegionChangedEvent != null
                }, PrettyJson);

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
                CancellationTokenSource? ctsToStop;
                Task? taskToWait;
                lock (WatchLock)
                {
                    ctsToStop = _mcpWatchCts;
                    taskToWait = _mcpWatchTask;
                    _mcpWatchCts = null;
                    _mcpWatchTask = null;
                    _mcpWatching = false;
                }

                if (ctsToStop != null)
                {
                    try { ctsToStop.Cancel(); } catch { }
                    if (taskToWait != null)
                    {
                        try { taskToWait.Wait(1500); } catch { }
                    }
                    try { ctsToStop.Dispose(); } catch { }
                }
                return JsonSerializer.Serialize(new { Watching = false }, PrettyJson);

            default:
                throw new ArgumentException($"Accion invalida: '{action}'. Usa start, poll o stop.");
        }
    }

    internal static void RunWatchLoop(CancellationToken ct, Action<UiEvent> emit)
    {
        AutomationFocusChangedEventHandler focusHandler = (sender, _) =>
        {
            if (sender is AutomationElement el)
            {
                var ev = BuildEvent("focus", el);
                if (ev != null) emit(ev);
            }
        };

        AutomationEventHandler windowOpenedHandler = (sender, _) =>
        {
            if (sender is AutomationElement el)
            {
                var ev = BuildEvent("window_opened", el);
                if (ev != null) emit(ev);
            }
        };

        AutomationEventHandler liveRegionHandler = (sender, _) =>
        {
            if (sender is AutomationElement el)
            {
                var ev = BuildEvent("live_region", el);
                if (ev != null) emit(ev);
            }
        };

        Automation.AddAutomationFocusChangedEventHandler(focusHandler);
        bool windowOpenedAttached = false;
        try
        {
            Automation.AddAutomationEventHandler(
                WindowPattern.WindowOpenedEvent,
                AutomationElement.RootElement,
                TreeScope.Subtree,
                windowOpenedHandler);
            windowOpenedAttached = true;
        }
        catch { }

        AutomationElement? liveRegionWindow = null;

        void DetachLiveRegion()
        {
            if (liveRegionWindow != null && LiveRegionChangedEvent != null)
            {
                try { Automation.RemoveAutomationEventHandler(LiveRegionChangedEvent, liveRegionWindow, liveRegionHandler); } catch { }
                liveRegionWindow = null;
            }
        }

        try
        {
            IntPtr lastHwnd = IntPtr.Zero;
            while (!ct.IsCancellationRequested)
            {
                IntPtr fg = Native.GetForegroundWindow();
                if (fg != IntPtr.Zero && fg != lastHwnd)
                {
                    lastHwnd = fg;
                    DetachLiveRegion();
                    try
                    {
                        var el = AutomationElement.FromHandle(fg);
                        if (el != null)
                        {
                            var ev = BuildEvent("window", el);
                            if (ev != null) emit(ev);

                            if (LiveRegionChangedEvent != null)
                            {
                                try
                                {
                                    Automation.AddAutomationEventHandler(LiveRegionChangedEvent, el, TreeScope.Subtree, liveRegionHandler);
                                    liveRegionWindow = el;
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }
                }

                try { ct.WaitHandle.WaitOne(200); }
                catch (ObjectDisposedException) { break; }
            }
        }
        finally
        {
            DetachLiveRegion();
            if (windowOpenedAttached)
                try { Automation.RemoveAutomationEventHandler(WindowPattern.WindowOpenedEvent, AutomationElement.RootElement, windowOpenedHandler); } catch { }
            try { Automation.RemoveAutomationFocusChangedEventHandler(focusHandler); } catch { }
        }
    }

    private static void EnqueueEvent(UiEvent ev)
    {
        string sig = $"{ev.Event}|{ev.ProcessId}|{ev.WindowTitle}|{ev.ControlType}|{ev.Name}|{ev.Value}";
        lock (WatchLock)
        {
            if (sig == _lastSignature) return;
            _lastSignature = sig;
        }
        EventQueue.Enqueue(ev);
        while (EventQueue.Count > MaxQueuedEvents)
            EventQueue.TryDequeue(out _);
    }

    private static bool TryClickBoundsCenter(AutomationElement el)
    {
        var bounds = ReadBounds(el);
        if (bounds == null || bounds.Width <= 0 || bounds.Height <= 0) return false;
        int cx = bounds.X + bounds.Width / 2;
        int cy = bounds.Y + bounds.Height / 2;
        Program.ClickAt(cx, cy, "left");
        return true;
    }

    private static void FindMatches(
        AutomationElement el,
        TreeWalker walker,
        int depth,
        int maxDepth,
        string? name,
        string? automationId,
        string? controlType,
        Stopwatch sw,
        int timeoutMs,
        ref bool timedOut,
        List<AutomationElement> exactMatches,
        List<AutomationElement> partialMatches)
    {
        if (depth > maxDepth) return;
        if (sw.ElapsedMilliseconds >= timeoutMs) { timedOut = true; return; }

        try
        {
            string elName = el.Current.Name ?? "";
            string elId = el.Current.AutomationId ?? "";
            string elType = CleanControlType(el.Current.ControlType.ProgrammaticName);

            bool typeOk = string.IsNullOrWhiteSpace(controlType)
                          || elType.Equals(controlType, StringComparison.OrdinalIgnoreCase);
            bool idOk = string.IsNullOrWhiteSpace(automationId)
                        || elId.Equals(automationId, StringComparison.OrdinalIgnoreCase);

            if (typeOk && idOk)
            {
                if (string.IsNullOrWhiteSpace(name))
                    exactMatches.Add(el);
                else if (elName.Equals(name, StringComparison.OrdinalIgnoreCase))
                    exactMatches.Add(el);
                else if (elName.Contains(name, StringComparison.OrdinalIgnoreCase))
                    partialMatches.Add(el);
            }
        }
        catch { }

        if (depth == maxDepth) return;

        AutomationElement? child = null;
        try { child = walker.GetFirstChild(el); } catch { return; }

        while (child != null)
        {
            if (sw.ElapsedMilliseconds >= timeoutMs) { timedOut = true; return; }
            FindMatches(child, walker, depth + 1, maxDepth, name, automationId, controlType, sw, timeoutMs, ref timedOut, exactMatches, partialMatches);
            try { child = walker.GetNextSibling(child); } catch { break; }
        }
    }

    private static void Traverse(
        AutomationElement el,
        TreeWalker walker,
        int depth,
        int maxDepth,
        int maxNodes,
        string? filter,
        string? controlType,
        Stopwatch sw,
        int timeoutMs,
        ref bool timedOut,
        Dictionary<int, string> procCache,
        List<object> output)
    {
        if (output.Count >= maxNodes || depth > maxDepth) return;
        if (sw.ElapsedMilliseconds >= timeoutMs) { timedOut = true; return; }

        var info = ReadElement(el, procCache, includeWindow: false, depth: depth, filter: filter, controlType: controlType);
        if (info != null)
            output.Add(info);

        if (depth == maxDepth || output.Count >= maxNodes) return;

        AutomationElement? child = null;
        try { child = walker.GetFirstChild(el); } catch { return; }

        while (child != null && output.Count < maxNodes)
        {
            if (sw.ElapsedMilliseconds >= timeoutMs) { timedOut = true; return; }
            Traverse(child, walker, depth + 1, maxDepth, maxNodes, filter, controlType, sw, timeoutMs, ref timedOut, procCache, output);
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
        int depth = 0,
        string? filter = null,
        string? controlType = null)
    {
        try
        {
            bool isOffscreen = el.Current.IsOffscreen;
            bool isPassword = el.Current.IsPassword;
            string name = Truncate(el.Current.Name, 300);
            string cType = CleanControlType(el.Current.ControlType.ProgrammaticName);
            string autoId = el.Current.AutomationId ?? "";
            string helpText = Truncate(el.Current.HelpText, 200);
            string? value = ReadValue(el, isPassword);
            string? text = ReadTextSnippet(el, isPassword);
            string? toggle = ReadToggleState(el);
            bool? selected = ReadSelectionState(el);
            var bounds = ReadBounds(el);
            int pid = el.Current.ProcessId;

            if (!string.IsNullOrWhiteSpace(controlType)
                && !cType.Equals(controlType, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(filter))
            {
                bool matches = name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                               || autoId.Contains(filter, StringComparison.OrdinalIgnoreCase)
                               || helpText.Contains(filter, StringComparison.OrdinalIgnoreCase)
                               || (value != null && value.Contains(filter, StringComparison.OrdinalIgnoreCase))
                               || (text != null && text.Contains(filter, StringComparison.OrdinalIgnoreCase));
                if (!matches) return null;
            }

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
                    ControlType = cType,
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
                ControlType = cType,
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
        try
        {
            using var proc = Process.GetProcessById(pid);
            name = proc.ProcessName;
        }
        catch { }
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
