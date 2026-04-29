using Renci.SshNet;
using EZPannel.Api.Models;

namespace EZPannel.Api.Services
{
    public class SshService
    {
        public async Task<(bool Success, string Output)> ExecuteCommandAsync(Server server, string command)
        {
            return await Task.Run(() =>
            {
                try
                {
                    using var client = GetClient(server);
                    client.Connect();
                    var cmd = client.CreateCommand(command);
                    var result = cmd.Execute();
                    client.Disconnect();
                    return (true, result);
                }
                catch (Exception ex)
                {
                    return (false, ex.Message);
                }
            });
        }

        public async Task<bool> TestConnectionAsync(Server server)
        {
            return await Task.Run(() =>
            {
                try
                {
                    using var client = GetClient(server);
                    client.Connect();
                    var connected = client.IsConnected;
                    client.Disconnect();
                    return connected;
                }
                catch
                {
                    return false;
                }
            });
        }

        private SshClient GetClient(Server server)
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

            throw new Exception("No authentication method provided (Password or PrivateKey)");
        }
    }
}
