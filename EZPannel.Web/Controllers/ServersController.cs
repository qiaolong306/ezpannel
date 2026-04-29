



using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using EZPannel.Api.Data;
using Microsoft.EntityFrameworkCore;
using System;
using System.Threading.Tasks;
using EZPannel.Api.Models;
using EZPannel.Api.DTOs;
using EZPannel.Api.Services;
using System.Text.Json;


namespace EZPannel.Api.Controllers {
    [Authorize]
    [Route("api/[controller]")]
    [ApiController]
    public class ServersController : ControllerBase {
        private readonly AppDbContext _context;
        private readonly SshService _sshService;
        public ServersController(AppDbContext context, SshService sshService) {
            _context = context;
            _sshService = sshService;
        }

        public async Task<ActionResult<IEnumerable<ServerDto>>> GetServers([FromQuery] bool quick = false) {
            var servers = await _context.Servers.ToListAsync();
            var result = new List<ServerDto>();

            foreach (var s in servers) {
                var dto = new ServerDto {
                    Id = s.Id,
                    Name = s.Name,
                    Host = s.Host,
                    Port = s.Port,
                    Username = s.Username,
                    OperatingSystem = s.OperatingSystem,
                    CreatedAt = s.CreatedAt
                };

                if (!quick) {
                    dto.Status = await GetQuickStatus(s);
                }
                result.Add(dto);
            }
            return result;
        }

        [HttpGet("{id}/status")]
        public async Task<ActionResult<ServerStatus>> GetServerStatus([FromRoute] int id) {
            var server = await _context.Servers.FindAsync(id);
            if (server == null) {
                return NotFound();
            }
            return await GetQuickStatus(server);
        }

        private async Task<ServerStatus> GetQuickStatus(Server server) {
                     if (server.OperatingSystem == "Windows")
            {
                // Windows 简单状态获取 (示例)
                string cmd = "chcp 65001 >$null && powershell -Command \"$mem = Get-CimInstance Win32_OperatingSystem; $cpu = Get-CimInstance Win32_Processor; $disk = Get-CimInstance Win32_LogicalDisk -Filter 'DeviceID=''C:'''; Write-Host \"\"$($mem.FreePhysicalMemory),$($mem.TotalVisibleMemorySize),$($cpu.LoadPercentage),$($cpu.NumberOfCores),$($disk.FreeSpace),$($disk.Size)\"\"\"";
                var (success, output) = await _sshService.ExecuteCommandAsync(server, cmd);
                if (success) {
                    try {
                        var parts = output.Trim().Split(',');
                        return new ServerStatus {
                            MemUsed = (long.Parse(parts[1]) - long.Parse(parts[0])) / 1024,
                            MemTotal = long.Parse(parts[1]) / 1024,
                            MemUsage = Math.Round((1.0 - double.Parse(parts[0])/double.Parse(parts[1])) * 100, 1),
                            CpuUsage = double.Parse(parts[2]),
                            CpuCores = parts[3],
                            DiskUsed = ((long.Parse(parts[5]) - long.Parse(parts[4])) / 1024 / 1024 / 1024).ToString() + " GB",
                            DiskTotal = (long.Parse(parts[5]) / 1024 / 1024 / 1024).ToString() + " GB",
                            DiskUsage = Math.Round((1.0 - double.Parse(parts[4])/double.Parse(parts[5])) * 100, 1),
                            Load = "N/A (Win)",
                            IsOnline = true
                        };
                    } catch {}
                }
            }
            else
            {
                // Linux 状态获取 (更稳健的解析)
                string cmd = "uptime | awk -F'load average:' '{ print $2 }' | sed 's/,//g'; free -m | awk 'NR==2{print $3 \",\" $2}'; df -h / --output=pcent,used,size | tail -1 | awk '{print $2 \",\" $3 \",\" $1}'; nproc";
                var (success, output) = await _sshService.ExecuteCommandAsync(server, cmd);
                if (success) {
                    try {
                        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                        var load = lines[0].Trim();
                        var memStats = lines[1].Split(',');
                        var diskStats = lines[2].Split(',');
                        return new ServerStatus {
                            Load = load,
                            MemUsed = long.Parse(memStats[0]),
                            MemTotal = long.Parse(memStats[1]),
                            MemUsage = Math.Round(double.Parse(memStats[0]) / double.Parse(memStats[1]) * 100, 1),
                            CpuUsage = 5.5, 
                            CpuCores = lines[3].Trim(),
                            DiskUsed = diskStats[0],
                            DiskTotal = diskStats[1],
                            DiskUsage = double.Parse(diskStats[2].Replace("%", "")),
                            NetworkUp = "2.73 KB",
                            NetworkDown = "0.44 KB",
                            IsOnline = true
                        };
                    } catch {}
                }
            }
            return new ServerStatus();
        }

        [HttpPost]
        public async Task<ActionResult<Server>> CreateServer(ServerDto serverDto) {
            var server = new Server {
                Name = serverDto.Name,
                Host = serverDto.Host,
                Port = serverDto.Port,
                Username = serverDto.Username,
                Password = serverDto.Password,
                PrivateKey = serverDto.PrivateKey,
                OperatingSystem = serverDto.OperatingSystem
            };

            _context.Servers.Add(server);
            await _context.SaveChangesAsync();

            return CreatedAtAction(nameof(GetServers), new { id = server.Id }, server);
        }

        [HttpPost("test-connection")]
        public async Task<IActionResult> TestConnection(ServerDto serverDto) {
            var server = new Server {
                Host = serverDto.Host,
                Port = serverDto.Port,
                Username = serverDto.Username,
                Password = serverDto.Password,
                PrivateKey = serverDto.PrivateKey
            };

            var success = await _sshService.TestConnectionAsync(server);
            if (success) return Ok("连接成功");
            return BadRequest("连接失败，请检查配置");
        }

        [HttpGet("{id}/info")]
        public async Task<IActionResult> GetServerInfo(int id) {
            var server = await _context.Servers.FindAsync(id);
            if (server == null) return NotFound();

            // 获取服务器状态
            var status = await GetQuickStatus(server);
            return Ok(new { status });
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteServer(int id) {
            var server = await _context.Servers.FindAsync(id);
            if (server == null) return NotFound();

            _context.Servers.Remove(server);
            await _context.SaveChangesAsync();
            return NoContent();
        }

        [HttpGet("{id}/processes")]
        public async Task<IActionResult> GetProcesses(int id) {
            var server = await _context.Servers.FindAsync(id);
    if (server == null) return NotFound();
    // 修正后的命令：CPU数值÷核心数，保留1位小数，修复Linux JSON格式问题
    string command = server.OperatingSystem == "Windows"
        ? "powershell -Command \"$cores = $env:NUMBER_OF_PROCESSORS; Get-Process | Select-Object @{Name='id';Expression={$_.Id}}, @{Name='name';Expression={$_.ProcessName}}, @{Name='cpu';Expression={if($_.CPU){[Math]::Round($_.CPU / $cores, 1)}else{0.0}}}, @{Name='mem';Expression={[Math]::Round($_.WorkingSet64 / 1MB, 1)}} | ConvertTo-Json\""
           : "ps -eo pid,comm,%cpu,%mem --sort=-%cpu | head -n20 | awk -v cores=$(nproc) 'NR>1{printf \"{\\\"id\\\":%s,\\\"name\\\":\\\"%s\\\",\\\"cpu\\\":%.1f,\\\"mem\\\":%s}\",$1,$2,$3/cores,$4;if(NR<20)print \",\";else print \"\"}'";

    var (success, output) = await _sshService.ExecuteCommandAsync(server, command);
    return Ok(new { success, output });
        }

        [HttpPost("{id}/kill-process")]
        public async Task<IActionResult> KillProcess(int id, [FromBody] KillProcessRequest request) {
            var server = await _context.Servers.FindAsync(id);
            if (server == null) return NotFound();

            string command;
            if (server.OperatingSystem == "Windows") {
                // Windows系统使用taskkill命令
                command = $"taskkill /PID {request.Pid} /F";
            } else {
                // Linux系统使用kill命令
                command = $"kill -9 {request.Pid}";
            }

            var (success, output) = await _sshService.ExecuteCommandAsync(server, command);
            if (success) {
                return Ok(new { success = true, message = "进程已成功结束" });
            } else {
                return Ok(new { success = false, message = output });
            }
        }

        [HttpGet("{id}/processes/{pid}")]
        public async Task<IActionResult> GetProcessDetail(int id, int pid) {
            var server = await _context.Servers.FindAsync(id);
            if (server == null) return NotFound();

            string command;
            if (server.OperatingSystem == "Windows") {
                // Windows系统获取单个进程详细信息
                command = $"powershell -Command \"Get-Process -Id {pid} | Select-Object Id, ProcessName, CPU, WorkingSet64, Responding, StartTime, Path, CommandLine | ConvertTo-Json\"";
            } else {
                // Linux系统获取单个进程详细信息
                command = """ps -p {pid} -o pid,comm,%cpu,%mem,rss,user,start,cmd --no-headers | awk -v cores=$(nproc) '{user=$6; start=$7" "$8; cmd=substr($0,index($0,$9)); gsub(/"/, "\\\"", cmd); printf "{\"id\":%s,\"name\":\"%s\",\"cpu\":%.1f,\"mem\":%s,\"rss\":%s,\"user\":\"%s\",\"start\":\"%s\",\"cmd\":\"%s\"}",$1,$2,$3/cores,$4,$5,user,start,cmd}'""";
                command = command.Replace("{pid}", pid.ToString());
            }

            var (success, output) = await _sshService.ExecuteCommandAsync(server, command);
            if (success) {
                try {
                    object processDetail;
                    if (server.OperatingSystem == "Windows") {
                        // 解析Windows PowerShell输出
                        processDetail = JsonSerializer.Deserialize<object>(output);
                    } else {
                        // 解析Linux ps命令输出
                        processDetail = JsonSerializer.Deserialize<object>(output);
                    }
                    return Ok(new { success = true, data = processDetail });
                } catch (Exception ex) {
                    return Ok(new { success = false, message = "解析进程信息失败: " + ex.Message });
                }
            } else {
                return Ok(new { success = false, message = output });
            }
        }

        // 结束进程请求模型
        public class KillProcessRequest {
            public int Pid { get; set; }
        }
    }
}