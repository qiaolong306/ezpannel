using Microsoft.AspNetCore.SignalR;
using EZPannel.Api.Data;
using EZPannel.Api.Services;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;

namespace EZPannel.Api.Hubs
{
    [Microsoft.AspNetCore.Authorization.Authorize]
    public class TerminalHub : Hub
    {
        private readonly AppDbContext _context;
        private readonly AgentService _agentService;
        private static readonly ConcurrentDictionary<string, ClientWebSocket> _sessions = new();

        public TerminalHub(AppDbContext context, AgentService agentService)
        {
            _context = context;
            _agentService = agentService;
        }

        public async Task Connect(int serverId, int cols = 0, int rows = 0)
        {
            var server = await _context.Servers.FindAsync(serverId);
            if (server == null)
            {
                await Clients.Caller.SendAsync("ReceiveData", "\r\n[Error] 数据库中找不到该服务器。\r\n");
                return;
            }

            try
            {
                // 同一连接重复 Connect（例如快速重开终端）时先关掉旧的 Agent 会话，
                // 否则 Agent 侧会遗留一个没人管的 shell 进程
                if (_sessions.TryRemove(Context.ConnectionId, out var stale))
                {
                    await CloseQuietlyAsync(stale);
                }

                var ws = await _agentService.ConnectTerminalAsync(server, cols, rows);
                _sessions[Context.ConnectionId] = ws;

                var caller = Clients.Caller;
                await caller.SendAsync("ReceiveData", $"[已连接 {server.Name} ({server.Host}:{server.AgentPort})]\r\n");

                // 后台任务：持续读取 Agent 输出并转发给浏览器
                _ = Task.Run(async () =>
                {
                    var buffer = new byte[4096];
                    try
                    {
                        while (ws.State == WebSocketState.Open)
                        {
                            // 按整条消息聚合后再解码：逐帧解码会把跨帧的多字节 UTF-8 字符拆坏，
                            // 终端里就会出现乱码或光标错位
                            using var ms = new MemoryStream();
                            WebSocketReceiveResult result;
                            do
                            {
                                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                                if (result.MessageType == WebSocketMessageType.Close)
                                {
                                    break;
                                }
                                if (result.MessageType == WebSocketMessageType.Text && result.Count > 0)
                                {
                                    ms.Write(buffer, 0, result.Count);
                                }
                            } while (!result.EndOfMessage);

                            if (result.MessageType == WebSocketMessageType.Close)
                            {
                                await caller.SendAsync("ReceiveData", "\r\n[连接已关闭]\r\n");
                                break;
                            }
                            if (ms.Length > 0)
                            {
                                await caller.SendAsync("ReceiveData", Encoding.UTF8.GetString(ms.ToArray()));
                            }
                        }
                    }
                    catch { }
                });
            }
            catch (Exception ex)
            {
                await Clients.Caller.SendAsync("ReceiveData", $"\r\n[Agent Error] {ex.Message}\r\n");
            }
        }

        public async Task SendData(string data)
        {
            if (_sessions.TryGetValue(Context.ConnectionId, out var ws) && ws.State == WebSocketState.Open)
            {
                var bytes = Encoding.UTF8.GetBytes(data);
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            await Task.CompletedTask;
        }

        /// <summary>
        /// 同步终端窗口尺寸到 Agent 的伪终端（Agent 侧二进制帧 = 控制指令）。
        /// </summary>
        public async Task Resize(int cols, int rows)
        {
            if (cols <= 0 || rows <= 0)
            {
                return;
            }
            if (_sessions.TryGetValue(Context.ConnectionId, out var ws) && ws.State == WebSocketState.Open)
            {
                var payload = Encoding.UTF8.GetBytes($"{{\"type\":\"resize\",\"cols\":{cols},\"rows\":{rows}}}");
                await ws.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Binary, true, CancellationToken.None);
            }
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            if (_sessions.TryRemove(Context.ConnectionId, out var ws))
            {
                await CloseQuietlyAsync(ws);
            }
            await base.OnDisconnectedAsync(exception);
        }

        /// <summary>
        /// 优雅关闭 Agent 连接；Agent 收到关闭帧后会结束对应的 shell 进程。
        /// </summary>
        private static async Task CloseQuietlyAsync(ClientWebSocket ws)
        {
            try
            {
                if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "disconnect", cts.Token);
                }
            }
            catch { }
            finally
            {
                try { ws.Dispose(); } catch { }
            }
        }
    }
}