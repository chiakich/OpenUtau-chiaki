using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using OpenUtau.Core.Agent;

namespace OpenUtau.Cli {
    // The session protocol as Model Context Protocol tools, over stdio: one tool per op.
    sealed class Mcp {
        const string LatestProtocol = "2025-06-18";
        static readonly string[] KnownProtocols = { "2025-06-18", "2025-03-26", "2024-11-05" };
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(10);

        sealed record Tool(string Name, string Op, bool ReadOnly, JsonObject Properties, string[] Required);

        static JsonObject P(string type) => new JsonObject { ["type"] = type };
        static JsonObject Tone() => new JsonObject { ["type"] = new JsonArray("integer", "string") };
        static JsonObject Props(params (string name, JsonObject schema)[] items) {
            var result = new JsonObject();
            foreach (var (name, schema) in items) {
                result[name] = schema;
            }
            return result;
        }
        static JsonObject ArrayOf(JsonObject? items = null) {
            var schema = P("array");
            if (items != null) {
                schema["items"] = items;
            }
            return schema;
        }

        // Types only: what each field means is in the tool's description, taken from the session's own help.
        static readonly Tool[] Tools = {
            new Tool("status", "status", true, Props(), new string[0]),
            new Tool("new_project", "new", false, Props(("score", P("object")), ("discard_changes", P("boolean")), ("expect_revision", P("integer"))), new[] { "score" }),
            new Tool("open_project", "open", false, Props(("path", P("string")), ("discard_changes", P("boolean")), ("expect_revision", P("integer"))), new[] { "path" }),
            new Tool("save_project", "save", false, Props(("path", P("string"))), new string[0]),
            new Tool("get_notes", "get", true, Props(("part", P("integer")), ("from_tick", P("integer")), ("to_tick", P("integer")),
                ("detail", new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("notes", "phonemes", "phrases") }),
                ("curves", ArrayOf(P("string")))), new string[0]),
            new Tool("add_notes", "add_notes", false, Props(("notes", ArrayOf(new JsonObject())), ("at", P("integer")), ("after", P("integer")),
                ("part", P("integer")), ("track", P("integer")), ("expect_revision", P("integer"))), new[] { "notes" }),
            new Tool("edit_notes", "edit_notes", false, Props(("notes", ArrayOf(new JsonObject {
                ["type"] = "object",
                ["properties"] = Props(("id", P("integer")), ("lyric", P("string")), ("tone", Tone()), ("tick", P("integer")),
                    ("duration", new JsonObject { ["type"] = new JsonArray("integer", "string") })),
                ["required"] = new JsonArray("id"),
            })), ("expect_revision", P("integer"))), new[] { "notes" }),
            new Tool("delete_notes", "delete_notes", false, Props(("ids", ArrayOf(P("integer"))), ("expect_revision", P("integer"))), new[] { "ids" }),
            new Tool("set_curve", "set_curve", false, Props(("abbr", P("string")), ("points", ArrayOf(ArrayOf(P("number")))),
                ("clear", P("boolean")), ("from_tick", P("integer")), ("to_tick", P("integer")), ("default", P("number")),
                ("reset_default", P("boolean")), ("part", P("integer")), ("track", P("integer")), ("expect_revision", P("integer"))), new[] { "abbr" }),
            new Tool("set_tempo", "set_tempo", false, Props(("bpm", P("number")), ("expect_revision", P("integer"))), new[] { "bpm" }),
            new Tool("set_track", "set_track", false, Props(("track", P("integer")), ("singer", P("string")), ("phonemizer", P("string")),
                ("expect_revision", P("integer"))), new string[0]),
            new Tool("load_pitch", "load_pitch", false, Props(("mode", new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("untuned", "all") }),
                ("part", P("integer")), ("expect_revision", P("integer"))), new string[0]),
            new Tool("render", "render", false, Props(("out", P("string")), ("from_tick", P("integer")), ("to_tick", P("integer")),
                ("track", P("integer")), ("mixdown", P("boolean")), ("allow_errors", P("boolean"))), new[] { "out" }),
            new Tool("undo", "undo", false, Props(("expect_revision", P("integer"))), new string[0]),
            new Tool("redo", "redo", false, Props(("expect_revision", P("integer"))), new string[0]),
            new Tool("list_singers", "singers", true, Props(), new string[0]),
            new Tool("list_phonemizers", "phonemizers", true, Props(("singer", P("string")), ("query", P("string")), ("lyrics", new JsonObject { ["type"] = "array", ["items"] = P("string") })), new string[0]),
            new Tool("list_expressions", "expressions", true, Props(("singer", P("string")), ("track", P("integer"))), new string[0]),
        };

        readonly Func<string, string> send;
        readonly string where;
        readonly JsonObject help = AgentSession.Describe();

        // send carries one session request and returns its reply; where says what the session works on.
        public Mcp(Func<string, string> send, string where) {
            this.send = send;
            this.where = where;
        }

        public void Serve(Stream input, Stream output) {
            void Write(JsonObject message) {
                byte[] bytes = Utf8.GetBytes(message.ToJsonString() + "\n");
                lock (output) {
                    output.Write(bytes, 0, bytes.Length);
                    output.Flush();
                }
            }
            // Tools run one at a time on their own thread: a render takes minutes, and a client pings meanwhile.
            using var calls = new BlockingCollection<JsonObject>();
            var worker = new Thread(() => {
                foreach (var message in calls.GetConsumingEnumerable()) {
                    Write(CallWithProgress(message, Write));
                }
            }) { IsBackground = true, Name = "MCP tools" };
            worker.Start();
            using var reader = new StreamReader(input, Utf8, false, 4096, leaveOpen: true);
            string? line;
            while ((line = reader.ReadLine()) != null) {
                if (line.Trim().Length == 0) {
                    continue;
                }
                var reply = Handle(line, calls.Add);
                if (reply != null) {
                    Write(reply);
                }
            }
            calls.CompleteAdding();
            worker.Join();
        }

        // Null for a notification, which gets no reply, and for a tool call handed to later.
        public JsonObject? Handle(string line, Action<JsonObject>? later = null) {
            JsonObject? message;
            try {
                message = JsonNode.Parse(line) as JsonObject;
            } catch (JsonException) {
                message = null;
            }
            if (message == null) {
                return Error(null, -32700, "Not a JSON-RPC message.");
            }
            var id = message["id"]?.DeepClone();
            string? method = message["method"] is JsonValue m && m.TryGetValue(out string? name) ? name : null;
            if (method == null) {
                // A reply to something this server never asked.
                return null;
            }
            if (id == null) {
                return null;
            }
            try {
                switch (method) {
                    case "initialize": return Result(id, Initialize(message["params"] as JsonObject));
                    case "ping": return Result(id, new JsonObject());
                    case "tools/list": return Result(id, new JsonObject { ["tools"] = new JsonArray(Tools.Select(t => (JsonNode)Describe(t)).ToArray()) });
                    case "tools/call":
                        if (later != null) {
                            later(message);
                            return null;
                        }
                        return Result(id, Call(message["params"] as JsonObject));
                    default: return Error(id, -32601, $"Unknown method {method}.");
                }
            } catch (ArgumentException e) {
                return Error(id, -32602, e.Message);
            } catch (Exception e) {
                // The server outlives a request it could not answer.
                return Error(id, -32603, e.Message);
            }
        }

        // A client that sent a progress token is told the call is alive, which also resets its timeout.
        JsonObject CallWithProgress(JsonObject message, Action<JsonObject> write) {
            var id = message["id"]!.DeepClone();
            var parameters = message["params"] as JsonObject;
            var token = (parameters?["_meta"] as JsonObject)?["progressToken"];
            int beats = 0;
            using var timer = token == null ? null : new Timer(_ => {
                try {
                    write(new JsonObject {
                        ["jsonrpc"] = "2.0",
                        ["method"] = "notifications/progress",
                        ["params"] = new JsonObject {
                            ["progressToken"] = token.DeepClone(),
                            ["progress"] = Interlocked.Increment(ref beats),
                            ["message"] = "Still working.",
                        },
                    });
                } catch (Exception) {
                    // The client went away; the call still finishes.
                }
            }, null, ProgressInterval, ProgressInterval);
            try {
                return Result(id, Call(parameters));
            } catch (ArgumentException e) {
                return Error(id, -32602, e.Message);
            } catch (Exception e) {
                return Error(id, -32603, e.Message);
            } finally {
                // No progress may follow the reply.
                if (timer != null) {
                    using var stopped = new ManualResetEvent(false);
                    if (timer.Dispose(stopped)) {
                        stopped.WaitOne();
                    }
                }
            }
        }

        JsonObject Initialize(JsonObject? parameters) {
            string? asked = parameters?["protocolVersion"] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
            return new JsonObject {
                ["protocolVersion"] = asked != null && KnownProtocols.Contains(asked) ? asked : LatestProtocol,
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                ["serverInfo"] = new JsonObject {
                    ["name"] = "openutau-chiaki",
                    ["version"] = typeof(Mcp).Assembly.GetName().Version?.ToString() ?? "0",
                },
                ["instructions"] = $"Singing synthesis with OpenUtau (the Chiaki fork, not the official app) and DiffSinger voicebanks. {where} "
                    + "Start with status, and list_singers before choosing a singer. Notes have ids; get_notes returns them with the phonemes "
                    + "they are sung with. Every reply is JSON with ok, revision, warnings and diagnostics, and error {code, message} when ok is false. "
                    + ToolNames($"{help["units"]} {help["revisions"]} {help["tips"]}"),
            };
        }

        // The session's help speaks of its ops; a tool whose name differs is called by that name here.
        static string ToolNames(string text) {
            foreach (var tool in Tools.Where(t => t.Name != t.Op)) {
                text = System.Text.RegularExpressions.Regex.Replace(text, $@"(?<![\w""])({tool.Op})(?= (lists|shows|with|writes)\b)", tool.Name);
            }
            return text;
        }

        JsonObject Describe(Tool tool) {
            var op = (JsonObject)help["ops"]![tool.Op]!;
            var description = new StringBuilder(ToolNames(op["summary"]!.GetValue<string>()));
            foreach (var field in (JsonArray)op["fields"]!) {
                description.Append("\n- ").Append(ToolNames(field!.GetValue<string>()));
            }
            if (tool.Properties.ContainsKey("expect_revision")) {
                description.Append("\n- expect_revision: refuse unless the project is still at this revision");
            }
            var schema = new JsonObject {
                ["type"] = "object",
                ["properties"] = tool.Properties.DeepClone(),
            };
            if (tool.Required.Length > 0) {
                schema["required"] = new JsonArray(tool.Required.Select(r => (JsonNode)r).ToArray());
            }
            return new JsonObject {
                ["name"] = tool.Name,
                ["description"] = description.ToString(),
                ["inputSchema"] = schema,
                ["annotations"] = new JsonObject { ["readOnlyHint"] = tool.ReadOnly },
            };
        }

        JsonObject Call(JsonObject? parameters) {
            string? name = parameters?["name"] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
            var tool = Tools.FirstOrDefault(t => t.Name == name) ?? throw new ArgumentException($"Unknown tool {name}.");
            var request = new JsonObject { ["op"] = tool.Op };
            if (parameters?["arguments"] is JsonObject arguments) {
                foreach (var pair in arguments) {
                    if (pair.Key == "op" || pair.Key == "id") {
                        throw new ArgumentException($"\"{pair.Key}\" is not an argument of {tool.Name}.");
                    }
                    request[pair.Key] = pair.Value?.DeepClone();
                }
            }
            string reply;
            bool failed;
            try {
                reply = send(request.ToJsonString());
                failed = !(JsonNode.Parse(reply) is JsonObject json && json["ok"] is JsonValue ok && ok.TryGetValue(out bool fine) && fine);
            } catch (AgentException e) {
                // Typically the app not running: the tool failed, the server goes on.
                reply = new JsonObject {
                    ["ok"] = false,
                    ["op"] = tool.Op,
                    ["error"] = new JsonObject { ["code"] = e.Code, ["message"] = e.Message },
                }.ToJsonString();
                failed = true;
            }
            return new JsonObject {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = reply }),
                ["isError"] = failed,
            };
        }

        static JsonObject Result(JsonNode id, JsonObject result) => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

        static JsonObject Error(JsonNode? id, int code, string message) => new JsonObject {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        };
    }
}
