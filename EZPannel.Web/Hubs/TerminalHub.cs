using Microsoft.AspNetCore.SignalR;
using Renci.SshNet;
using EZPannel.Api.Data;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;

namespace EZPannel.Api.Hubs
{
    [Microsoft.AspNetCore.Authorization.Authorize]
    public class TerminalHub : Hub
    {
        private readonly AppDbContext _context;
        private static readonly ConcurrentDictionary<string, (SshClient Client, ShellStream Stream)> _sessions = new();

        public TerminalHub(AppDbContext context)
        {
            _context = context;
        }

        public async Task Connect(int serverId)
        {
            var server = await _context.Servers.FindAsync(serverId);
            if (server == null)
            {
                await Clients.Caller.SendAsync("ReceiveData", "\r\n[Error] Server not found in database.\r\n");
                return;
            }

            try
            {
                var client = GetClient(server);
                client.Connect();
                
                var stream = client.CreateShellStream("xterm", 80, 24, 800, 600, 1024);
                _sessions[Context.ConnectionId] = (client, stream);

                var caller = Clients.Caller;

                _ = Task.Run(async () =>
                {
                    var buffer = new byte[1024];
                    try
                    {
                        while (client.IsConnected)
                        {
                            if (stream.DataAvailable)
                            {
                                var read = await stream.ReadAsync(buffer, 0, buffer.Length);
                                if (read > 0)
                                {
                                    var data = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
                                    await caller.SendAsync("ReceiveData", data);
                                }
                            }
                            else
                            {
                                await Task.Delay(50);
                            }
                        }
                    }
                    catch { }
                });

                await caller.SendAsync("ReceiveData", $"[Connected to {server.Name} ({server.Host})]\r\n");

                if (server.OperatingSystem == "Windows")
                {
                    var chcpCmd = System.Text.Encoding.UTF8.GetBytes("chcp 65001\r");
                    stream.Write(chcpCmd, 0, chcpCmd.Length);
                    stream.Flush();
                }
            }
            catch (Exception ex)
            {
                await Clients.Caller.SendAsync("ReceiveData", $"\r\n[SSH Error] {ex.Message}\r\n");
            }
        }

        public async Task SendData(string data)
        {
            if (_sessions.TryGetValue(Context.ConnectionId, out var session))
            {
                // 将字符串转为 UTF8 字节数组写入流
                var bytes = System.Text.Encoding.UTF8.GetBytes(data);
                session.Stream.Write(bytes, 0, bytes.Length);
                session.Stream.Flush();
            }
            await Task.CompletedTask;
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            if (_sessions.TryRemove(Context.ConnectionId, out var session))
            {
                session.Stream.Dispose();
                session.Client.Disconnect();
                session.Client.Dispose();
            }
            await base.OnDisconnectedAsync(exception);
        }

        private SshClient GetClient(Models.Server server)
        {
            if (!string.IsNullOrEmpty(server.Password))
            {
                return new SshClient(server.Host, server.Port, server.Username, server.Password);
            }
            if (!string.IsNullOrEmpty(server.PrivateKey))
            {
                var keyFile = new PrivateKeyFile(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(server.PrivateKey)));
                return new SshClient(server.Host, server.Port, server.Username, keyFile);
            }
            throw new Exception("Auth method required");
        }
    }
}