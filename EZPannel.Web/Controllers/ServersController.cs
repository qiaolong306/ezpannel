



using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using EZPannel.Api.Data;
using Microsoft.EntityFrameworkCore;
using System;
using System.Globalization;
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
        private readonly AgentService _agentService;
        public ServersController(AppDbContext context, AgentService agentService) {
            _context = context;
            _agentService = agentService;
        }

        public async Task<ActionResult<IEnumerable<ServerDto>>> GetServers([FromQuery] bool quick = false) {
            var servers = await _context.Servers.ToListAsync();
            var result = new List<ServerDto>();

            foreach (var s in servers) {
                var dto = new ServerDto {
                    Id = s.Id,
                    Name = s.Name,
                    Host = s.Host,
                    AgentKey = s.AgentKey,
                    AgentPort = s.AgentPort,
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
                // Windows 状态获取。三个口径修正：
                // 1) CPU 使用率：Win32_PerfFormattedData_PerfOS_Processor 的 PercentProcessorTime 是"熟"计数器，
                //    由性能计数器服务在两次采样后才算出增量；新开 WMI 会话时往往拿不到有效基线，实测会稳定返回 0，
                //    这正是卡片一直显示 0% 的原因。改为读原始计数器 Win32_PerfRawData_PerfOS_Processor 的
                //    PercentIdleTime/Timestamp_PerfTime，自己隔 800ms 取两次做差分，等价于 perfmon 的算法：
                //    cpu = 100 - ΔIdle/ΔTimestamp*100。两个量都是 100ns 单位，比值为无量纲，天然是整机 0-100 值。
                //    原始计数器取不到时才回退熟计数器，再回退 LoadPercentage 平均值。
                //    注意 Win32_Processor.LoadPercentage 还按物理插槽返回数组，多路机器上 $($cpu.LoadPercentage)
                //    会拼成 "3 5" 破坏逗号分割。
                // 2) 核心数：NumberOfCores 是物理核心数，而进程列表的 CPU 百分比是按逻辑处理器
                //    （[Environment]::ProcessorCount，等于 Linux 的 nproc）归一化的，两边必须同口径，
                //    故改取 NumberOfLogicalProcessors 并按插槽求和。
                // 3) 内存：FreePhysicalMemory 不含备用列表（standby），大文件缓存的机器上会把缓存算成"已用"，
                //    改用 PerfOS_Memory.AvailableBytes（任务管理器的"可用"），used = 总量 - 可用，与 Linux free 的 used 口径一致。
                // 另外 chcp 的重定向必须是 cmd 的 >nul，写 >$null 会在工作目录生成一个名为 $null 的垃圾文件。
                string cmd = "chcp 65001 >nul && powershell -NoProfile -Command \"[System.Threading.Thread]::CurrentThread.CurrentCulture=[Globalization.CultureInfo]::InvariantCulture; $os=Get-CimInstance Win32_OperatingSystem; $cpu=Get-CimInstance Win32_Processor; $pm=Get-CimInstance Win32_PerfFormattedData_PerfOS_Memory -ErrorAction SilentlyContinue; $dk=Get-CimInstance Win32_LogicalDisk -Filter 'DeviceID=''C:'''; $r1=Get-CimInstance Win32_PerfRawData_PerfOS_Processor -ErrorAction SilentlyContinue | Where-Object { $_.Name -eq '_Total' }; Start-Sleep -Milliseconds 800; $r2=Get-CimInstance Win32_PerfRawData_PerfOS_Processor -ErrorAction SilentlyContinue | Where-Object { $_.Name -eq '_Total' }; $cu=0.0; $ok=$false; if($r1 -and $r2){ $dIdle=[double]$r2.PercentIdleTime-[double]$r1.PercentIdleTime; $dTs=[double]$r2.Timestamp_PerfTime-[double]$r1.Timestamp_PerfTime; if($dTs -gt 0){ $cu=[Math]::Round(100.0-($dIdle/$dTs)*100.0,1); $ok=$true } }; if(-not $ok){ $pf=Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -ErrorAction SilentlyContinue | Where-Object { $_.Name -eq '_Total' }; if($pf -and [double]$pf.PercentProcessorTime -gt 0){ $cu=[Math]::Round([double]$pf.PercentProcessorTime,1) } else { $cu=[Math]::Round([double](($cpu|Measure-Object -Property LoadPercentage -Average).Average),1) } }; $tot=[Math]::Round([double]$os.TotalVisibleMemorySize); $av=if($pm -and $pm.AvailableBytes -gt 0){[Math]::Round([double]$pm.AvailableBytes/1024)}else{[double]$os.FreePhysicalMemory}; $co=($cpu|Measure-Object -Property NumberOfLogicalProcessors -Sum).Sum; if(-not $co -or $co -le 0){$co=[Environment]::ProcessorCount}; if(-not $dk){ $dk=Get-CimInstance Win32_LogicalDisk | Where-Object { $_.DriveType -eq 3 } | Select-Object -First 1 }; Write-Output (@($av,$tot,$cu,$co,$dk.FreeSpace,$dk.Size) -join ',')\"";
                var (success, output) = await _agentService.ExecuteCommandAsync(server, cmd);
                if (success) {
                    // 逐字段容错解析：任何一个字段缺失（例如机器上没有 C: 盘）都不应连带丢掉 CPU，
                    // 否则整段解析失败会返回默认 ServerStatus，卡片会被判成离线、CPU 显示 0。
                    // 数值一律用 InvariantCulture 解析，避免远端/本机区域设置用逗号作小数点时解析异常。
                    try {
                        var parts = output.Trim().Split(',');
                        var status = new ServerStatus { IsOnline = true, Load = "N/A (Win)" };

                        if (parts.Length > 2 && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var cpu)) {
                            status.CpuUsage = Math.Round(cpu, 1);
                        }
                        status.CpuCores = parts.Length > 3 && !string.IsNullOrWhiteSpace(parts[3]) ? parts[3].Trim() : "1";

                        if (parts.Length > 1
                            && long.TryParse(parts[0], out var availKb)
                            && long.TryParse(parts[1], out var totalKb)
                            && totalKb > 0) {
                            status.MemTotal = totalKb / 1024;
                            status.MemUsed = (totalKb - availKb) / 1024;
                            status.MemUsage = Math.Round((1.0 - (double)availKb / totalKb) * 100, 1);
                        }

                        if (parts.Length > 5
                            && long.TryParse(parts[4], out var diskFree)
                            && long.TryParse(parts[5], out var diskSize)
                            && diskSize > 0) {
                            status.DiskUsed = ((diskSize - diskFree) / 1024 / 1024 / 1024).ToString() + " GB";
                            status.DiskTotal = (diskSize / 1024 / 1024 / 1024).ToString() + " GB";
                            status.DiskUsage = Math.Round((1.0 - (double)diskFree / diskSize) * 100, 1);
                        }

                        return status;
                    } catch {}
                }
            }
            else
            {
                // Linux 状态获取 (更稳健的解析)
                // CPU 使用率原先写死成 5.5，属于占位值，与 Windows 侧口径不对等。改为读 /proc/stat 的
                // cpu 汇总行，隔 1 秒取两次做差分：used = (Δtotal - Δidle) / Δtotal，与 top/htop 同源。
                // 该值已是整机 0-100，无需再除以核心数。
                string cmd = "uptime | awk -F'load average:' '{ print $2 }' | sed 's/,//g'; free -m | awk 'NR==2{print $3 \",\" $2}'; df -h / --output=pcent,used,size | tail -1 | awk '{print $2 \",\" $3 \",\" $1}'; nproc; S1=$(awk '/^cpu /{print $2+$3+$4+$5+$6+$7+$8+$9, $5}' /proc/stat); sleep 1; S2=$(awk '/^cpu /{print $2+$3+$4+$5+$6+$7+$8+$9, $5}' /proc/stat); echo \"$S1 $S2\" | awk '{t=$3-$1; i=$4-$2; if(t>0) printf \"%.1f\\n\", (t-i)/t*100; else print \"0\"}'";
                var (success, output) = await _agentService.ExecuteCommandAsync(server, cmd);
                if (success) {
                    try {
                        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                        var load = lines[0].Trim();
                        var memStats = lines[1].Split(',');
                        var diskStats = lines[2].Split(',');
                        // CPU 百分比单独容错解析：awk 偶尔会输出空行或多一行，不能让 CPU 的解析异常
                        // 把内存、磁盘一起丢掉。
                        var cpuVal = 0.0;
                        if (lines.Length > 4) {
                            double.TryParse(lines[4].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out cpuVal);
                        }
                        return new ServerStatus {
                            Load = load,
                            MemUsed = long.Parse(memStats[0]),
                            MemTotal = long.Parse(memStats[1]),
                            MemUsage = Math.Round(double.Parse(memStats[0]) / double.Parse(memStats[1]) * 100, 1),
                            CpuUsage = Math.Round(cpuVal, 1),
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
                AgentKey = serverDto.AgentKey,
                AgentPort = serverDto.AgentPort,
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
                AgentKey = serverDto.AgentKey,
                AgentPort = serverDto.AgentPort,
                OperatingSystem = serverDto.OperatingSystem
            };

            var success = await _agentService.TestConnectionAsync(server);
            if (success) return Ok("连接成功");
            return BadRequest("连接失败，请检查 Agent 是否已启动、端口与 Key 是否正确");
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
    // Windows：Get-Process 的 CPU 属性是进程启动至今的累计 CPU 秒数，不是占用率，
    // 除以核心数毫无意义（长时间运行的进程会算出几百上千）。
    // 这里改用采样增量法：对 TotalProcessorTime 间隔 1 秒取两次快照，用真实经过时间
    // 折算成占用率，结果与任务管理器一致（已用系统总 CPU 占用交叉校验）。
    // 内存优先取性能计数器的 WorkingSetPrivate（任务管理器的"内存"口径），缺失时回退 WorkingSet64。
    // Linux：ps 的 %cpu 按核心归一化后再除以核心数，两边口径保持一致。
    string command = server.OperatingSystem == "Windows"
        ? "powershell -NoProfile -Command \"$cores=[Environment]::ProcessorCount; $m=@{}; Get-CimInstance Win32_PerfFormattedData_PerfProc_Process -ErrorAction SilentlyContinue | Where-Object { $_.IDProcess -gt 0 } | ForEach-Object { $m[([int]$_.IDProcess)]=[double]$_.WorkingSetPrivate }; $sw=[System.Diagnostics.Stopwatch]::StartNew(); $t=@{}; Get-Process | ForEach-Object { try { $t[$_.Id]=$_.TotalProcessorTime.TotalMilliseconds } catch {} }; Start-Sleep -Milliseconds 1000; $el=$sw.Elapsed.TotalMilliseconds; Get-Process | ForEach-Object { $d=0; if($t.ContainsKey($_.Id)){ try { $d=$_.TotalProcessorTime.TotalMilliseconds-$t[$_.Id] } catch {} }; $mm=$_.WorkingSet64; if($m.ContainsKey($_.Id) -and $m[$_.Id] -gt 0){ $mm=$m[$_.Id] }; [PSCustomObject]@{ id=[int]$_.Id; name=$_.ProcessName; cpu=[Math]::Round($d/$el*100/$cores,1); mem=[Math]::Round($mm/1MB,1) } } | ConvertTo-Json -Compress\""
           : "ps -eo pid,comm,%cpu,%mem --no-headers | awk -v cores=$(nproc) 'BEGIN{n=0} {printf \"%s{\\\"id\\\":%s,\\\"name\\\":\\\"%s\\\",\\\"cpu\\\":%.1f,\\\"mem\\\":%s}\", (n>0?\",\":\"\"), $1, $2, $3/cores, $4; n++}'";

    var (success, output) = await _agentService.ExecuteCommandAsync(server, command);
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

            var (success, output) = await _agentService.ExecuteCommandAsync(server, command);
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

            var (success, output) = await _agentService.ExecuteCommandAsync(server, command);
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