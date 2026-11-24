using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

// Churchfront Video's MCP server, for Claude. It speaks MCP (JSON-RPC over stdin and stdout)
// and passes every tool call to the running app through its local control interface
// (127.0.0.1:9911, with the key in %APPDATA%\CueVideo\control-token). The tools themselves
// are defined in the app (ControlTools.cs), so this stays tiny. If the app isn't open,
// calling a tool opens it first. Listing the tools doesn't.

const int Port = 9911;
var support = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CueVideo");
// The tool list the app gave last time, for when it's closed.
var toolsCache = Path.Combine(support, "mcp-tools.json");
var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
var relaxed = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
var pretty = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true };

string Token()
{
    try { return File.ReadAllText(Path.Combine(support, "control-token")).Trim(); } catch { return ""; }
}

// One request to the app; null body for GET. Returns the JSON reply, or null if the app didn't answer.
JsonObject? App(string method, string path, JsonObject? body = null, double timeout = 30)
{
    try
    {
        using var req = new HttpRequestMessage(new HttpMethod(method), $"http://127.0.0.1:{Port}{path}");
        req.Headers.Add("X-CueVideo-Token", Token());
        if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
        using var r = http.Send(req, cts.Token);
        using var s = r.Content.ReadAsStream(cts.Token);
        return JsonNode.Parse(s) as JsonObject;
    }
    catch { return null; }
}

// ChurchfrontVideo.exe beside this program.
string? AppExe()
{
    var p = Path.Combine(AppContext.BaseDirectory, "ChurchfrontVideo.exe");
    return File.Exists(p) ? p : null;
}

// Makes sure the app is running and answering; opens it (minimized) if not.
bool EnsureApp()
{
    if (App("GET", "/tools", timeout: 2) != null) return true;
    if (AppExe() is not string exe) return false;
    try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Minimized, WorkingDirectory = AppContext.BaseDirectory }); }
    catch { return false; }
    for (int i = 0; i < 40; i++)
    {
        Thread.Sleep(500);
        if (App("GET", "/tools", timeout: 2) != null) return true;
    }
    return false;
}

var notRunning = $"Churchfront Video isn't running or isn't answering on 127.0.0.1:{Port}. Open Churchfront Video on this computer and try again.";

void Send(JsonObject o) => stdout.WriteLine(o.ToJsonString(relaxed));
void Result(JsonNode id, JsonObject r) => Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = r });
void Failure(JsonNode id, int code, string message) =>
    Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } });

string[] supported = { "2025-06-18", "2025-03-26", "2024-11-05" };

string? line;
while ((line = stdin.ReadLine()) != null)
{
    if (line.Trim().Length == 0) continue;
    JsonObject? msg;
    try { msg = JsonNode.Parse(line) as JsonObject; } catch { continue; }
    if (msg == null) continue;
    var method = msg["method"]?.GetValue<string>() ?? "";
    if (msg["id"] is not JsonNode id) continue;  // notifications need no answer
    var prms = msg["params"] as JsonObject ?? new JsonObject();
    switch (method)
    {
        case "initialize":
        {
            var asked = (prms["protocolVersion"] as JsonValue)?.GetValue<string>() ?? supported[0];
            Result(id, new JsonObject
            {
                ["protocolVersion"] = supported.Contains(asked) ? asked : supported[0],
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                ["serverInfo"] = new JsonObject { ["name"] = "cuevideo", ["version"] = "0.1.2" },
                ["instructions"] = "Controls Churchfront Video, the church booth app for the ATEM switcher and PTZ cameras (this is the Windows version: NDI previews, auto tracking and stage scan aren't in it yet). Setting it up for the first time (get_config shows no atemHost or no cameras): call find_devices to locate the ATEM and cameras on the network, confirm with the person which camera is which and which switcher input each feeds, save them with update_config (atemHost, cameras with name, host, proto, atemInput), then add sources and key buttons. Explain each step in plain words; the person may be a volunteer. Running it: call get_status first to see what's Live and Next, the sources, cameras, shots and key buttons. Cuts, fades and Live changes go out to the congregation and the stream immediately, so confirm with the person before changing what's live during a service. Camera tools act on the camera on Next unless one is named, and refuse a camera that's on Live unless allow_live is true.",
            });
            break;
        }
        case "ping":
            Result(id, new JsonObject());
            break;
        case "tools/list":
        {
            // Listing the tools never opens the app: a Claude session starting must not bring up
            // an app that connects to the switcher and takes control of the cameras.
            if (App("GET", "/tools", timeout: 2)?["tools"] is JsonNode tools)
            {
                try { Directory.CreateDirectory(support); File.WriteAllText(toolsCache, tools.ToJsonString()); } catch { }
                Result(id, new JsonObject { ["tools"] = tools.DeepClone() });
            }
            else
            {
                JsonNode? cached = null;
                try { cached = JsonNode.Parse(File.ReadAllText(toolsCache)); } catch { }
                Result(id, new JsonObject
                {
                    ["tools"] = cached ?? new JsonArray(new JsonObject
                    {
                        ["name"] = "get_status", ["description"] = "Status of Churchfront Video. (Churchfront Video isn't running right now.)",
                        ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
                    }),
                });
            }
            break;
        }
        case "tools/call":
        {
            var name = (prms["name"] as JsonValue)?.GetValue<string>() ?? "";
            var callArgs = prms["arguments"] as JsonObject ?? new JsonObject();
            JsonObject? r = null;
            if (EnsureApp()) r = App("POST", "/call", new JsonObject { ["name"] = name, ["arguments"] = callArgs.DeepClone() }, timeout: 120);
            if (r == null)
            {
                Result(id, new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = notRunning }), ["isError"] = true });
                break;
            }
            // Pictures (camera_snapshots) go back as MCP image content, each after a label.
            var images = r["images"] as JsonArray;
            r.Remove("images");
            var content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = r.ToJsonString(pretty) });
            if (images != null)
                foreach (var img in images)
                {
                    if ((img?["data"] as JsonValue)?.GetValue<string>() is not string data) continue;
                    content.Add(new JsonObject { ["type"] = "text", ["text"] = (img["label"] as JsonValue)?.GetValue<string>() ?? "Picture" });
                    content.Add(new JsonObject { ["type"] = "image", ["data"] = data, ["mimeType"] = (img["mimeType"] as JsonValue)?.GetValue<string>() ?? "image/jpeg" });
                }
            Result(id, new JsonObject { ["content"] = content, ["isError"] = r["error"] != null });
            break;
        }
        default:
            Failure(id, -32601, $"Unknown method {method}");
            break;
    }
}
