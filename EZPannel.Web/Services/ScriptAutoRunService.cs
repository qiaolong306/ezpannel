using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using EZPannel.Api.Data;
using EZPannel.Api.Models;
using EZPannel.Api.Services;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System;
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
    private readonly AgentFileService _agentFileService;
    private readonly AgentService _agentService;

    public ScriptAutoRunService(ILogger<ScriptAutoRunService> logger, ISchedulerFactory schedulerFactory, AppDbContext context,
        AgentFileService agentFileService, AgentService agentService) {
        _logger = logger;
        _schedulerFactory = schedulerFactory;
        _context = context;
        _agentFileService = agentFileService;
        _agentService = agentService;
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
        string content = script.Content.Replace("\r", "\n");

        // Determine script file name and path
        string fileName = $"ez_script_{script.Id}.{script.Type}";
        string remotePath = $"ez-script-tmp/{fileName}";

        // 通过 Agent 的文件接口创建目录并上传脚本，替代原有 SFTP
        await _agentFileService.CreateDirectoryAsync(server, "ez-script-tmp");
        using (var stream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(content))) {
            await _agentFileService.UploadFileAsync(server, remotePath, stream);
        }

        // Execute script
        string command;
        if (server.OperatingSystem == "Windows") {
            // Choose execution method based on script type
            switch (script.Type) {
                case "ps1":
                    command = $"powershell.exe -ExecutionPolicy Bypass -File {remotePath}";
                    break;
                default:
                    command = $"cmd.exe /c {remotePath}";
                    break;
            }
        }
        else {
            // Choose execution method based on script type（附带执行权限）
            switch (script.Type) {
                case "sh":
                    command = $"chmod +x {remotePath} && bash {remotePath}";
                    break;
                case "py":
                    command = $"python3 {remotePath}";
                    break;
                default:
                    command = $"chmod +x {remotePath} && {remotePath}";
                    break;
            }
        }

        var (success, output) = await _agentService.ExecuteCommandAsync(server, command);
        _logger.LogInformation($"Script execution result on server {server.Name} (Success: {success}): {output}");
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

    // 判断是否到了执行时间
    private bool IsTimeToRun(ScriptAutoRunModel autoRun) {
        if (!autoRun.IsAutoRun || string.IsNullOrWhiteSpace(autoRun.ScheduleType)) {
            _logger.LogDebug($"任务{autoRun.Name}(ID:{autoRun.Id})：未开启或未配置调度方式，无需执行");
            return false;
        }

        try {
            var now = DateTime.Now;
            var minuteStart = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0);
            var minuteEnd = minuteStart.AddMinutes(1);

            // 计算“当前这一分钟”内应当触发的下一次调度时间
            var scheduled = GetNextOccurrence(autoRun, minuteStart.AddSeconds(-1));
            if (!scheduled.HasValue) {
                return false;
            }

            // 调度时间必须落在当前这一分钟内
            if (scheduled.Value < minuteStart || scheduled.Value >= minuteEnd) {
                return false;
            }

            // 去重：避免同一分钟内重复执行
            if (autoRun.LastRunTime != DateTime.MinValue && autoRun.LastRunTime >= minuteStart) {
                return false;
            }

            return true;
        }
        catch (Exception ex) {
            _logger.LogError(ex, $"任务{autoRun.Name}调度计算异常，调度类型：{autoRun.ScheduleType}");
            return false;
        }
    }

    // 计算 afterTime 之后最近一次应当执行的时间（本地时间，精确到分钟）
    private DateTime? GetNextOccurrence(ScriptAutoRunModel autoRun, DateTime afterTime) {
        var runTime = ParseRunTime(autoRun.RunTime);
        if (!runTime.HasValue) {
            return null;
        }

        switch (autoRun.ScheduleType) {
            case "Daily":
                return GetDailyOccurrence(afterTime, runTime.Value);
            case "Monthly":
                return GetMonthlyOccurrence(afterTime, runTime.Value, autoRun.DayOfMonth);
            case "Yearly":
                return GetYearlyOccurrence(afterTime, runTime.Value, autoRun.Month, autoRun.DayOfMonth);
            case "Once":
                return GetOnceOccurrence(afterTime, runTime.Value, autoRun.RunDate);
            default:
                return null;
        }
    }

    private TimeSpan? ParseRunTime(string? runTime) {
        if (string.IsNullOrWhiteSpace(runTime)) {
            return null;
        }
        return TimeSpan.TryParse(runTime, CultureInfo.InvariantCulture, out var ts) ? ts : (TimeSpan?)null;
    }

    // 按日：每天在指定时间执行
    private DateTime GetDailyOccurrence(DateTime afterTime, TimeSpan runTime) {
        var candidate = afterTime.Date.Add(runTime);
        if (candidate <= afterTime) {
            candidate = candidate.AddDays(1);
        }
        return candidate;
    }

    // 按月：每月第 dayOfMonth 天在指定时间执行（自动处理2月30日这类无效日期）
    private DateTime? GetMonthlyOccurrence(DateTime afterTime, TimeSpan runTime, int? dayOfMonth) {
        if (!dayOfMonth.HasValue || dayOfMonth.Value < 1 || dayOfMonth.Value > 31) {
            return null;
        }

        var candidate = BuildDateInMonth(afterTime.Year, afterTime.Month, dayOfMonth.Value, runTime);
        if (candidate <= afterTime) {
            var nextMonth = afterTime.AddMonths(1);
            candidate = BuildDateInMonth(nextMonth.Year, nextMonth.Month, dayOfMonth.Value, runTime);
        }
        return candidate;
    }

    // 按年：每年第 month 月第 dayOfMonth 天在指定时间执行
    private DateTime? GetYearlyOccurrence(DateTime afterTime, TimeSpan runTime, int? month, int? dayOfMonth) {
        if (!month.HasValue || month.Value < 1 || month.Value > 12 ||
            !dayOfMonth.HasValue || dayOfMonth.Value < 1 || dayOfMonth.Value > 31) {
            return null;
        }

        var candidate = BuildDateInMonth(afterTime.Year, month.Value, dayOfMonth.Value, runTime);
        if (candidate <= afterTime) {
            candidate = BuildDateInMonth(afterTime.Year + 1, month.Value, dayOfMonth.Value, runTime);
        }
        return candidate;
    }

    // 按日期：仅在指定日期执行一次
    private DateTime? GetOnceOccurrence(DateTime afterTime, TimeSpan runTime, DateTime? runDate) {
        if (!runDate.HasValue) {
            return null;
        }
        var candidate = runDate.Value.Date.Add(runTime);
        return candidate > afterTime ? candidate : (DateTime?)null;
    }

    // 构造某年某月某天的执行时间（超出当月天数的自动截取到月末）
    private DateTime BuildDateInMonth(int year, int month, int dayOfMonth, TimeSpan runTime) {
        var maxDay = DateTime.DaysInMonth(year, month);
        var day = Math.Min(dayOfMonth, maxDay);
        return new DateTime(year, month, day).Add(runTime);
    }


}