using Renci.SshNet;
using EZPannel.Api.Models;
using EZPannel.Api.DTOs;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace EZPannel.Api.Services
{
    public class SftpService
    {
        private Renci.SshNet.ConnectionInfo GetConnectionInfo(Server server)
        {
            if (!string.IsNullOrEmpty(server.Password))
            {
                return new Renci.SshNet.ConnectionInfo(server.Host, server.Port, server.Username,
                    new PasswordAuthenticationMethod(server.Username, server.Password));
            }

            if (!string.IsNullOrEmpty(server.PrivateKey))
            {
                var keyFile = new PrivateKeyFile(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(server.PrivateKey)));
                return new Renci.SshNet.ConnectionInfo(server.Host, server.Port, server.Username,
                    new PrivateKeyAuthenticationMethod(server.Username, keyFile));
            }

            throw new System.Exception("No authentication method provided");
        }

        public async Task<List<FileItemDto>> ListFilesAsync(Server server, string path)
        {
            return await Task.Run(() =>
            {
                using var client = new SftpClient(GetConnectionInfo(server));
                client.Connect();
                var entries = client.ListDirectory(path);
                var result = entries
                    .Where(e => e.Name != "." && e.Name != "..")
                    .Select(e => new FileItemDto
                    {
                        Name = e.Name,
                        Path = e.FullName,
                        Size = e.Attributes.Size,
                        IsDirectory = e.IsDirectory,
                        LastModified = e.LastWriteTime,
                        Permissions = e.Attributes.ToString()
                    })
                    .OrderByDescending(e => e.IsDirectory)
                    .ThenBy(e => e.Name)
                    .ToList();
                client.Disconnect();
                return result;
            });
        }

        public async Task UploadFileAsync(Server server, string remotePath, Stream localStream)
        {
            await Task.Run(() =>
            {
                using var client = new SftpClient(GetConnectionInfo(server));
                client.Connect();
                client.UploadFile(localStream, remotePath);
                client.Disconnect();
            });
        }

        public async Task<byte[]> DownloadFileAsync(Server server, string remotePath)
        {
            return await Task.Run(() =>
            {
                using var client = new SftpClient(GetConnectionInfo(server));
                client.Connect();
                using var ms = new MemoryStream();
                client.DownloadFile(remotePath, ms);
                client.Disconnect();
                return ms.ToArray();
            });
        }

        public async Task<string> GetFileContentAsync(Server server, string remotePath)
        {
            return await Task.Run(() =>
            {
                try
                {
                                    using var client = new SftpClient(GetConnectionInfo(server));
                client.Connect();
                using var ms = new MemoryStream();
                client.DownloadFile(remotePath, ms);
                ms.Position = 0;
                using var reader = new StreamReader(ms, System.Text.Encoding.UTF8);
                var content = reader.ReadToEnd();
                client.Disconnect();
                return content;
                }
                catch (System.Exception)
                {
                    return string.Empty;
                }
            });
        }

        public async Task SaveFileContentAsync(Server server, string remotePath, string content)
        {
            await Task.Run(() =>
            {
                using var client = new SftpClient(GetConnectionInfo(server));
                client.Connect();
                using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
                client.UploadFile(ms, remotePath, true); // true表示覆盖已存在的文件
                client.Disconnect();
            });
        }

        public async Task CreateDirectoryAsync(Server server, string directoryPath)
        {
            await Task.Run(() =>
            {
                using var client = new SftpClient(GetConnectionInfo(server));
                client.Connect();
                client.CreateDirectory(directoryPath);
                client.Disconnect();
            });
        }

        public async Task DeleteAsync(Server server, string remotePath, bool isDirectory)
        {
            await Task.Run(() =>
            {
                using var client = new SftpClient(GetConnectionInfo(server));
                client.Connect();
                if (isDirectory)
                {
                    // 删除目录时，如果目录不为空，需要先删除内容
                    try
                    {
                        client.DeleteDirectory(remotePath);
                    }
                    catch (Renci.SshNet.Common.SftpPathNotFoundException)
                    {
                        throw new System.IO.FileNotFoundException($"路径不存在: {remotePath}");
                    }
                    catch (Renci.SshNet.Common.SftpPermissionDeniedException)
                    {
                        throw new System.UnauthorizedAccessException($"没有权限删除: {remotePath}");
                    }
                    catch
                    {
                        // 如果目录不为空，尝试递归删除目录内容
                        DeleteDirectoryRecursively(client, remotePath);
                    }
                }
                else
                {
                    client.DeleteFile(remotePath);
                }
                client.Disconnect();
            });
        }
        
        private void DeleteDirectoryRecursively(SftpClient client, string directoryPath)
        {
            var files = client.ListDirectory(directoryPath);
            foreach (var file in files.Where(f => f.Name != "." && f.Name != ".."))
            {
                if (file.IsDirectory)
                {
                    DeleteDirectoryRecursively(client, file.FullName);
                }
                else
                {
                    client.DeleteFile(file.FullName);
                }
            }
            client.DeleteDirectory(directoryPath);
        }
    }
}