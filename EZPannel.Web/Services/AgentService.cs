using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EZPannel.Api.Models;

namespace EZPannel.Api.Services
{
    /// <summary>
    /// 通过 HTTP / WebSocket 调用目标机器上的 EZPannelClient Agent，替代 SSH。
    /// - 一次性命令执行：POST /api/execute
    /// - 交互式终端    ：WS   /ws?key=...
    /// </summary>
    public class AgentService
    {
        private readonly IHttpClientFactory _httpClientFactory;

        public AgentService(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }

        private static string BaseUrl(Server server) => $"http://{server.Host}:{server.AgentPort}";

        private static void ApplyKey(HttpRequestMessage request, Server server)
        {
            if (!string.IsNullOrEmpty(server.AgentKey))
            {
                request.Headers.TryAddWithoutValidation("X-Auth-Key", server.AgentKey);
            }
        }

        public async Task<(bool Success, string Output)> ExecuteCommandAsync(Server server, string command)
        {
            try
            {
                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromMinutes(5);

                using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl(server)}/api/execute");
                ApplyKey(request, server);
                request.Content = new StringContent(
                    JsonSerializer.Serialize(new { command = command }),
                    Encoding.UTF8,
                    "application/json");

                using var response = await client.SendAsync(request);
                var text = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    return (false, text);
                }

                var result = JsonSerializer.Deserialize<AgentExecResult>(text);
                if (result == null)
                {
                    return (false, "Agent 返回了空响应");
                }

                var output = string.IsNullOrEmpty(result.Stderr)
                    ? (result.Stdout ?? "")
                    : $"{result.Stdout}{result.Stderr}";
                return (result.Success, output);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        public async Task<bool> TestConnectionAsync(Server server)
        {
            try
            {
                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(10);

                using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl(server)}/health");
                ApplyKey(request, server);
                using var response = await client.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public async Task<ClientWebSocket> ConnectTerminalAsync(Server server, int cols = 0, int rows = 0)
        {
            var key = Uri.EscapeDataString(server.AgentKey ?? "");
            var query = $"ws://{server.Host}:{server.AgentPort}/ws?key={key}";
            if (cols > 0 && rows > 0)
            {
                query += $"&cols={cols}&rows={rows}";
            }
            var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri(query), CancellationToken.None);
            return ws;
        }

        private class AgentExecResult
        {
            [JsonPropertyName("success")] public bool Success { get; set; }
            [JsonPropertyName("exit_code")] public int? ExitCode { get; set; }
            [JsonPropertyName("stdout")] public string? Stdout { get; set; }
            [JsonPropertyName("stderr")] public string? Stderr { get; set; }
            [JsonPropertyName("timed_out")] public bool TimedOut { get; set; }
        }
    }
}