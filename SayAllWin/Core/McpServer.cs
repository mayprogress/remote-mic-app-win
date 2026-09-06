using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SayAll.Core;

/// <summary>
/// 本地 Agent 访问：stdio JSON-RPC（MCP 风格）服务，只读访问“回眸”历史
/// （自 SayAllMCP 移植，最小实现：initialize / tools/list / tools/call + transcript_search）。
/// 不监听 HTTP/TCP，不输出任何用户内容之外的数据。
/// </summary>
public sealed class SayAllMcpServer : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly AppSettings _settings;
    private Thread? _thread;
    private volatile bool _running;
    private readonly string _serverInfo =
        $"{{\"name\":\"sayall-windows-mcp\",\"version\":\"{VersionInfo.Version}\"}}";

    public SayAllMcpServer(AppSettings settings)
    {
        _settings = settings;
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "SayAllMcpStdio" };
        _thread.Start();
    }

    /// <summary>命令行模式：在当前线程运行 stdio 循环直到 stdin 关闭（SayAll.exe --mcp-stdio）。</summary>
    public void RunOnCurrentThread()
    {
        _running = true;
        Loop();
        _running = false;
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(500);
        _thread = null;
    }

    private void Loop()
    {
        try
        {
            using var stdin = Console.OpenStandardInput();
            using var stdout = Console.OpenStandardOutput();
            using var reader = new StreamReader(stdin);
            var writer = new StreamWriter(stdout) { AutoFlush = true };

            while (_running)
            {
                var line = reader.ReadLine();
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonNode? response = null;
                try
                {
                    var request = JsonNode.Parse(line);
                    response = HandleRequest(request as JsonObject);
                }
                catch (Exception ex)
                {
                    response = ErrorResponse(null, -32700, "Parse error: " + ex.Message);
                }
                if (response is not null)
                {
                    writer.WriteLine(response.ToJsonString());
                }
            }
        }
        catch
        {
            // stdin 关闭或后台进程终止
        }
    }

    private JsonObject? HandleRequest(JsonObject? request)
    {
        if (request is null) return ErrorResponse(null, -32600, "Invalid Request");
        var id = request["id"]?.DeepClone();
        var method = request["method"]?.GetValue<string>();
        var @params = request["params"] as JsonObject ?? new JsonObject();
        var notify = request["id"] is null;

        switch (method)
        {
            case "initialize":
                return SuccessResponse(id, new JsonObject
                {
                    ["protocolVersion"] = "2025-06-18",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = JsonNode.Parse(_serverInfo),
                });
            case "ping":
                return SuccessResponse(id, new JsonObject());
            case "notifications/initialized":
                return null; // 通知：无响应
            case "tools/list":
                return SuccessResponse(id, new JsonObject
                {
                    ["tools"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["name"] = "transcript_search",
                            ["description"] = "Read-only search over SayAll lookback transcript history (never audio).",
                            ["inputSchema"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Optional substring filter on transcript text." },
                                    ["app"] = new JsonObject { ["type"] = "string", ["description"] = "Optional app title filter." },
                                    ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max records, default 20." },
                                },
                            },
                        },
                    },
                });
            case "tools/call":
                var name = @params["name"]?.GetValue<string>();
                var arguments = @params["arguments"] as JsonObject ?? new JsonObject();
                if (name == "transcript_search")
                {
                    return HandleTranscriptSearch(id, arguments);
                }
                return ErrorResponse(id, -32601, "Unknown tool: " + name);
            case "resources/list":
                return SuccessResponse(id, new JsonObject { ["resources"] = new JsonArray() });
            default:
                if (notify) return null;
                return ErrorResponse(id, -32601, "Method not found: " + method);
        }
    }

    private JsonObject HandleTranscriptSearch(JsonNode? id, JsonObject arguments)
    {
        var query = arguments["query"]?.GetValue<string>()?.Trim().ToLowerInvariant();
        var app = arguments["app"]?.GetValue<string>()?.Trim();
        var limit = Math.Clamp(arguments["limit"]?.GetValue<int>() ?? 20, 1, 100);

        var records = _settings.LoadTranscripts()
            .Where(r => query is null || (r.Text ?? "").ToLowerInvariant().Contains(query))
            .Where(r => app is null || (r.App ?? "").Contains(app, StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .Select(r => new JsonObject
            {
                ["startedAt"] = r.StartedAt.ToString("o"),
                ["app"] = r.App,
                ["text"] = r.Text,
                ["source"] = r.Source,
            })
            .ToList();

        return SuccessResponse(id, new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = JsonSerializer.Serialize(records),
                },
            },
        });
    }

    private static JsonObject SuccessResponse(JsonNode? id, JsonObject result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = result,
    };

    private static JsonObject ErrorResponse(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
        },
    };

    public void Dispose() => Stop();
}