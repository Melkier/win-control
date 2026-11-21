using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinControl;

internal static class McpServer
{
    private static readonly JsonSerializerOptions CompactJson = new() { WriteIndented = false };

    public static int Run()
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = new UTF8Encoding(false);

        string? line;
        while ((line = Console.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var msg = JsonNode.Parse(line)?.AsObject();
                if (msg == null) continue;
                HandleMessage(msg);
            }
            catch (Exception ex)
            {
                SendError(null, -32700, $"Parse error: {ex.Message}");
            }
        }
        return 0;
    }

    private static void HandleMessage(JsonObject msg)
    {
        var id = msg["id"]?.DeepClone();
        var method = msg["method"]?.GetValue<string>() ?? "";
        var @params = msg["params"] as JsonObject;

        if (id == null) return;

        try
        {
            switch (method)
            {
                case "initialize":
                {
                    var protocolVersion = @params?["protocolVersion"]?.GetValue<string>() ?? "2024-11-05";
                    var result = new JsonObject
                    {
                        ["protocolVersion"] = protocolVersion,
                        ["capabilities"] = new JsonObject
                        {
                            ["tools"] = new JsonObject { ["listChanged"] = false }
                        },
                        ["serverInfo"] = new JsonObject
                        {
                            ["name"] = "win-control",
                            ["version"] = "2.1.0"
                        }
                    };
                    SendResult(id, result);
                    break;
                }
                case "ping":
                    SendResult(id, new JsonObject());
                    break;
                case "tools/list":
                    SendResult(id, new JsonObject { ["tools"] = BuildToolsList() });
                    break;
                case "tools/call":
                {
                    var name = @params?["name"]?.GetValue<string>() ?? "";
                    var args = @params?["arguments"] as JsonObject ?? new JsonObject();
                    var result = ExecuteTool(name, args);
                    SendResult(id, result);
                    break;
                }
                default:
                    SendError(id, -32601, $"Method not found: {method}");
                    break;
            }
        }
        catch (Exception ex)
        {
            SendError(id, -32603, ex.Message);
        }
    }

    private static JsonArray BuildToolsList()
    {
        return new JsonArray
        {
            Tool("desktop_screen_summary", "Resumen de accesibilidad de baja sobrecarga cognitiva: devuelve foco actual, controles interactivos visibles y textos legibles sin ruido estructural.", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["target"] = Prop("string", "Opcional: HWND o subcadena de titulo/proceso. Si se omite, resume la ventana activa."),
                    ["max_items"] = Prop("integer", "Opcional: maximo de controles interactivos (defecto 40)."),
                    ["max_texts"] = Prop("integer", "Opcional: maximo de textos legibles (defecto 30)."),
                    ["timeout_ms"] = Prop("integer", "Opcional: limite de tiempo en ms (defecto 3000).")
                }
            }),
            Tool("desktop_uia_tree", "Lee el arbol semantico de accesibilidad (UI Automation) de la ventana activa o indicada con filtros opcionales y timeout seguro.", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["target"] = Prop("string", "Opcional: HWND o subcadena de titulo/proceso. Si se omite, lee la ventana activa."),
                    ["max_depth"] = Prop("integer", "Opcional: profundidad maxima (defecto 6, max 15)."),
                    ["max_nodes"] = Prop("integer", "Opcional: maximo de nodos (defecto 250, max 1500)."),
                    ["filter"] = Prop("string", "Opcional: filtra nodos cuyo Name, Value, Text o AutomationId contenga este texto."),
                    ["control_type"] = Prop("string", "Opcional: filtra por ControlType (ej: Button, Edit, Hyperlink, Text, ListItem)."),
                    ["timeout_ms"] = Prop("integer", "Opcional: limite de tiempo en ms (defecto 4000, max 15000).")
                }
            }),
            Tool("desktop_uia_act", "Ejecuta una accion semantica directa sobre un control por Name, AutomationId o ControlType sin calcular coordenadas manualmente.", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["action"] = Prop("string", "Accion: invoke, click, set_value, toggle, select, expand, collapse o focus."),
                    ["target"] = Prop("string", "Opcional: HWND o subcadena de titulo/proceso. Si se omite, usa la ventana activa."),
                    ["name"] = Prop("string", "Opcional: nombre exacto o subcadena del elemento."),
                    ["automation_id"] = Prop("string", "Opcional: AutomationId exacto del elemento."),
                    ["control_type"] = Prop("string", "Opcional: ControlType (ej: Button, Edit, CheckBox)."),
                    ["value"] = Prop("string", "Requerido para set_value: texto a asignar."),
                    ["index"] = Prop("integer", "Opcional: indice si hay multiples coincidencias (defecto 0)."),
                    ["timeout_ms"] = Prop("integer", "Opcional: tiempo limite de busqueda en ms (defecto 4000).")
                },
                ["required"] = new JsonArray("action")
            }),
            Tool("desktop_focused", "Devuelve en tiempo real el elemento enfocado, su texto/valor, ventana y estado de elevacion/UIPI.", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject()
            }),
            Tool("desktop_element_at", "Lee el elemento accesible en las coordenadas (x, y) o bajo la posicion actual del cursor.", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["x"] = Prop("integer", "Opcional: coordenada X fisica (si se omite, usa el cursor actual)."),
                    ["y"] = Prop("integer", "Opcional: coordenada Y fisica (si se omite, usa el cursor actual).")
                }
            }),
            Tool("desktop_watch_events", "Inicia, consulta o detiene un monitor en tiempo real de foco, cambios de ventana, nuevas ventanas y regiones dinamicas (LiveRegion).", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["action"] = Prop("string", "Accion: start, poll o stop.")
                },
                ["required"] = new JsonArray("action")
            }),
            Tool("desktop_ocr", "Ejecuta OCR nativo offline de Windows sobre una ventana, monitor o region para leer texto y coordenadas en apps graficas sin UIA.", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["target"] = Prop("string", "Opcional: HWND o subcadena de titulo/proceso."),
                    ["monitor"] = Prop("integer", "Opcional: indice de monitor (0, 1, ...)."),
                    ["region"] = Prop("string", "Opcional: recorte fisico 'x,y,width,height'."),
                    ["lang"] = Prop("string", "Opcional: codigo de idioma instalado (ej: es-ES, en-US).")
                }
            }),
            Tool("desktop_screenshot", "Captura pantalla, monitor, ventana (con PrintWindow anti-oclusion y respaldo anti-frame-negro) o region en PNG o JPEG.", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["target"] = Prop("string", "Opcional: HWND o subcadena de titulo/proceso."),
                    ["monitor"] = Prop("integer", "Opcional: indice de monitor (0, 1, ...)."),
                    ["region"] = Prop("string", "Opcional: recorte fisico 'x,y,width,height'."),
                    ["max_width"] = Prop("integer", "Opcional: ancho maximo en pixeles (defecto 1280)."),
                    ["format"] = Prop("string", "Opcional: png (defecto) o jpeg (mas ligero para tiempo real)."),
                    ["quality"] = Prop("integer", "Opcional: calidad JPEG 10-100 (defecto 80)."),
                    ["capture_mode"] = Prop("string", "Opcional: auto (defecto, usa PrintWindow con deteccion de frame negro) o screen.")
                }
            }),
            Tool("desktop_sequence", "Ejecuta una cadena de hasta 50 pasos en una sola llamada con freno de seguridad instantaneo al presionar Esc.", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["steps"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["description"] = "Arreglo de pasos. Cada paso lleva 'op': wait, click, hover, mouse_relative, drag, scroll, key, type, focus o uia_act."
                    },
                    ["abort_on_esc"] = Prop("boolean", "Opcional: detiene inmediatamente si el usuario pulsa Esc (defecto true).")
                },
                ["required"] = new JsonArray("steps")
            }),
            Tool("desktop_screens", "Lista monitores conectados y sus coordenadas/resoluciones fisicas.", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject()
            }),
            Tool("desktop_windows", "Lista ventanas visibles del escritorio con HWND, PID, proceso, titulo y coordenadas.", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["filter"] = Prop("string", "Filtro opcional por titulo o nombre de proceso.")
                }
            }),
            Tool("desktop_controls", "Lista controles hijos Win32 de una ventana para leer botones y textos nativos.", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["target"] = Prop("string", "HWND (ej: 0x1234) o subcadena del titulo/proceso.")
                },
                ["required"] = new JsonArray("target")
            }),
            Tool("desktop_launch", "Abre una aplicacion, acceso directo (.lnk), carpeta, archivo o URL en el escritorio.", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["target"] = Prop("string", "Ruta del ejecutable, acceso directo, carpeta, URL o comando."),
                    ["args"] = Prop("string", "Argumentos opcionales.")
                },
                ["required"] = new JsonArray("target")
            }),
            Tool("desktop_focus", "Restaura y trae al frente una ventana del escritorio.", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["target"] = Prop("string", "HWND (ej: 0x1234) o subcadena del titulo/proceso.")
                },
                ["required"] = new JsonArray("target")
            }),
            Tool("desktop_window_manage", "Minimiza, maximiza, restaura, cierra o mueve/redimensiona una ventana.", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["target"] = Prop("string", "HWND o subcadena del titulo/proceso."),
                    ["action"] = Prop("string", "Accion: minimize, maximize, restore, close o move_resize."),
                    ["x"] = Prop("integer", "Requerido para move_resize: nueva X."),
                    ["y"] = Prop("integer", "Requerido para move_resize: nueva Y."),
                    ["width"] = Prop("integer", "Requerido para move_resize: nuevo ancho."),
                    ["height"] = Prop("integer", "Requerido para move_resize: nuevo alto.")
                },
                ["required"] = new JsonArray("target", "action")
            }),
            Tool("desktop_wait_window", "Espera hasta timeout_ms a que aparezca una ventana por titulo o proceso.", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["target"] = Prop("string", "Subcadena del titulo o nombre de proceso."),
                    ["timeout_ms"] = Prop("integer", "Opcional: tiempo maximo de espera en ms (defecto 10000).")
                },
                ["required"] = new JsonArray("target")
            }),
            Tool("desktop_clipboard", "Lee (get) o escribe (set) texto en el portapapeles de Windows.", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["action"] = Prop("string", "Accion: get o set."),
                    ["text"] = Prop("string", "Texto a copiar cuando action es set.")
                },
                ["required"] = new JsonArray("action")
            }),
            Tool("desktop_click", "Mueve el cursor y hace clic en coordenadas fisicas (x, y) de pantalla.", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["x"] = Prop("integer", "Coordenada X fisica en pantalla."),
                    ["y"] = Prop("integer", "Coordenada Y fisica en pantalla."),
                    ["button"] = Prop("string", "left (defecto), right, middle o double.")
                },
                ["required"] = new JsonArray("x", "y")
            }),
            Tool("desktop_hover", "Mueve el cursor a coordenadas fisicas (x, y) sin hacer clic.", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["x"] = Prop("integer", "Coordenada X fisica."),
                    ["y"] = Prop("integer", "Coordenada Y fisica.")
                },
                ["required"] = new JsonArray("x", "y")
            }),
            Tool("desktop_mouse_relative", "Envia desplazamiento relativo del raton (dx, dy) compatible con entornos 3D y aplicaciones de entrada directa.", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["dx"] = Prop("integer", "Desplazamiento horizontal relativo."),
                    ["dy"] = Prop("integer", "Desplazamiento vertical relativo."),
                    ["steps"] = Prop("integer", "Opcional: dividir el movimiento en N pasos suaves (defecto 1, max 60).")
                },
                ["required"] = new JsonArray("dx", "dy")
            }),
            Tool("desktop_drag", "Arrastra el cursor desde (x1, y1) hasta (x2, y2) con freno Esc.", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["x1"] = Prop("integer", "X inicial."),
                    ["y1"] = Prop("integer", "Y inicial."),
                    ["x2"] = Prop("integer", "X final."),
                    ["y2"] = Prop("integer", "Y final."),
                    ["steps"] = Prop("integer", "Pasos intermedios (defecto 15).")
                },
                ["required"] = new JsonArray("x1", "y1", "x2", "y2")
            }),
            Tool("desktop_scroll", "Envia scroll de rueda del raton en (x, y).", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["x"] = Prop("integer", "Coordenada X."),
                    ["y"] = Prop("integer", "Coordenada Y."),
                    ["delta"] = Prop("integer", "Desplazamiento (+ arriba, - abajo, ej: -360).")
                },
                ["required"] = new JsonArray("x", "y", "delta")
            }),
            Tool("desktop_type", "Escribe texto Unicode (con soporte de saltos de linea y tabulacion) en la ventana enfocada.", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["text"] = Prop("string", "Texto a escribir.")
                },
                ["required"] = new JsonArray("text")
            }),
            Tool("desktop_key", "Envia una tecla o combinacion con ScanCodes de hardware y duracion configurable (hold_ms) con freno Esc.", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["combo"] = Prop("string", "Tecla o combinacion separada por '+' (ej: Enter, Ctrl+S, W)."),
                    ["hold_ms"] = Prop("integer", "Opcional: duracion en milisegundos manteniendo pulsada la tecla (defecto 30, max 15000)."),
                    ["scancode"] = Prop("boolean", "Opcional: usar ScanCodes de hardware (defecto true).")
                },
                ["required"] = new JsonArray("combo")
            })
        };
    }

    internal static JsonObject ExecuteTool(string name, JsonObject args)
    {
        try
        {
            return name switch
            {
                "desktop_screen_summary" => TextResult(AccessibilityReader.GetAccessibleSummaryJson(
                    args["target"]?.GetValue<string>(),
                    args["max_items"]?.GetValue<int?>() ?? 40,
                    args["max_texts"]?.GetValue<int?>() ?? 30,
                    args["timeout_ms"]?.GetValue<int?>() ?? 3000)),
                "desktop_uia_tree" => TextResult(AccessibilityReader.GetUiTreeJson(
                    args["target"]?.GetValue<string>(),
                    args["max_depth"]?.GetValue<int?>() ?? 6,
                    args["max_nodes"]?.GetValue<int?>() ?? 250,
                    args["filter"]?.GetValue<string>(),
                    args["control_type"]?.GetValue<string>(),
                    args["timeout_ms"]?.GetValue<int?>() ?? 4000)),
                "desktop_uia_act" => TextResult(AccessibilityReader.PerformUiAction(
                    args["target"]?.GetValue<string>(),
                    RequireStr(args, "action"),
                    args["name"]?.GetValue<string>(),
                    args["automation_id"]?.GetValue<string>(),
                    args["control_type"]?.GetValue<string>(),
                    args["value"]?.GetValue<string>(),
                    args["index"]?.GetValue<int?>() ?? 0,
                    args["timeout_ms"]?.GetValue<int?>() ?? 4000)),
                "desktop_focused" => TextResult(AccessibilityReader.GetFocusedJson()),
                "desktop_element_at" => TextResult(AccessibilityReader.GetElementAtPointJson(
                    args["x"]?.GetValue<int?>(),
                    args["y"]?.GetValue<int?>())),
                "desktop_watch_events" => TextResult(AccessibilityReader.ControlWatchMcp(RequireStr(args, "action"))),
                "desktop_ocr" => TextResult(OcrReader.RunOcrJson(
                    args["target"]?.GetValue<string>(),
                    args["monitor"]?.GetValue<int?>(),
                    args["region"]?.GetValue<string>(),
                    args["lang"]?.GetValue<string>())),
                "desktop_screenshot" => CaptureScreenshotMcp(
                    args["target"]?.GetValue<string>(),
                    args["monitor"]?.GetValue<int?>(),
                    args["region"]?.GetValue<string>(),
                    args["max_width"]?.GetValue<int?>() ?? 1280,
                    args["format"]?.GetValue<string>() ?? "png",
                    args["quality"]?.GetValue<int?>() ?? 80,
                    args["capture_mode"]?.GetValue<string>() ?? "auto"),
                "desktop_sequence" => TextResult(Program.ExecuteSequenceJson(
                    args["steps"] as JsonArray ?? throw new ArgumentException("Falta el arreglo requerido: steps"),
                    args["abort_on_esc"]?.GetValue<bool?>() ?? true)),
                "desktop_screens" => TextResult(Program.GetScreensJson()),
                "desktop_windows" => TextResult(Program.GetWindowsJson(args["filter"]?.GetValue<string>())),
                "desktop_controls" => TextResult(Program.GetControlsJson(RequireStr(args, "target"))),
                "desktop_launch" => TextResult(Program.LaunchTarget(
                    RequireStr(args, "target"),
                    args["args"]?.GetValue<string>() ?? "")),
                "desktop_focus" => TextResult(Program.FocusTarget(RequireStr(args, "target"))),
                "desktop_window_manage" => TextResult(Program.ManageWindowJson(
                    RequireStr(args, "target"),
                    RequireStr(args, "action"),
                    args["x"]?.GetValue<int?>(),
                    args["y"]?.GetValue<int?>(),
                    args["width"]?.GetValue<int?>(),
                    args["height"]?.GetValue<int?>())),
                "desktop_wait_window" => TextResult(Program.WaitWindowJson(
                    RequireStr(args, "target"),
                    args["timeout_ms"]?.GetValue<int?>() ?? 10000)),
                "desktop_clipboard" => TextResult(Program.ClipboardActionJson(
                    RequireStr(args, "action"),
                    args["text"]?.GetValue<string>())),
                "desktop_click" => TextResult(Program.ClickAt(
                    RequireInt(args, "x"),
                    RequireInt(args, "y"),
                    args["button"]?.GetValue<string>() ?? "left")),
                "desktop_hover" => TextResult(Program.HoverAt(
                    RequireInt(args, "x"),
                    RequireInt(args, "y"))),
                "desktop_mouse_relative" => TextResult(Program.MoveMouseRelative(
                    RequireInt(args, "dx"),
                    RequireInt(args, "dy"),
                    args["steps"]?.GetValue<int?>() ?? 1)),
                "desktop_drag" => TextResult(Program.DragBetween(
                    RequireInt(args, "x1"),
                    RequireInt(args, "y1"),
                    RequireInt(args, "x2"),
                    RequireInt(args, "y2"),
                    args["steps"]?.GetValue<int?>() ?? 15)),
                "desktop_scroll" => TextResult(Program.ScrollAt(
                    RequireInt(args, "x"),
                    RequireInt(args, "y"),
                    RequireInt(args, "delta"))),
                "desktop_type" => TextResult(Program.TypeText(RequireStr(args, "text"))),
                "desktop_key" => TextResult(Program.SendKeyCombo(
                    RequireStr(args, "combo"),
                    args["hold_ms"]?.GetValue<int?>() ?? 30,
                    args["scancode"]?.GetValue<bool?>() ?? true)),
                _ => ErrorResult($"Herramienta desconocida: {name}")
            };
        }
        catch (Exception ex)
        {
            return ErrorResult(ex.Message);
        }
    }

    private static JsonObject CaptureScreenshotMcp(
        string? target,
        int? monitorIdx,
        string? regionSpec,
        int maxWidth,
        string format,
        int quality,
        string captureMode)
    {
        var sw = Stopwatch.StartNew();
        var region = ScreenCapture.ParseRegion(regionSpec);
        var capture = ScreenCapture.CaptureBitmap(target, monitorIdx, region, captureMode);
        using var bmp = capture.Bitmap;
        var encoded = ScreenCapture.EncodeBitmap(bmp, maxWidth, format, quality);
        sw.Stop();

        string base64 = Convert.ToBase64String(encoded.Bytes);

        var meta = JsonSerializer.Serialize(new
        {
            CaptureMode = capture.ModeUsed,
            MimeType = encoded.MimeType,
            Bytes = encoded.Bytes.Length,
            ElapsedMs = sw.ElapsedMilliseconds,
            PhysicalBounds = new
            {
                capture.PhysicalBounds.Left,
                capture.PhysicalBounds.Top,
                capture.PhysicalBounds.Width,
                capture.PhysicalBounds.Height
            },
            ImageSize = new { encoded.Width, encoded.Height },
            encoded.ScaleToImage,
            Note = "Para desktop_click usa coordenadas fisicas: x_phys = Left + x_img / ScaleToImage, y_phys = Top + y_img / ScaleToImage."
        });

        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = meta },
                new JsonObject { ["type"] = "image", ["data"] = base64, ["mimeType"] = encoded.MimeType }
            },
            ["isError"] = false
        };
    }

    private static JsonObject Tool(string name, string description, JsonObject inputSchema) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = inputSchema
    };

    private static JsonObject Prop(string type, string description) => new()
    {
        ["type"] = type,
        ["description"] = description
    };

    private static string RequireStr(JsonObject args, string key) =>
        args[key]?.GetValue<string>() ?? throw new ArgumentException($"Falta parametro requerido: {key}");

    private static int RequireInt(JsonObject args, string key) =>
        args[key]?.GetValue<int?>() ?? throw new ArgumentException($"Falta parametro entero requerido: {key}");

    private static JsonObject TextResult(string text) => new()
    {
        ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } },
        ["isError"] = false
    };

    private static JsonObject ErrorResult(string msg) => new()
    {
        ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = $"ERROR: {msg}" } },
        ["isError"] = true
    };

    private static void SendResult(JsonNode id, JsonObject result)
    {
        var resp = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = result
        };
        Console.WriteLine(resp.ToJsonString(CompactJson));
        Console.Out.Flush();
    }

    private static void SendError(JsonNode? id, int code, string message)
    {
        var resp = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message
            }
        };
        Console.WriteLine(resp.ToJsonString(CompactJson));
        Console.Out.Flush();
    }
}
