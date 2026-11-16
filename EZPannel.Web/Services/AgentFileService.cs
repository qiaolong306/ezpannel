using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using EZPannel.Api.DTOs;
using EZPannel.Api.Models;

namespace EZPannel.Api.Services
{
    /// <summary>
    /// 文件管理服务：通过目标机器上的 EZPannelClient Agent 的 HTTP 文件接口转发（替代 SFTP）。
    /// - 列目录   GET  /api/files/list?path=...
    /// - 上传     POST /api/files/upload?path=...
    /// - 下载     GET  /api/files/download?path=...
    /// - 读内容   GET  /api/files/content?path=...
    /// - 保存文本 POST /api/files/save
    /// - 新建目录 POST /api/files/mkdir
    /// - 删除     POST /api/files/delete
    /// </summary>
    public class AgentFileService
    {
        private readonly IHttpClientFactory _httpClientFactory;

        public AgentFileService(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }

        private static string BaseUrl(Server server) => $"http://{server.Host}:{server.AgentPort}";

        private HttpClient CreateClient(Server server)
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(5);
            if (!string.IsNullOrEmpty(server.AgentKey))
                client.DefaultRequestHeaders.TryAddWithoutValidation("X-Auth-Key", server.AgentKey);
            return client;
        }

        private static string Enc(string path) => Uri.EscapeDataString(path ?? "/");

        private async Task PostJsonAsync(Server server, string url, object payload)
        {
            using var client = CreateClient(server);
            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await client.PostAsync(url, content);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                throw new Exception($"Agent 返回错误 {(int)resp.StatusCode}: {body}");
            }
        }

        public async Task<List<FileItemDto>> ListFilesAsync(Server server, string path)
        {
            using var client = CreateClient(server);
            var url = $"{BaseUrl(server)}/api/files/list?path={Enc(path)}";
            using var resp = await client.GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                throw new Exception($"Agent 返回错误 {(int)resp.StatusCode}: {body}");

            using var doc = JsonSerializer.Deserialize<JsonDocument>(body);
            var items = new List<FileItemDto>();
            if (doc.RootElement.TryGetProperty("items", out var arr))
            {
                foreach (var it in arr.EnumerateArray())
                {
                    var item = new FileItemDto
                    {
                        Name = it.GetProperty("name").GetString(),
                        Path = it.GetProperty("path").GetString(),
                        Size = it.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0,
                        IsDirectory = it.GetProperty("isDirectory").GetBoolean(),
                        Permissions = it.TryGetProperty("permissions", out var pm) ? pm.GetString() : "",
                    };
                    if (it.TryGetProperty("lastModified", out var lm))
                    {
                        try { item.LastModified = lm.GetDateTime(); }
                        catch { item.LastModified = DateTime.MinValue; }
                    }
                    items.Add(item);
                }
            }
            return items;
        }

        public async Task UploadFileAsync(Server server, string remotePath, Stream localStream)
        {
            using var client = CreateClient(server);
            var url = $"{BaseUrl(server)}/api/files/upload?path={Enc(remotePath)}";
            using var ms = new MemoryStream();
            await localStream.CopyToAsync(ms);
            using var content = new ByteArrayContent(ms.ToArray());
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var resp = await client.PostAsync(url, content);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                throw new Exception($"Agent 返回错误 {(int)resp.StatusCode}: {body}");
            }
        }

        public async Task<byte[]> DownloadFileAsync(Server server, string remotePath)
        {
            using var client = CreateClient(server);
            var url = $"{BaseUrl(server)}/api/files/download?path={Enc(remotePath)}";
            using var resp = await client.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                throw new Exception($"Agent 返回错误 {(int)resp.StatusCode}: {body}");
            }
            return await resp.Content.ReadAsByteArrayAsync();
        }

        public async Task<string> GetFileContentAsync(Server server, string remotePath)
        {
            using var client = CreateClient(server);
            var url = $"{BaseUrl(server)}/api/files/content?path={Enc(remotePath)}";
            using var resp = await client.GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                return string.Empty;
            using var doc = JsonSerializer.Deserialize<JsonDocument>(body);
            return doc.RootElement.TryGetProperty("content", out var c) ? c.GetString() : string.Empty;
        }

        public async Task SaveFileContentAsync(Server server, string remotePath, string content)
        {
            await PostJsonAsync(server, $"{BaseUrl(server)}/api/files/save",
                new { path = remotePath, content = content });
        }

        public async Task CreateDirectoryAsync(Server server, string directoryPath)
        {
            await PostJsonAsync(server, $"{BaseUrl(server)}/api/files/mkdir",
                new { path = directoryPath });
        }

        public async Task DeleteAsync(Server server, string remotePath, bool isDirectory)
        {
            await PostJsonAsync(server, $"{BaseUrl(server)}/api/files/delete",
                new { path = remotePath, isDirectory = isDirectory });
        }
    }
}