using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using EZPannel.Api.Data;
using EZPannel.Api.Models;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System;
using Renci.SshNet;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Quartz.Impl.Matchers;

public class ScriptAutoRunService {
    private readonly ILogger<ScriptAutoRunService> _logger;
    private readonly ISchedulerFactory _schedulerFactory;
    private IScheduler _scheduler;
    private readonly AppDbContext _context;

    public ScriptAutoRunService(ILogger<ScriptAutoRunService> logger, ISchedulerFactory schedulerFactory, AppDbContext context) {
        _logger = logger;
        _schedulerFactory = schedulerFactory;
        _context = context;
    }

    public async Task InitializeAutoRunJobs() {
        // Initialize Quartz scheduler
        _scheduler = await _schedulerFactory.GetScheduler();
        await _scheduler.Start();

        // Clear existing jobs and schedule new ones
        await ClearExistingJobs(_scheduler);
        await ScheduleGlobalAutoRunCheckJob(_scheduler);
    }

    // Clear all existing script auto run jobs
    private async Task ClearExistingJobs(IScheduler scheduler) {
        // Get all auto run job keys
        var jobKeys = await scheduler.GetJobKeys(GroupMatcher<JobKey>.GroupEquals("ScriptAutoRunGroup"));
        // Delete all jobs and related triggers
        if (jobKeys.Any()) {
            _logger.LogInformation($"Clearing {jobKeys.Count} existing script auto run jobs.");
            await scheduler.DeleteJobs(jobKeys.ToList());
        }
    }

    // Schedule global auto run check job
    private async Task ScheduleGlobalAutoRunCheckJob(IScheduler scheduler) {
        var jobKey = new JobKey("GlobalScriptAutoRunCheckJob", "ScriptAutoRunGroup");
        // Check if job already exists
        if (!await scheduler.CheckExists(jobKey)) {
            var job = JobBuilder.Create<GlobalScriptAutoRunCheckJob>()
                .WithIdentity(jobKey)
                .Build();

            // Create a trigger that runs every minute
            var trigger = TriggerBuilder.Create()
                .WithIdentity("GlobalScriptAutoRunCheckTrigger", "ScriptAutoRunGroup")
                .WithSimpleSchedule(x => x
                    .WithIntervalInMinutes(1)
                    .RepeatForever())
                .StartNow()
                .Build();

            await scheduler.ScheduleJob(job, trigger);
            _logger.LogInformation("全局脚本运行调度器已经启动，每1分钟运行一次。");
        }
    }


    // Execute the auto run task
    public async Task<string> ExecuteAutoRunAsync(ScriptAutoRunModel autoRun) {
        // Get script information
        var script = await _context.Scripts.FindAsync(autoRun.ScriptId);
        if (script == null) {
            _logger.LogError($"Script not found for auto run task: {autoRun.Name} (ID: {autoRun.Id})");
            return $"Script not found for auto run task: {autoRun.Name} (ID: {autoRun.Id})";
        }
        // Get target servers
        var targetServers = autoRun.ServerIds != null && autoRun.ServerIds.Count > 0
            ? await _context.Servers.Where(s => autoRun.ServerIds.Contains(s.Id)).ToListAsync()
            : await _context.Servers.ToListAsync();

        foreach (var server in targetServers) {
            try {
                // Execute script on server
                await ExecuteScriptOnServerAsync(script, server);
                // Log successful execution
                var history = new ScriptHistory {
                    ScriptId = script.Id,
                    ServerId = server.Id,
                    Executor = "System (AutoRun)",
                    Success = true,
                    ExecutedAt = DateTime.Now
                };
                _context.ScriptHistories.Add(history);
            }
            catch (Exception ex) {
                _logger.LogError(ex, $"Error executing script {script.Name} on server {server.Name} (ID: {server.Id})");

                // Log failed execution
                var history = new ScriptHistory {
                    ScriptId = script.Id,
                    ServerId = server.Id,
                    Executor = "System (AutoRun)",
                    Success = false,
                    ExecutedAt = DateTime.Now
                };
                _context.ScriptHistories.Add(history);
                return $"Error executing script {script.Name} on server {server.Name} (ID: {server.Id}, Error: {ex.Message})";
            }
        }

        // Update last execution time
        autoRun.LastRunTime = DateTime.Now;
        await _context.SaveChangesAsync();
        return string.Empty;
    }

    // Execute script on specific server
    private async Task ExecuteScriptOnServerAsync(Script script, Server server) {
        string content = script.Content;
        content = content.Replace("\r", "\n");

        // Use SSH to connect to server and execute script
        using (var client = new SshClient(server.Host, server.Port, server.Username, server.Password)) {
            client.Connect();

            // Determine script file name and path
            string fileName = $"ez_script_{script.Id}.{script.Type}";
            string remotePath;

            if (server.OperatingSystem == "Windows") {
                remotePath = $"ez-script-tmp\\{fileName}";
                // Ensure directory exists
                using (var cmd = client.CreateCommand("if not exist \".\\ez-script-tmp\" mkdir \\ez-script-tmp\"")) {
                    cmd.Execute();
                }
            }
            else {
                remotePath = $"ez-script-tmp/{fileName}";
                // Ensure directory exists
                using (var cmd = client.CreateCommand("mkdir -p ez-script-tmp")) {
                    cmd.Execute();
                }
            }

            // Upload script file
            using (var sftp = new SftpClient(server.Host, server.Port, server.Username, server.Password)) {
                sftp.Connect();
                byte[] contentBytes = System.Text.Encoding.UTF8.GetBytes(content);
                using (var stream = new System.IO.MemoryStream(contentBytes)) {
                    sftp.UploadFile(stream, remotePath, true);
                }
                sftp.Disconnect();
            }

            // Execute script
            string command;
            if (server.OperatingSystem == "Windows") {
                // Choose execution method based on script type
                switch (script.Type) {
                    case "bat":
                        command = $"cmd.exe /c {remotePath}";
                        break;
                    case "ps1":
                        command = $"powershell.exe -ExecutionPolicy Bypass -File {remotePath}";
                        break;
                    default:
                        command = $"cmd.exe /c {remotePath}";
                        break;
                }
            }
            else {
                // Add execute permission to script
                using (var cmd = client.CreateCommand($"chmod +x {remotePath}")) {
                    cmd.Execute();
                }

                // Choose execution method based on script type
                switch (script.Type) {
                    case "sh":
                        command = $"bash {remotePath}";
                        break;
                    case "py":
                        command = $"python3 {remotePath}";
                        break;
                    default:
                        command = $"{remotePath}";
                        break;
                }
            }
            // Execute script command
            using (var cmd = client.CreateCommand(command)) {
                var result = cmd.Execute();
                _logger.LogInformation($"Script execution result on server {server.Name}: {result}");
            }
            client.Disconnect();
        }
    }
}

// Global script auto run check job, runs every minute to check all auto run configurations
public class GlobalScriptAutoRunCheckJob : IJob {
    private readonly ILogger<GlobalScriptAutoRunCheckJob> _logger;
    private readonly AppDbContext _context;
    private readonly ScriptAutoRunService _scriptAutoRunService;
    public GlobalScriptAutoRunCheckJob(ILogger<GlobalScriptAutoRunCheckJob> logger, AppDbContext context, ScriptAutoRunService scriptAutoRunService) {
        _logger = logger;
        _context = context;
        _scriptAutoRunService = scriptAutoRunService;
    }

    public async Task Execute(IJobExecutionContext context) {
        // Get all auto run configurations
        var autoRuns = await _context.ScriptAutoRuns.Where(x => x.IsAutoRun).ToListAsync();
        // Check each configuration if it's time to run
        foreach (var autoRun in autoRuns) {
            try {
                if (IsTimeToRun(autoRun)) {
                    _logger.LogInformation($"Executing script auto run task: {autoRun.Name} (ID: {autoRun.Id})");
                    await _scriptAutoRunService.ExecuteAutoRunAsync(autoRun);
                }
            }
            catch (Exception ex) {
                _logger.LogError(ex, $"Error checking script auto run task: {autoRun.Name} (ID: {autoRun.Id})");
            }
        }
    }

    // Determine if it's time to run the auto run task
    private bool IsTimeToRun(ScriptAutoRunModel autoRun) {
        if (!autoRun.IsAutoRun || string.IsNullOrWhiteSpace(autoRun.CronExpression)) {
            _logger.LogDebug($"任务{autoRun.Name}(ID:{autoRun.Id})：未开启或Cron为空，无需执行");
            return false;
        }
        try {
            // 1. 实例化Quartz CronExpression（显式命名空间避免包冲突）
            var cronExpression = new Quartz.CronExpression(autoRun.CronExpression);
            // 核心1：指定固定中国东八区（生产环境推荐，脱离服务器时区依赖）
            var chinaTz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
            cronExpression.TimeZone = chinaTz;
            // 业务判断的当前本地时间（定义当前分钟范围，不变）
            var nowLocal = DateTime.Now;
            var currentMinuteStart = new DateTime(nowLocal.Year, nowLocal.Month, nowLocal.Day, nowLocal.Hour, nowLocal.Minute, 0);
            var currentMinuteEnd = currentMinuteStart.AddMinutes(1);
            // 核心2：将原有DateTimeOffset入参「标准化为纯UTC时间」（关键！确保Offset=0）
            // 假设你原有入参基准时间为baseTimeOffset，先转换为UTC
            var baseTimeOffset = new DateTimeOffset(currentMinuteStart, chinaTz.BaseUtcOffset); // 你的原始入参
            var baseTimeUtc = baseTimeOffset.ToUniversalTime(); // 强制转为纯UTC，Offset=TimeSpan.Zero
            // 核心3：调用方法（入参为纯UTC的DateTimeOffset，返回也是DateTimeOffset?）
            DateTimeOffset? nextOccurrenceOffset = cronExpression.GetNextValidTimeAfter(baseTimeUtc);
            if (!nextOccurrenceOffset.HasValue) {
                return false;
            }
            // 核心4：直接取返回值的LocalDateTime → 已自动转换为Asia/Shanghai本地时间
            DateTime nextOccurrenceLocal = nextOccurrenceOffset.Value.LocalDateTime;
            // 原有业务判断逻辑（完全不变，基于本地时间）
            bool isInCurrentMinute = nextOccurrenceLocal >= currentMinuteStart && nextOccurrenceLocal <= currentMinuteEnd;
            bool isNotRunYet = autoRun.LastRunTime == DateTime.MinValue || nextOccurrenceLocal > autoRun.LastRunTime;

            if (isInCurrentMinute && isNotRunYet) {
                return true;
            }
        }
        catch (TimeZoneNotFoundException) {
            _logger.LogError("服务器未配置中国时区（Asia/Shanghai），请检查系统时区！");
        }
        catch (Exception ex) {
            _logger.LogError(ex, $"任务{autoRun.Name}Cron计算异常，Cron：{autoRun.CronExpression}");
        }
        return false;
    }


}