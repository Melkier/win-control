using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;

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

        // Notifications have no id
        if (id == null)
        {
            return;
        }

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
                            ["version"] = "1.0.0"
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
            Tool("desktop_uia_tree", "Lee el arbol semantico completo de accesibilidad (UI Automation) de la ventana activa o indicada: textos, botones, enlaces, entradas y coordenadas en Chrome, Edge, Electron, WPF, WinUI y Win32.", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["target"] = Prop("string", "Opcional: HWND o subcadena de titulo/proceso. Si se omite, lee la ventana activa."),
                    ["max_depth"] = Prop("integer", "Opcional: profundidad maxima del arbol (defecto 6, max 15)."),
                    ["max_nodes"] = Prop("integer", "Opcional: maximo de nodos a devolver (defecto 250, max 1500).")
                }
            }),
            Tool("desktop_focused", "Devuelve en tiempo real el elemento actualmente enfocado, su texto/valor, ventana y si el proceso corre elevado.", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject()
            }),
            Tool("desktop_watch_events", "Inicia, consulta o detiene un monitor en tiempo real de eventos de foco y cambios de ventana en todo Windows.", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["action"] = Prop("string", "Accion: start (iniciar), poll (leer eventos acumulados) o stop (detener).")
                },
                ["required"] = new JsonArray("action")
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
            Tool("desktop_screenshot", "Captura la pantalla completa, un monitor o una ventana y devuelve la imagen PNG y sus coordenadas fisicas.", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["target"] = Prop("string", "Opcional: HWND o subcadena de titulo/proceso para capturar solo esa ventana."),
                    ["monitor"] = Prop("integer", "Opcional: indice de monitor (0, 1, ...)."),
                    ["max_width"] = Prop("integer", "Opcional: ancho maximo en pixeles para reducir tamano (por defecto 1280).")
                }
            }),
            Tool("desktop_launch", "Abre una aplicacion, acceso directo (.lnk), carpeta o archivo en el escritorio del usuario.", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["target"] = Prop("string", "Ruta del ejecutable, acceso directo, carpeta o comando."),
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
            Tool("desktop_drag", "Arrastra el cursor desde (x1, y1) hasta (x2, y2).", new JsonObject
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
            Tool("desktop_type", "Escribe texto Unicode en la ventana actualmente enfocada.", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["text"] = Prop("string", "Texto a escribir.")
                },
                ["required"] = new JsonArray("text")
            }),
            Tool("desktop_key", "Envia una tecla o combinacion de atajo (ej: Enter, Esc, Ctrl+S, Alt+F4).", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["combo"] = Prop("string", "Tecla o combinacion separada por '+'.")
                },
                ["required"] = new JsonArray("combo")
            })
        };
    }

    private static JsonObject ExecuteTool(string name, JsonObject args)
    {
        try
        {
            return name switch
            {
                "desktop_screens" => TextResult(Program.GetScreensJson()),
                "desktop_windows" => TextResult(Program.GetWindowsJson(args["filter"]?.GetValue<string>())),
                "desktop_controls" => TextResult(Program.GetControlsJson(RequireStr(args, "target"))),
                "desktop_screenshot" => CaptureScreenshotMcp(
                    args["target"]?.GetValue<string>(),
                    args["monitor"]?.GetValue<int?>(),
                    args["max_width"]?.GetValue<int?>() ?? 1280),
                "desktop_launch" => TextResult(Program.LaunchTarget(
                    RequireStr(args, "target"),
                    args["args"]?.GetValue<string>() ?? "")),
                "desktop_focus" => TextResult(Program.FocusTarget(RequireStr(args, "target"))),
                "desktop_click" => TextResult(Program.ClickAt(
                    RequireInt(args, "x"),
                    RequireInt(args, "y"),
                    args["button"]?.GetValue<string>() ?? "left")),
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
                "desktop_key" => TextResult(Program.SendKeyCombo(RequireStr(args, "combo"))),
                "desktop_uia_tree" => TextResult(AccessibilityReader.GetUiTreeJson(
                    args["target"]?.GetValue<string>(),
                    args["max_depth"]?.GetValue<int?>() ?? 6,
                    args["max_nodes"]?.GetValue<int?>() ?? 250)),
                "desktop_focused" => TextResult(AccessibilityReader.GetFocusedJson()),
                "desktop_watch_events" => TextResult(AccessibilityReader.ControlWatchMcp(RequireStr(args, "action"))),
                _ => ErrorResult($"Herramienta desconocida: {name}")
            };
        }
        catch (Exception ex)
        {
            return ErrorResult(ex.Message);
        }
    }

    private static JsonObject CaptureScreenshotMcp(string? target, int? monitorIdx, int maxWidth)
    {
        Rectangle bounds = Program.ResolveCaptureBounds(target, monitorIdx);
        using var fullBmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(fullBmp))
        {
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        }

        Bitmap toEncode = fullBmp;
        Bitmap? resized = null;
        double scale = 1.0;

        if (maxWidth > 0 && fullBmp.Width > maxWidth)
        {
            scale = (double)maxWidth / fullBmp.Width;
            int newW = maxWidth;
            int newH = Math.Max(1, (int)Math.Round(fullBmp.Height * scale));
            resized = new Bitmap(newW, newH, PixelFormat.Format32bppArgb);
            using (var rg = Graphics.FromImage(resized))
            {
                rg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                rg.DrawImage(fullBmp, 0, 0, newW, newH);
            }
            toEncode = resized;
        }

        int outWidth = toEncode.Width;
        int outHeight = toEncode.Height;
        string base64;
        using (var ms = new MemoryStream())
        {
            toEncode.Save(ms, ImageFormat.Png);
            base64 = Convert.ToBase64String(ms.ToArray());
        }
        resized?.Dispose();

        var meta = JsonSerializer.Serialize(new
        {
            PhysicalBounds = new { bounds.Left, bounds.Top, bounds.Width, bounds.Height },
            ImageSize = new { Width = outWidth, Height = outHeight },
            ScaleToImage = scale,
            Note = "Para desktop_click usa coordenadas fisicas: x_phys = Left + x_img / ScaleToImage, y_phys = Top + y_img / ScaleToImage."
        });

        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = meta },
                new JsonObject { ["type"] = "image", ["data"] = base64, ["mimeType"] = "image/png" }
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


