using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace PS2Disassembler
{
    internal sealed class DebugServerClient : IDisposable
    {
        private string _host = AppSettings.DefaultDebugHost;
        private int _port = AppSettings.DefaultMcpPort;
        private const int ConnectTimeoutMs = 2000;
        private const int IoTimeoutMs = 3000;

        private readonly object _sync = new();
        private TcpClient? _client;
        private NetworkStream? _stream;
        private StreamReader? _reader;
        private StreamWriter? _writer;

        public string Host => _host;
        public int Port => _port;

        public bool IsConnected
        {
            get
            {
                lock (_sync)
                {
                    return _client?.Connected == true && _stream != null && _reader != null && _writer != null;
                }
            }
        }

        public void Configure(string? host, int port)
        {
            lock (_sync)
            {
                string nextHost = AppSettings.NormalizeDebugHost(host);
                int nextPort = AppSettings.NormalizePort(port, AppSettings.DefaultMcpPort);
                bool endpointChanged = !string.Equals(_host, nextHost, StringComparison.OrdinalIgnoreCase) || _port != nextPort;
                _host = nextHost;
                _port = nextPort;
                if (endpointChanged)
                    DisconnectLocked();
            }
        }

        public void Connect(int timeoutMs = ConnectTimeoutMs)
        {
            lock (_sync)
            {
                DisconnectLocked();

                var client = new TcpClient();
                var ar = client.BeginConnect(_host, _port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(timeoutMs)))
                {
                    client.Close();
                    throw new IOException("Debug server timeout.");
                }

                client.EndConnect(ar);
                client.NoDelay = true;
                client.ReceiveTimeout = IoTimeoutMs;
                client.SendTimeout = IoTimeoutMs;

                _client = client;
                _stream = client.GetStream();
                _reader = new StreamReader(_stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
                _writer = new StreamWriter(_stream, new UTF8Encoding(false), bufferSize: 4096, leaveOpen: true)
                {
                    NewLine = "\n",
                    AutoFlush = true,
                };
            }
        }

        public void Disconnect()
        {
            lock (_sync) { DisconnectLocked(); }
        }

        private void DisconnectLocked()
        {
            try { _writer?.Dispose(); } catch { }
            try { _reader?.Dispose(); } catch { }
            try { _stream?.Dispose(); } catch { }
            try { _client?.Close(); } catch { }
            _writer = null;
            _reader = null;
            _stream = null;
            _client = null;
        }

        public DebugServerStatus GetStatus(string cpu = "ee")
        {
            using JsonDocument doc = SendCommand(new Dictionary<string, object?>
            {
                ["cmd"] = "status",
                ["cpu"] = cpu,
            });

            JsonElement data = RequireProperty(doc.RootElement, "data");
            return new DebugServerStatus
            {
                Alive = GetBool(data, "alive"),
                Paused = GetBool(data, "paused"),
                Pc = GetUInt32(data, "pc"),
                Cycles = GetInt64(data, "cycles"),
            };
        }

        public void SetBreakpoint(uint address, bool temporary = false, bool enabled = true, string cpu = "ee", string? description = null, string? condition = null)
        {
            var cmd = new Dictionary<string, object?>
            {
                ["cmd"] = "set_breakpoint",
                ["cpu"] = cpu,
                ["address"] = address,
                ["temporary"] = temporary,
                ["enabled"] = enabled,
            };

            if (!string.IsNullOrWhiteSpace(description)) cmd["description"] = description;
            if (!string.IsNullOrWhiteSpace(condition)) cmd["condition"] = condition;
            using JsonDocument _ = SendCommand(cmd);
        }

        public void RemoveBreakpoint(uint address, string cpu = "ee")
        {
            using JsonDocument _ = SendCommand(new Dictionary<string, object?>
            {
                ["cmd"] = "remove_breakpoint",
                ["cpu"] = cpu,
                ["address"] = address,
            });
        }

        public IReadOnlyList<DebugBreakpointInfo> ListBreakpoints(string cpu = "ee")
        {
            using JsonDocument doc = SendCommand(new Dictionary<string, object?>
            {
                ["cmd"] = "list_breakpoints",
                ["cpu"] = cpu,
            });

            JsonElement arr = RequireProperty(doc.RootElement, "breakpoints");
            var list = new List<DebugBreakpointInfo>();
            foreach (JsonElement item in arr.EnumerateArray())
            {
                list.Add(new DebugBreakpointInfo
                {
                    Address = GetUInt32(item, "address"),
                    Enabled = GetBool(item, "enabled"),
                    Temporary = GetBool(item, "temporary"),
                    Stepping = GetBool(item, "stepping"),
                    HasCondition = GetBool(item, "has_condition"),
                    Condition = GetString(item, "condition"),
                    Description = GetString(item, "description"),
                });
            }
            return list;
        }

        public void SetMemcheck(uint startAddress, uint endAddress, string type, string cpu = "ee", string action = "break", string? description = null, string? condition = null)
        {
            var cmd = new Dictionary<string, object?>
            {
                ["cmd"] = "set_memcheck",
                ["cpu"] = cpu,
                ["address"] = startAddress,
                ["end"] = endAddress,
                ["type"] = type,
                ["action"] = action,
            };

            if (!string.IsNullOrWhiteSpace(description)) cmd["description"] = description;
            if (!string.IsNullOrWhiteSpace(condition)) cmd["condition"] = condition;
            using JsonDocument _ = SendCommand(cmd);
        }

        public void RemoveMemcheck(uint startAddress, uint endAddress, string cpu = "ee")
        {
            using JsonDocument _ = SendCommand(new Dictionary<string, object?>
            {
                ["cmd"] = "remove_memcheck",
                ["cpu"] = cpu,
                ["address"] = startAddress,
                ["end"] = endAddress,
            });
        }

        public IReadOnlyList<DebugMemcheckInfo> ListMemchecks(string cpu = "ee")
        {
            using JsonDocument doc = SendCommand(new Dictionary<string, object?>
            {
                ["cmd"] = "list_memchecks",
                ["cpu"] = cpu,
            });

            JsonElement arr = RequireProperty(doc.RootElement, "memchecks");
            var list = new List<DebugMemcheckInfo>();
            foreach (JsonElement item in arr.EnumerateArray())
            {
                list.Add(new DebugMemcheckInfo
                {
                    Start = GetUInt32(item, "start"),
                    End = GetUInt32(item, "end"),
                    Hits = GetInt64(item, "hits"),
                    LastPc = GetUInt32(item, "last_pc"),
                    LastAddr = GetUInt32(item, "last_addr"),
                    Description = GetString(item, "description"),
                });
            }
            return list;
        }

        public void Pause(string cpu = "ee")
        {
            using JsonDocument _ = SendCommand(new Dictionary<string, object?>
            {
                ["cmd"] = "pause",
                ["cpu"] = cpu,
            });
        }

        public void Resume(string cpu = "ee")
        {
            using JsonDocument _ = SendCommand(new Dictionary<string, object?>
            {
                ["cmd"] = "resume",
                ["cpu"] = cpu,
            });
        }

        public DebugStepResult Step(string cpu = "ee") => StepLike("step", cpu);
        public DebugStepResult StepOver(string cpu = "ee") => StepLike("step_over", cpu);

        private DebugStepResult StepLike(string command, string cpu)
        {
            using JsonDocument doc = SendCommand(new Dictionary<string, object?>
            {
                ["cmd"] = command,
                ["cpu"] = cpu,
            });

            JsonElement root = doc.RootElement;
            return new DebugStepResult
            {
                OldPc = GetUInt32(root, "old_pc"),
                NewPc = GetUInt32(root, "new_pc"),
                Disasm = GetString(root, "disasm"),
                InBios = GetBool(root, "in_bios"),
                Opcode = TryGetUInt32(root, "opcode"),
            };
        }

        public DebugRegisterSnapshot ReadRegisters(string cpu = "ee")
        {
            using JsonDocument doc = SendCommand(new Dictionary<string, object?>
            {
                ["cmd"] = "read_registers",
                ["cpu"] = cpu,
            });

            JsonElement data = RequireProperty(doc.RootElement, "data");
            var snapshot = new DebugRegisterSnapshot
            {
                Pc = GetUInt32(data, "pc"),
                Hi = GetString(data, "hi"),
                Lo = GetString(data, "lo"),
            };

            foreach (JsonProperty property in data.EnumerateObject())
            {
                if (property.NameEquals("pc") || property.NameEquals("hi") || property.NameEquals("lo"))
                    continue;
                if (property.Value.ValueKind != JsonValueKind.Object)
                    continue;
                if (!property.Value.TryGetProperty("regs", out JsonElement regsEl) || regsEl.ValueKind != JsonValueKind.Array)
                    continue;

                var category = new DebugRegisterCategory
                {
                    Name = property.Name,
                    Size = TryGetInt32(property.Value, "size") ?? 0,
                    Count = TryGetInt32(property.Value, "count") ?? 0,
                };

                foreach (JsonElement regEl in regsEl.EnumerateArray())
                {
                    category.Registers.Add(new DebugRegisterValue
                    {
                        Name = GetString(regEl, "name") ?? string.Empty,
                        Value = GetString(regEl, "value"),
                        Display = GetString(regEl, "display"),
                    });
                }

                snapshot.Categories.Add(category);
            }

            return snapshot;
        }

        public IReadOnlyList<DebugBacktraceFrame> GetBacktrace(int maxFrames = 32, string cpu = "ee")
        {
            using JsonDocument doc = SendCommand(new Dictionary<string, object?>
            {
                ["cmd"] = "get_backtrace",
                ["cpu"] = cpu,
                ["max_frames"] = maxFrames,
            });

            JsonElement framesEl = RequireProperty(doc.RootElement, "frames");
            var frames = new List<DebugBacktraceFrame>();
            foreach (JsonElement frameEl in framesEl.EnumerateArray())
            {
                frames.Add(new DebugBacktraceFrame
                {
                    Entry = GetUInt32(frameEl, "entry"),
                    Pc = GetUInt32(frameEl, "pc"),
                    Sp = GetUInt32(frameEl, "sp"),
                    StackSize = TryGetInt32(frameEl, "stack_size") ?? 0,
                    Disasm = GetString(frameEl, "disasm"),
                });
            }
            return frames;
        }


        public IReadOnlyList<DebugThreadInfo> ListThreads(string cpu = "ee")
        {
            using JsonDocument doc = SendCommand(new Dictionary<string, object?>
            {
                ["cmd"] = "get_threads",
                ["cpu"] = cpu,
            });

            JsonElement threadsEl = RequireProperty(doc.RootElement, "threads");
            var threads = new List<DebugThreadInfo>();
            foreach (JsonElement threadEl in threadsEl.EnumerateArray())
            {
                threads.Add(new DebugThreadInfo
                {
                    Id = TryGetInt32(threadEl, "id") ?? 0,
                    Pc = GetUInt32(threadEl, "pc"),
                    Status = TryGetInt32(threadEl, "status") ?? 0,
                    WaitType = TryGetInt32(threadEl, "wait_type") ?? 0,
                });
            }
            return threads;
        }

        public void ClearBreakpoints(string cpu = "ee")
        {
            using JsonDocument _ = SendCommand(new Dictionary<string, object?>
            {
                ["cmd"] = "clear_breakpoints",
                ["cpu"] = cpu,
            });
        }

        private JsonDocument SendCommand(IReadOnlyDictionary<string, object?> command)
        {
            lock (_sync)
            {
                // First attempt
                var ex1 = TrySendCommandLocked(command, out JsonDocument? doc1);
                if (ex1 == null && doc1 != null)
                    return doc1;

                // If the server explicitly rejected the command (ok:false), don't retry —
                // a reconnect won't change the server's answer.
                if (ex1 is InvalidOperationException)
                    throw new IOException(ex1.Message);

                // I/O failure — tear down the broken connection and reconnect for a single retry.
                DisconnectLocked();

                try
                {
                    var client = new TcpClient();
                    var ar = client.BeginConnect(_host, _port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(ConnectTimeoutMs)))
                    {
                        client.Close();
                        throw new IOException($"Debug server reconnect timed out after first failure: {ex1?.Message}");
                    }
                    client.EndConnect(ar);
                    client.NoDelay = true;
                    client.ReceiveTimeout = IoTimeoutMs;
                    client.SendTimeout = IoTimeoutMs;

                    _client = client;
                    _stream = client.GetStream();
                    _reader = new StreamReader(_stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
                    _writer = new StreamWriter(_stream, new UTF8Encoding(false), bufferSize: 4096, leaveOpen: true)
                    {
                        NewLine = "\n",
                        AutoFlush = true,
                    };
                }
                catch
                {
                    DisconnectLocked();
                    throw new IOException($"Debug server reconnect failed: {ex1?.Message}");
                }

                // Retry once on the fresh connection
                var ex2 = TrySendCommandLocked(command, out JsonDocument? doc2);
                if (ex2 == null && doc2 != null)
                    return doc2;

                DisconnectLocked();
                throw new IOException($"Debug server command failed after reconnect: {(ex2 ?? ex1)?.Message}");
            }
        }

        /// <summary>
        /// Attempts to send a command and read the response on the current connection.
        /// Returns null on success (doc is set), or the exception on failure (doc is null).
        /// On I/O failure the connection is cleaned up.  On logical errors (server returned
        /// ok:false) the connection is left intact — the caller should not retry.
        /// </summary>
        private Exception? TrySendCommandLocked(IReadOnlyDictionary<string, object?> command, out JsonDocument? doc)
        {
            doc = null;

            if (_client == null || _stream == null || _reader == null || _writer == null || !_client.Connected)
                return new IOException("Debug server not connected.");

            try
            {
                string payload = JsonSerializer.Serialize(command);
                _writer.WriteLine(payload);
                string? response = _reader.ReadLine();
                if (string.IsNullOrWhiteSpace(response))
                {
                    DisconnectLocked();
                    return new IOException("Debug server returned no data.");
                }

                doc = JsonDocument.Parse(response);
                JsonElement root = doc.RootElement;
                if (root.TryGetProperty("ok", out JsonElement okEl) && okEl.ValueKind == JsonValueKind.False)
                {
                    string err = GetString(root, "error") ?? "Debug server command failed.";
                    doc.Dispose();
                    doc = null;
                    // Logical error — connection is fine, don't disconnect.
                    // Wrap in InvalidOperationException so SendCommand knows not to retry.
                    return new InvalidOperationException(err);
                }
                return null; // success
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                doc = null;
                DisconnectLocked();
                return ex;
            }
        }

        private static JsonElement RequireProperty(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out JsonElement value))
                throw new InvalidDataException($"Debug server response is missing '{name}'.");
            return value;
        }

        private static bool GetBool(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out JsonElement value))
                return false;
            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String when bool.TryParse(value.GetString(), out bool parsed) => parsed,
                JsonValueKind.Number => value.GetInt32() != 0,
                _ => false,
            };
        }

        private static long GetInt64(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out JsonElement value))
                return 0;
            return value.ValueKind switch
            {
                JsonValueKind.Number => value.GetInt64(),
                JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) => parsed,
                _ => 0,
            };
        }

        private static int? TryGetInt32(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out JsonElement value))
                return null;
            return value.ValueKind switch
            {
                JsonValueKind.Number => value.GetInt32(),
                JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) => parsed,
                _ => null,
            };
        }

        private static uint GetUInt32(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out JsonElement value))
                return 0;
            return ParseUInt32(value);
        }

        private static uint? TryGetUInt32(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out JsonElement value))
                return null;
            return ParseUInt32(value);
        }

        private static uint ParseUInt32(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.Number => element.GetUInt32(),
                JsonValueKind.String => ParseUInt32String(element.GetString()),
                _ => 0,
            };
        }

        private static uint ParseUInt32String(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return 0;
            string value = text.Trim();
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                value = value[2..];
            return uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint parsed)
                ? parsed
                : 0;
        }

        private static string? GetString(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out JsonElement value))
                return null;
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => bool.TrueString,
                JsonValueKind.False => bool.FalseString,
                _ => null,
            };
        }

        public void Dispose() => Disconnect();
    }

    internal sealed class DebugServerStatus
    {
        public bool Alive { get; init; }
        public bool Paused { get; init; }
        public uint Pc { get; init; }
        public long Cycles { get; init; }
    }

    internal sealed class DebugBreakpointInfo
    {
        public uint Address { get; init; }
        public bool Enabled { get; init; }
        public bool Temporary { get; init; }
        public bool Stepping { get; init; }
        public bool HasCondition { get; init; }
        public string? Condition { get; init; }
        public string? Description { get; init; }
    }

    internal sealed class DebugMemcheckInfo
    {
        public uint Start { get; init; }
        public uint End { get; init; }
        public long Hits { get; init; }
        public uint LastPc { get; init; }
        public uint LastAddr { get; init; }
        public string? Description { get; init; }
    }

    internal sealed class DebugStepResult
    {
        public uint OldPc { get; init; }
        public uint NewPc { get; init; }
        public string? Disasm { get; init; }
        public bool InBios { get; init; }
        public uint? Opcode { get; init; }
    }

    internal sealed class DebugRegisterSnapshot
    {
        public List<DebugRegisterCategory> Categories { get; } = new();
        public uint Pc { get; init; }
        public string? Hi { get; init; }
        public string? Lo { get; init; }
    }

    internal sealed class DebugRegisterCategory
    {
        public string Name { get; init; } = string.Empty;
        public int Size { get; init; }
        public int Count { get; init; }
        public List<DebugRegisterValue> Registers { get; } = new();
    }

    internal sealed class DebugRegisterValue
    {
        public string Name { get; init; } = string.Empty;
        public string? Value { get; init; }
        public string? Display { get; init; }
    }

    internal sealed class DebugThreadInfo
    {
        public int Id { get; init; }
        public uint Pc { get; init; }
        public int Status { get; init; }
        public int WaitType { get; init; }
    }

    internal sealed class DebugBacktraceFrame
    {
        public uint Entry { get; init; }
        public uint Pc { get; init; }
        public uint Sp { get; init; }
        public int StackSize { get; init; }
        public string? Disasm { get; init; }
    }
}
