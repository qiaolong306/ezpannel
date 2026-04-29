using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EZPannel.Api.Data;
using EZPannel.Api.Models;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;
using Quartz;
using Quartz.Impl.Matchers;

namespace EZPannel.Api.Services {
    public interface IDatabaseBackupService {
        Task InitializeBackupJobs();
        Task<OperationResult> ExecuteBackup(int configId);
        Task CreateBackupConfig(DatabaseBackupConfig config);
        Task UpdateBackupConfig(DatabaseBackupConfig config);
        Task DeleteBackupConfig(int id);
        Task<OperationResult> RestoreBackup(int logId);
    }

    public class OperationResult {
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    public class DatabaseBackupService : IDatabaseBackupService {
        private readonly AppDbContext _context;
        private readonly ISchedulerFactory _schedulerFactory;
        private readonly string _backupDirectory;
        private const int _expireDays = 7;

        public DatabaseBackupService(AppDbContext context, ISchedulerFactory schedulerFactory) {
            _context = context;
            _schedulerFactory = schedulerFactory;
            _backupDirectory = Path.Combine(Directory.GetCurrentDirectory(), "Backups");
            Directory.CreateDirectory(_backupDirectory);
        }

        public async Task InitializeBackupJobs() {
            var scheduler = await _schedulerFactory.GetScheduler();
            await scheduler.Start();

            // 清除所有现有的备份作业和触发器
            await ClearExistingBackupJobs(scheduler);

            // 只创建一个全局备份检查作业，每10分钟执行一次
            await ScheduleGlobalBackupCheckJob(scheduler);
        }

        // 清除所有现有的备份作业
        private async Task ClearExistingBackupJobs(IScheduler scheduler) {
            // 获取所有备份作业组
            var jobKeys = await scheduler.GetJobKeys(GroupMatcher<JobKey>.GroupEquals("BackupJobs"));

            // 删除所有作业和相关触发器
            if (jobKeys.Any()) {
                Console.WriteLine($"[{DateTime.UtcNow}] 清除 {jobKeys.Count} 个现有备份作业");
                await scheduler.DeleteJobs(jobKeys.ToList());
            }
        }

        // 调度全局备份检查作业
        private async Task ScheduleGlobalBackupCheckJob(IScheduler scheduler) {
            var jobKey = new JobKey("GlobalBackupCheckJob", "BackupJobs");

            // 检查作业是否已存在
            if (!await scheduler.CheckExists(jobKey)) {
                var job = JobBuilder.Create<GlobalBackupCheckJob>()
                    .WithIdentity(jobKey)
                    .Build();

                // 创建一个每10分钟执行一次的触发器
                var trigger = TriggerBuilder.Create()
                    .WithIdentity("GlobalBackupCheckTrigger", "BackupJobs")
                    .WithSimpleSchedule(x => x
                        .WithIntervalInMinutes(10)
                        .RepeatForever())
                    .StartNow()
                    .Build();

                await scheduler.ScheduleJob(job, trigger);
                Console.WriteLine($"[{DateTime.UtcNow}] 已创建全局备份检查作业，每10分钟执行一次");
            }
        }

        /// <summary>
        /// 清除过期备份文件夹
        /// </summary>
        private void ClearTimeoutFolders() {
            var timeout = TimeSpan.FromDays(_expireDays);
            var now = DateTime.Now;
            try {
                foreach (var folder in Directory.GetDirectories(_backupDirectory)) {
                    if (DateTime.TryParse(Path.GetFileName(folder), out var folderTime)) {
                        if (now - folderTime > timeout) {
                            Directory.Delete(folder, true);
                            Console.WriteLine($"[{now}] 已删除超时备份文件夹: {folder}");
                        }
                    }
                }
            }
            catch (Exception ex) {
                Console.WriteLine($"[{now}] 删除超时备份文件夹时出错: {ex.Message}");
            }
        }

        public async Task<OperationResult> ExecuteBackup(int configId) {
            try {
                ClearTimeoutFolders();
                var config = await _context.DatabaseBackupConfigs
                    .FirstOrDefaultAsync(c => c.Id == configId);

                if (config == null) {
                    return new OperationResult { Success = false, Message = "备份配置不存在" };
                }
                var database = await _context.Databases
                    .FirstOrDefaultAsync(d => d.Id == config.DatabaseId);

                if (database == null) {
                    return new OperationResult { Success = false, Message = "数据库连接信息不存在" };
                }

                var databases = config.DatabaseNames.Split(',', System.StringSplitOptions.RemoveEmptyEntries);

                if (databases.Length == 0) {
                    return new OperationResult { Success = false, Message = "没有指定要备份的数据库名称" };
                }

                var backupResults = new List<bool>();
                var errorMessages = new List<string>();

                foreach (var dbName in databases) {
                    var backupLog = new BackupLog {
                        DatabaseBackupConfigId = config.Id,
                        DatabaseName = dbName,
                        BackupTime = DateTime.Now,
                        IsSuccess = false
                    };

                    try {
                        var backupPath = await GenerateBackupPath(database, dbName);
                        bool success = false;

                        switch (database.Type.ToLower()) {
                            case "mysql":
                                success = await BackupMySqlDatabase(database, dbName, backupPath);
                                break;
                            case "postgresql":
                                success = await BackupPostgreSqlDatabase(database, dbName, backupPath);
                                break;
                            case "sqlserver":
                                success = await BackupSqlServerDatabase(database, dbName, backupPath);
                                break;
                            default:
                                throw new Exception($"不支持的数据库类型: {database.Type}");
                        }

                        if (success) {
                            // 压缩备份文件
                            string zipPath = await CompressFile(backupPath);

                            var fileInfo = new FileInfo(zipPath);
                            backupLog.IsSuccess = true;
                            backupLog.FilePath = zipPath;
                            backupLog.FileSize = fileInfo.Length;
                            backupLog.Message = "备份成功";
                            backupResults.Add(true);
                        }
                        else {
                            backupLog.IsSuccess = false;
                            backupLog.Message = "备份失败，但未捕获到具体错误";
                            backupResults.Add(false);
                            errorMessages.Add($"数据库 {dbName} 备份失败");
                        }
                    }
                    catch (Exception ex) {
                        backupLog.IsSuccess = false;
                        backupLog.Message = ex.Message;
                        backupResults.Add(false);
                        errorMessages.Add($"数据库 {dbName} 备份失败: {ex.Message}");
                    }
                    finally {
                        _context.BackupLogs.Add(backupLog);
                        await _context.SaveChangesAsync();
                    }
                }

                // 如果有任何备份失败，返回错误信息
                if (errorMessages.Any()) {
                    return new OperationResult { Success = false, Message = string.Join(Environment.NewLine, errorMessages) };
                }

                config.LastBackupTime = DateTime.Now;
                await _context.SaveChangesAsync();

                return new OperationResult { Success = true, Message = "所有数据库备份成功" };
            }
            catch (Exception ex) {
                return new OperationResult { Success = false, Message = ex.Message };
            }
        }

        private async Task BackupDatabase(DatabaseBackupConfig config, Database database, string dbName) {
            var backupLog = new BackupLog {
                DatabaseBackupConfigId = config.Id,
                DatabaseName = dbName,
                BackupTime = DateTime.Now,
                IsSuccess = false
            };

            try {
                var backupPath = await GenerateBackupPath(database, dbName);
                bool success = false;

                switch (database.Type.ToLower()) {
                    case "mysql":
                        success = await BackupMySqlDatabase(database, dbName, backupPath);
                        break;
                    case "postgresql":
                        success = await BackupPostgreSqlDatabase(database, dbName, backupPath);
                        break;
                    case "sqlserver":
                        success = await BackupSqlServerDatabase(database, dbName, backupPath);
                        break;
                }

                if (success) {
                    //压缩备份文件
                    string zipPath = await CompressFile(backupPath);
                    var fileInfo = new FileInfo(zipPath);
                    backupLog.IsSuccess = true;
                    backupLog.FilePath = zipPath;
                    backupLog.FileSize = fileInfo.Length;
                    backupLog.Message = "Backup completed successfully";
                }
            }
            catch (Exception ex) {
                backupLog.IsSuccess = false;
                backupLog.Message = ex.Message;
            }
            finally {
                _context.BackupLogs.Add(backupLog);
                await _context.SaveChangesAsync();
            }
        }
        private async Task<string> CompressFile(string filePath) {
            try {
                var zipPath = filePath + ".zip";
                using (var zipToOpen = new FileStream(zipPath, FileMode.Create))
                using (var archive = new ZipArchive(zipToOpen, ZipArchiveMode.Create)) {
                    await archive.CreateEntryFromFileAsync(filePath, Path.GetFileName(filePath));
                }
                //删除原始备份文件
                File.Delete(filePath);
                return zipPath;
            }
            catch (Exception ex) {
                return filePath;
            }
        }

        private async Task<string> GenerateBackupPath(Database database, string dbName) {
            var dateTime = DateTime.UtcNow;
            var subDirectory = Path.Combine(_backupDirectory, database.Type, dateTime.ToString("yyyy-MM-dd"));
            Directory.CreateDirectory(subDirectory);

            var fileName = $"{dbName}_{dateTime:yyyyMMdd_HHmmss}{GetBackupExtension(database.Type)}";
            return Path.Combine(subDirectory, fileName);
        }

        private string GetBackupExtension(string databaseType) {
            switch (databaseType.ToLower()) {
                case "mysql":
                    return ".sql";
                case "postgresql":
                    return ".sql";
                case "sqlserver":
                    return ".bak";
                default:
                    return ".bak";
            }
        }

        private async Task<bool> BackupMySqlDatabase(Database database, string dbName, string backupPath) {
            // 确保备份目录存在
            var backupDirectory = Path.GetDirectoryName(backupPath);
            if (!string.IsNullOrEmpty(backupDirectory) && !Directory.Exists(backupDirectory)) {
                Directory.CreateDirectory(backupDirectory);
            }

            try {
                // 使用mysqldump工具备份数据库
                var arguments = $"--host={database.Host} --port={database.Port} --user={database.Username} --password={database.Password} --databases {dbName} --single-transaction --add-drop-database --add-drop-table --skip-add-locks --extended-insert --create-options --default-character-set=utf8mb4 --result-file={backupPath}";

                using var process = new Process();
                process.StartInfo.FileName = "mysqldump";
                process.StartInfo.Arguments = arguments;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.RedirectStandardError = true;

                var errorOutput = new StringBuilder();
                process.ErrorDataReceived += (sender, e) => {
                    if (!string.IsNullOrEmpty(e.Data)) {
                        errorOutput.AppendLine(e.Data);
                    }
                };

                process.Start();
                process.BeginErrorReadLine();
                await process.WaitForExitAsync();

                if (process.ExitCode != 0) {
                    Console.WriteLine($"MySQL backup failed: {errorOutput.ToString()}");
                    return false;
                }

                return true;
            }
            catch (Exception ex) {
                Console.WriteLine($"MySQL backup failed: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> BackupPostgreSqlDatabase(Database database, string dbName, string backupPath) {
            var arguments = $"-h {database.Host} -p {database.Port} -U {database.Username} -d {dbName} -f {backupPath}";

            using var process = new Process();
            process.StartInfo.FileName = "pg_dump";
            process.StartInfo.Arguments = arguments;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.EnvironmentVariables["PGPASSWORD"] = database.Password;

            process.Start();
            await process.WaitForExitAsync();

            return process.ExitCode == 0;
        }

        private async Task<bool> BackupSqlServerDatabase(Database database, string dbName, string backupPath) {
            var connectionString = $"Data Source={database.Host},{database.Port};User ID={database.Username};Password={database.Password};";
            var query = $"BACKUP DATABASE [{dbName}] TO DISK = '{backupPath}' WITH INIT";

            using var connection = new System.Data.SqlClient.SqlConnection(connectionString);
            using var command = new System.Data.SqlClient.SqlCommand(query, connection);

            await connection.OpenAsync();
            await command.ExecuteNonQueryAsync();

            return true;
        }

        public async Task CreateBackupConfig(DatabaseBackupConfig config) {
            _context.DatabaseBackupConfigs.Add(config);
            await _context.SaveChangesAsync();

            Console.WriteLine($"[{DateTime.UtcNow}] 已创建备份配置: {config.Name}");
        }

        public async Task UpdateBackupConfig(DatabaseBackupConfig config) {
            _context.DatabaseBackupConfigs.Update(config);
            await _context.SaveChangesAsync();

            Console.WriteLine($"[{DateTime.UtcNow}] 已更新备份配置: {config.Name}");
        }

        public async Task DeleteBackupConfig(int id) {
            _context.DatabaseBackupConfigs.Remove(new DatabaseBackupConfig { Id = id });
            await _context.SaveChangesAsync();

            Console.WriteLine($"[{DateTime.UtcNow}] 已删除备份配置: {id}");
        }

        public async Task<OperationResult> RestoreBackup(int logId) {
            try {
                // 获取备份日志
                var log = await _context.BackupLogs
                    .FirstOrDefaultAsync(l => l.Id == logId);

                if (log == null || string.IsNullOrEmpty(log.FilePath) || !System.IO.File.Exists(log.FilePath)) {
                    return new OperationResult { Success = false, Message = "备份文件不存在" };
                }

                // 获取关联的备份配置
                var config = await _context.DatabaseBackupConfigs
                    .FirstOrDefaultAsync(c => c.Id == log.DatabaseBackupConfigId);

                if (config == null) {
                    return new OperationResult { Success = false, Message = "备份配置不存在" };
                }

                // 获取数据库连接信息
                var database = await _context.Databases
                    .FirstOrDefaultAsync(d => d.Id == config.DatabaseId);

                if (database == null) {
                    return new OperationResult { Success = false, Message = "数据库连接信息不存在" };
                }

                // 根据数据库类型执行还原
                OperationResult restoreResult = null;

                switch (database.Type.ToLower()) {
                    case "mysql":
                        restoreResult = await RestoreMySqlDatabase(database, log.DatabaseName, log.FilePath);
                        break;
                    case "postgresql":
                        restoreResult = await RestorePostgreSqlDatabase(database, log.DatabaseName, log.FilePath);
                        break;
                    case "sqlserver":
                        restoreResult = await RestoreSqlServerDatabase(database, log.DatabaseName, log.FilePath);
                        break;
                    default:
                        restoreResult = new OperationResult { Success = false, Message = "不支持的数据库类型" };
                        break;
                }

                return restoreResult;
            }
            catch (Exception ex) {
                return new OperationResult { Success = false, Message = ex.Message };
            }
        }

        // 还原MySQL数据库
        private async Task<OperationResult> RestoreMySqlDatabase(Database database, string dbName, string backupPath) {
            try {
                // 构建MySQL连接字符串（连接到mysql数据库，而不是目标数据库）
                var connectionString = $"server={database.Host};port={database.Port};user={database.Username};password={database.Password};database=mysql;";

                using var connection = new MySqlConnection(connectionString);
                await connection.OpenAsync();
                string backupContent = "";
                if (backupPath.EndsWith(".zip")) {
                    // 解压备份文件
                    using (var zipToOpen = new FileStream(backupPath, FileMode.Open))
                    using (var archive = new ZipArchive(zipToOpen, ZipArchiveMode.Read)) {
                        var entry = archive.Entries.FirstOrDefault();
                        if (entry == null)
                            throw new Exception("压缩包中没有找到备份文件");
                        backupContent = new StreamReader(entry.Open()).ReadToEnd();
                    }
                }
                else {
                    backupContent = await System.IO.File.ReadAllTextAsync(backupPath);
                }
                // 读取备份文件内容

                // 分割SQL语句（简单实现，实际可能需要更复杂的解析）
                var sqlStatements = backupContent.Split(new[] { ";" }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s.Trim());

                // 执行每个SQL语句
                using var transaction = await connection.BeginTransactionAsync();
                try {
                    foreach (var sql in sqlStatements) {
                        if (string.IsNullOrWhiteSpace(sql))
                            continue;

                        using var command = new MySqlCommand(sql, connection, transaction);
                        await command.ExecuteNonQueryAsync();
                    }

                    await transaction.CommitAsync();
                    return new OperationResult { Success = true, Message = "MySQL数据库还原成功" };
                }
                catch {
                    await transaction.RollbackAsync();
                    throw;
                }
            }
            catch (Exception ex) {
                Console.WriteLine($"MySQL数据库还原失败: {ex.Message}");
                return new OperationResult { Success = false, Message = $"MySQL数据库还原失败: {ex.Message}" };
            }
        }

        // 还原PostgreSQL数据库
        private async Task<OperationResult> RestorePostgreSqlDatabase(Database database, string dbName, string backupPath) {
            try {
                // 使用psql命令行工具还原PostgreSQL数据库
                var arguments = $"-h {database.Host} -p {database.Port} -U {database.Username} -d {dbName} -f {backupPath}";

                using var process = new Process();
                process.StartInfo.FileName = "psql";
                process.StartInfo.Arguments = arguments;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.EnvironmentVariables["PGPASSWORD"] = database.Password;

                process.Start();
                await process.WaitForExitAsync();

                if (process.ExitCode == 0) {
                    return new OperationResult { Success = true, Message = "PostgreSQL数据库还原成功" };
                }
                else {
                    return new OperationResult { Success = false, Message = "PostgreSQL数据库还原失败: 命令执行失败" };
                }
            }
            catch (Exception ex) {
                Console.WriteLine($"PostgreSQL数据库还原失败: {ex.Message}");
                return new OperationResult { Success = false, Message = $"PostgreSQL数据库还原失败: {ex.Message}" };
            }
        }

        // 还原SQL Server数据库
        private async Task<OperationResult> RestoreSqlServerDatabase(Database database, string dbName, string backupPath) {
            try {
                // 构建SQL Server连接字符串（连接到master数据库）
                var connectionString = $"Data Source={database.Host},{database.Port};User ID={database.Username};Password={database.Password};Database=master;";

                // 构建RESTORE DATABASE语句
                var query = $@"RESTORE DATABASE [{dbName}] FROM DISK = '{backupPath}' WITH REPLACE";

                using var connection = new System.Data.SqlClient.SqlConnection(connectionString);
                using var command = new System.Data.SqlClient.SqlCommand(query, connection);

                await connection.OpenAsync();
                await command.ExecuteNonQueryAsync();

                return new OperationResult { Success = true, Message = "SQL Server数据库还原成功" };
            }
            catch (Exception ex) {
                Console.WriteLine($"SQL Server数据库还原失败: {ex.Message}");
                return new OperationResult { Success = false, Message = $"SQL Server数据库还原失败: {ex.Message}" };
            }
        }
    }

    // 全局备份检查作业，每10分钟检查一次所有配置
    public class GlobalBackupCheckJob : IJob {
        private readonly AppDbContext _context;
        private readonly IDatabaseBackupService _backupService;

        public GlobalBackupCheckJob(AppDbContext context, IDatabaseBackupService backupService) {
            _context = context;
            _backupService = backupService;
        }

        public async Task Execute(IJobExecutionContext context) {
            Console.WriteLine($"[{DateTime.Now}] 开始全局备份检查");

            // 获取所有启用的备份配置
            var enabledConfigs = await _context.DatabaseBackupConfigs
                .Where(c => c.IsEnabled)
                .ToListAsync();

            Console.WriteLine($"[{DateTime.Now}] 发现 {enabledConfigs.Count} 个启用的备份配置");

            // 检查每个配置是否需要执行备份
            foreach (var config in enabledConfigs) {
                if (IsTimeToBackup(config)) {
                    Console.WriteLine($"[{DateTime.Now}] 执行配置 {config.Name} 的备份任务");
                    await _backupService.ExecuteBackup(config.Id);
                }
            }

            Console.WriteLine($"[{DateTime.Now}] 全局备份检查完成");
        }

        // 判断是否需要执行备份
        private bool IsTimeToBackup(DatabaseBackupConfig config) {
            try {
                // 快速失败：Cron表达式为空/全空白，直接返回false
                if (string.IsNullOrWhiteSpace(config.CronExpression)) {
                    return false;
                }

                // 1. 实例化Quartz CronExpression（确保是Quartz命名空间）
                var cronExpression = new CronExpression(config.CronExpression);

                // 2. 核心：指定时区（生产推荐固定中国东八区，脱离服务器依赖；也可保留Local）
                var targetTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai"); // 推荐生产使用
                                                                                           // var targetTimeZone = TimeZoneInfo.Local; // 若需依赖服务器本地时区，启用此行
                cronExpression.TimeZone = targetTimeZone;

                // 3. 获取当前带时区的时间（DateTimeOffset，避免Kind歧义）
                var now = DateTimeOffset.Now;
                // 基准时间：向前偏移10分钟，且强制转为UTC（Quartz入参必须为UTC，否则时区配置失效）
                var baseTimeUtc = now.AddMinutes(-10).ToUniversalTime();

                // 4. 计算下一次执行时间（入参UTC，返回DateTimeOffset?，已带时区转换）
                var nextExecution = cronExpression.GetNextValidTimeAfter(baseTimeUtc);

                // 快速失败：无有效执行时间
                if (!nextExecution.HasValue) {
                    return false;
                }

                // 5. 关键：将返回的UTC时间转换为「目标时区本地时间」（用于业务判断）
                var nextExecutionLocal = nextExecution.Value.LocalDateTime;
                var nowLocal = now.LocalDateTime; // 当前时间转目标时区本地时间，保持判断基准一致

                // 6. 核心判断：最近10分钟内需要执行 + 未执行过/未在该时间段执行过（防重复）
                // 条件1：下一次执行时间在【当前时间-10分钟，当前时间】范围内
                // 条件2：无最后备份时间 或 下一次执行时间晚于最后备份时间（避免重复备份）
                bool isInLast10Minutes = nextExecutionLocal >= nowLocal.AddMinutes(-10) && nextExecutionLocal <= nowLocal;
                bool isNotBackedUpYet = !config.LastBackupTime.HasValue || nextExecutionLocal > config.LastBackupTime.Value;

                if (isInLast10Minutes && isNotBackedUpYet) {
                    return true;
                }
            }
            catch (TimeZoneNotFoundException) {
                Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 检查配置{config.Name}失败：服务器未配置中国时区（Asia/Shanghai），请检查时区配置");
            }
            catch (Exception ex) {
                Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 检查配置{config.Name}的备份时间失败: {ex.Message}");
            }
            return false;
        }
    }
}