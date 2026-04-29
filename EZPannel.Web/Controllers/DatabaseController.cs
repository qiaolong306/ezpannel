using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using EZPannel.Api.Data;
using EZPannel.Api.Models;
using EZPannel.Api.Services;
using System;
using System.Threading.Tasks;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using MySql.Data.MySqlClient;
using Npgsql;
using System.Data.SqlClient;

namespace EZPannel.Api.Controllers
{
    public class DatabaseController : Controller
    {
        private readonly AppDbContext _context;
    private readonly IDatabaseBackupService _backupService;

        public DatabaseController(AppDbContext context, IDatabaseBackupService backupService)
        {
            _context = context;
            _backupService = backupService;
        }

        public async Task<IActionResult> Index()
        {
            ViewData["ActiveMenu"] = "database";
            var databases = await _context.Databases.ToListAsync();
            return View(databases);
        }

        public IActionResult Create()
        {
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("Name,Host,Port,Username,Password,Type,Description,AutoBackup,BackupSchedule")] Database database)
        {
            if (ModelState.IsValid)
            {
                database.CreatedAt = DateTime.UtcNow;
                database.UpdatedAt = DateTime.UtcNow;
                _context.Add(database);
                await _context.SaveChangesAsync();
                return RedirectToAction(nameof(Index));
            }
            // 如果是AJAX请求，返回错误状态码
            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
            {
                return BadRequest(ModelState);
            }
            return View(database);
        }

        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var database = await _context.Databases.FindAsync(id);
            if (database == null)
            {
                return NotFound();
            }
            return View(database);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, [Bind("Id,Name,Host,Port,Username,Password,Type,Description,AutoBackup,BackupSchedule")] Database database)
        {
            if (id != database.Id)
            {
                return NotFound();
            }

            if (ModelState.IsValid)
            {
                try
                {
                    database.UpdatedAt = DateTime.UtcNow;
                    _context.Update(database);
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!DatabaseExists(database.Id))
                    {
                        return NotFound();
                    }
                    else
                    {
                        throw;
                    }
                }
                return RedirectToAction(nameof(Index));
            }
            // 如果是AJAX请求，返回错误状态码
            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
            {
                return BadRequest(ModelState);
            }
            return View(database);
        }

        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var database = await _context.Databases
                .FirstOrDefaultAsync(m => m.Id == id);
            if (database == null)
            {
                return NotFound();
            }

            return View(database);
        }

        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var database = await _context.Databases.FindAsync(id);
            if (database != null)
            {
                _context.Databases.Remove(database);
                await _context.SaveChangesAsync();
            }
            return RedirectToAction(nameof(Index));
        }



        [HttpPost]
        public async Task<IActionResult> TestConnection([FromBody] Database database)
        {
            if (database == null || string.IsNullOrEmpty(database.Host) || database.Port == 0 || string.IsNullOrEmpty(database.Username))
            {
                return Json(new { success = false, message = "请提供完整的数据库连接信息" });
            }

            try
            {
                switch (database.Type.ToLower())
                {
                    case "mysql":
                       string mysqlConnectString = $"server={database.Host};port={database.Port};user={database.Username};password={database.Password}";
                        // 测试MySQL连接
                        using (var connection = new MySql.Data.MySqlClient.MySqlConnection(mysqlConnectString))
                        {
                            await connection.OpenAsync();
                            return Json(new { success = true, message = "MySQL连接成功" });
                        }
                    case "postgresql":
                        // 测试PostgreSQL连接
                        using (var connection = new Npgsql.NpgsqlConnection(
                            $"Host={database.Host};Port={database.Port};Username={database.Username};Password={database.Password};Database=postgres;SSL Mode=Disable"))
                        {
                            await connection.OpenAsync();
                            return Json(new { success = true, message = "PostgreSQL连接成功" });
                        }
                    case "sqlserver":
                        // 测试SQL Server连接
                        using (var connection = new System.Data.SqlClient.SqlConnection(
                            $"Server={database.Host},{database.Port};User ID={database.Username};Password={database.Password};Integrated Security=False;TrustServerCertificate=True"))
                        {
                            await connection.OpenAsync();
                            return Json(new { success = true, message = "SQL Server连接成功" });
                        }
                    default:
                        return Json(new { success = false, message = "不支持的数据库类型" });
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        private bool DatabaseExists(int id)
    {
        return _context.Databases.Any(e => e.Id == id);
    }

    // Get database information API
    [HttpGet]
    public async Task<IActionResult> GetDatabaseInfo(int id)
    {
        try
        {
            Console.WriteLine($"获取数据库信息请求，ID: {id}");
            var database = await _context.Databases.FindAsync(id);
            if (database == null)
            {
                Console.WriteLine($"未找到数据库，ID: {id}");
                return NotFound();
            }
            
            Console.WriteLine($"数据库信息获取成功: {database.Name}");
            return Json(new {
                Id = database.Id,
                Name = database.Name,
                Host = database.Host,
                Port = database.Port,
                Type = database.Type
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"获取数据库信息失败，错误: {ex.Message}");
            return Json(new { success = false, error = ex.Message });
        }
    }



    // Saved Queries API
    [HttpGet]
    public async Task<IActionResult> GetSavedQueries()
    {
        try
        {
            Console.WriteLine($"获取已保存的查询");
            var queries = await _context.SavedQueries.ToListAsync();
            return Json(new { success = true, queries = queries });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"获取已保存的查询失败: {ex.Message}");
            return Json(new { success = false, error = ex.Message });
        }
    }

    [HttpPost]
    public async Task<IActionResult> SaveQuery([FromBody] SavedQuery query)
    {
        try
        {
            Console.WriteLine($"保存查询请求: {query.Name}");
            
            query.UpdatedAt = DateTime.UtcNow;
            if (query.Id == 0)
            {
                // 新建查询
                query.CreatedAt = DateTime.UtcNow;
                _context.SavedQueries.Add(query);
                Console.WriteLine($"添加新查询");
            }
            else
            {
                // 更新查询
                var existingQuery = await _context.SavedQueries.FindAsync(query.Id);
                if (existingQuery == null)
                {
                    return NotFound();
                }
                existingQuery.Name = query.Name;
                existingQuery.Sql = query.Sql;
                existingQuery.UpdatedAt = DateTime.UtcNow;
                Console.WriteLine($"更新现有查询");
            }
            
            await _context.SaveChangesAsync();
            Console.WriteLine($"查询保存成功");
            return Json(new { success = true, query = query });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"保存查询失败: {ex.Message}");
            return Json(new { success = false, error = ex.Message });
        }
    }

    [HttpDelete]
    public async Task<IActionResult> DeleteQuery(int id)
    {
        try
        {
            Console.WriteLine($"删除查询请求，ID: {id}");
            var query = await _context.SavedQueries.FindAsync(id);
            if (query == null)
            {
                return NotFound();
            }
            
            _context.SavedQueries.Remove(query);
            await _context.SaveChangesAsync();
            Console.WriteLine($"查询删除成功");
            return Json(new { success = true });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"删除查询失败: {ex.Message}");
            return Json(new { success = false, error = ex.Message });
        }
    }
        // SQL Execution
        [HttpPost]
        public async Task<IActionResult> ExecuteSql(int id, [FromBody] SqlExecutionRequest request)
        {
            Console.WriteLine($"收到SQL执行请求，数据库ID: {id}");
            Console.WriteLine($"请求的SQL: {request.Sql}");
            
            var database = await _context.Databases.FindAsync(id);
            if (database == null)
            {
                Console.WriteLine($"未找到数据库，ID: {id}");
                return NotFound();
            }

            try
            {
                Console.WriteLine($"开始执行SQL查询，数据库类型: {database.Type}");
                var result = await ExecuteSqlQuery(database, request.Sql);
                Console.WriteLine($"SQL查询执行成功，结果行数: {result.Rows.Count}");
                return Json(new { success = true, result = result });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SQL查询执行失败，错误信息: {ex.Message}");
                Console.WriteLine($"错误堆栈: {ex.StackTrace}");
                return Json(new { success = false, error = ex.Message });
            }
        }

  
        // }
        // Helper methods for database operations
        private async Task<SqlExecutionResult> ExecuteSqlQuery(Database database, string sql)
        {
            var result = new SqlExecutionResult();
            var startTime = DateTime.Now;

            try
            {
                sql = sql.Trim();
                bool isQuery = sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) ||
                             sql.StartsWith("SHOW", StringComparison.OrdinalIgnoreCase) ||
                             sql.StartsWith("DESCRIBE", StringComparison.OrdinalIgnoreCase) ||
                             sql.StartsWith("EXPLAIN", StringComparison.OrdinalIgnoreCase) ||
                             sql.StartsWith("HELP", StringComparison.OrdinalIgnoreCase);

                result.IsQuery = isQuery;

                switch (database.Type.ToLower())
                {
                    case "mysql":
                        await ExecuteMySqlQuery(database, sql, result);
                        break;
                    case "postgresql":
                        await ExecutePostgreSqlQuery(database, sql, result);
                        break;
                    case "sqlserver":
                        await ExecuteSqlServerQuery(database, sql, result);
                        break;
                    default:
                        throw new NotSupportedException($"不支持的数据库类型: {database.Type}");
                }
            }
            finally
            {
                result.ExecutionTime = DateTime.Now - startTime;
            }

            return result;
        }

        private async Task ExecuteMySqlQuery(Database database, string sql, SqlExecutionResult result)
        {
            using (var connection = new MySql.Data.MySqlClient.MySqlConnection(
                $"server={database.Host};port={database.Port};user={database.Username};password={database.Password};database=mysql"))
            {
                await connection.OpenAsync();

                using (var command = new MySql.Data.MySqlClient.MySqlCommand(sql, connection))
                {
                    if (result.IsQuery)
                    {
                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            // 获取列名
                            for (int i = 0; i < reader.FieldCount; i++)
                            {
                                result.Columns.Add(reader.GetName(i));
                            }

                            // 获取行数据
                            while (await reader.ReadAsync())
                            {
                                var row = new List<object>();
                                for (int i = 0; i < reader.FieldCount; i++)
                                {
                                    row.Add(reader.IsDBNull(i) ? null : reader.GetValue(i));
                                }
                                result.Rows.Add(row);
                            }
                        }
                    }
                    else
                    {
                        result.AffectedRows = await command.ExecuteNonQueryAsync();
                    }
                }
            }
        }

        private async Task ExecutePostgreSqlQuery(Database database, string sql, SqlExecutionResult result)
        {
            using (var connection = new Npgsql.NpgsqlConnection(
                $"Host={database.Host};Port={database.Port};Username={database.Username};Password={database.Password};Database=postgres;SSL Mode=Disable"))
            {
                await connection.OpenAsync();

                using (var command = new Npgsql.NpgsqlCommand(sql, connection))
                {
                    if (result.IsQuery)
                    {
                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            // 获取列名
                            for (int i = 0; i < reader.FieldCount; i++)
                            {
                                result.Columns.Add(reader.GetName(i));
                            }

                            // 获取行数据
                            while (await reader.ReadAsync())
                            {
                                var row = new List<object>();
                                for (int i = 0; i < reader.FieldCount; i++)
                                {
                                    row.Add(reader.IsDBNull(i) ? null : reader.GetValue(i));
                                }
                                result.Rows.Add(row);
                            }
                        }
                    }
                    else
                    {
                        result.AffectedRows = await command.ExecuteNonQueryAsync();
                    }
                }
            }
        }

        private async Task ExecuteSqlServerQuery(Database database, string sql, SqlExecutionResult result)
        {
            using (var connection = new System.Data.SqlClient.SqlConnection(
                $"Server={database.Host},{database.Port};User ID={database.Username};Password={database.Password};Integrated Security=False;TrustServerCertificate=True;Database=master"))
            {
                await connection.OpenAsync();

                using (var command = new System.Data.SqlClient.SqlCommand(sql, connection))
                {
                    if (result.IsQuery)
                    {
                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            // 获取列名
                            for (int i = 0; i < reader.FieldCount; i++)
                            {
                                result.Columns.Add(reader.GetName(i));
                            }

                            // 获取行数据
                            while (await reader.ReadAsync())
                            {
                                var row = new List<object>();
                                for (int i = 0; i < reader.FieldCount; i++)
                                {
                                    row.Add(reader.IsDBNull(i) ? null : reader.GetValue(i));
                                }
                                result.Rows.Add(row);
                            }
                        }
                    }
                    else
                    {
                        result.AffectedRows = await command.ExecuteNonQueryAsync();
                    }
                }
            }
        }

        // Backup Management
        public async Task<IActionResult> Backup()
        {
            ViewData["ActiveMenu"] = "database_backup";
            var backupConfigs = await _context.DatabaseBackupConfigs
                .ToListAsync();

            return View(backupConfigs);
        }

        // Restore Management
        public async Task<IActionResult> Restore(int page = 1, int pageSize = 10)
        {
            ViewData["ActiveMenu"] = "database_restore";
            // 计算总记录数
            var totalLogs = await _context.BackupLogs
                .Where(l => _context.DatabaseBackupConfigs.Any(c => c.Id == l.DatabaseBackupConfigId ))
                .CountAsync();

            // 分页查询
            var model = from a in _context.BackupLogs join b in _context.DatabaseBackupConfigs on a.DatabaseBackupConfigId equals b.Id
                select new BackupLog
                {
                    Id = a.Id,
                    DatabaseBackupConfigId = a.DatabaseBackupConfigId,
                    DatabaseName = a.DatabaseName,
                    BackupTime = a.BackupTime,
                    IsSuccess = a.IsSuccess,
                    Message = a.Message,
                    FilePath = a.FilePath,
                    FileSize = a.FileSize,
                    DatabaseConfigName = b.Name
                };
           var backupLogs =await model.OrderByDescending(l => l.BackupTime).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
            // 计算总页数
            var totalPages = (int)Math.Ceiling(totalLogs / (double)pageSize);
            ViewBag.Page = page;
            ViewBag.PageSize = pageSize;
            ViewBag.TotalPages = totalPages;
            ViewBag.TotalLogs = totalLogs;
            return View(backupLogs);
        }

        [HttpGet]
        public async Task<IActionResult> GetBackupConfig(int id)
        {
            var config = await _context.DatabaseBackupConfigs
                .Where(c => c.Id == id)
                .Select(c => new {
                    id = c.Id,
                    name = c.Name,
                    databaseId = c.DatabaseId,
                    databaseNames = c.DatabaseNames,
                    cronExpression = c.CronExpression,
                    isEnabled = c.IsEnabled
                })
                .FirstOrDefaultAsync();
            return Json(new { success = true, config = config });
        }

        [HttpGet]
        public async Task<IActionResult> CheckConfigNameExists(string name, int excludeId = 0)
        {
            if (string.IsNullOrEmpty(name))
            {
                return Json(new { exists = false });
            }

            var exists = await _context.DatabaseBackupConfigs
                .AnyAsync(c => c.Name == name && c.Id != excludeId);

            return Json(new { exists = exists });
        }

        [HttpPost]
        public async Task<IActionResult> CreateBackupConfig(DatabaseBackupConfig config)
        {
            if (ModelState.IsValid)
            {
                await _backupService.CreateBackupConfig(config);
                return Json(new { success = true });
            }
            return Json(new { success = false, errors = ModelState });
        }

        [HttpPost]
        public async Task<IActionResult> UpdateBackupConfig(DatabaseBackupConfig config)
        {
            if (ModelState.IsValid)
            {
                await _backupService.UpdateBackupConfig(config);
                return Json(new { success = true });
            }
            return Json(new { success = false, errors = ModelState });
        }

        [HttpPost]
        public async Task<IActionResult> RestoreBackup(int logId)
        {
            try
            {
                var result = await _backupService.RestoreBackup(logId);
                return Json(new { success = result.Success, message = result.Message });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> DeleteBackupConfig(int id)
        {
            await _backupService.DeleteBackupConfig(id);
            return Json(new { success = true });
        }

        [HttpPost]
        public async Task<IActionResult> ExecuteBackup(int configId)
        {
            try
            {
                var result = await _backupService.ExecuteBackup(configId);
                return Json(new { success = result.Success, message = result.Message });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetBackupLogs(int logId)
        {
            var log = await _context.BackupLogs
                .Where(l => l.Id == logId)
                .Join(_context.DatabaseBackupConfigs,
                    l => l.DatabaseBackupConfigId,
                    c => c.Id,
                    (l, c) => new { l, c })
                .Select(x => new BackupLog
                {
                    Id = x.l.Id,
                    DatabaseBackupConfigId = x.l.DatabaseBackupConfigId,
                    DatabaseName = x.l.DatabaseName,
                    BackupTime = x.l.BackupTime,
                    IsSuccess = x.l.IsSuccess,
                    Message = x.l.Message,
                    FilePath = x.l.FilePath,
                    FileSize = x.l.FileSize,
                    DatabaseConfigName = x.c.Name
                })
                .FirstOrDefaultAsync();
            return Json(new { success = true, log = log });
        }

        [HttpGet]
        public IActionResult DownloadBackup(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
            {
                return NotFound();
            }

            var fileName = System.IO.Path.GetFileName(filePath);
            var contentType = "application/octet-stream";
            return PhysicalFile(filePath, contentType, fileName);
        }

        [HttpGet]
        public async Task<IActionResult> GetAllDatabases()
        {
            var databases = await _context.Databases
                .Select(d => new {
                    id = d.Id,
                    name = d.Name,
                    host = d.Host,
                    port = d.Port,
                    type = d.Type
                })
                .ToListAsync();

            return Json(new { success = true, databases = databases });
        }

        // 还原结果类
        private class RestoreResult
        {
            public bool Success { get; set; }
            public string Message { get; set; }
        }

        // 还原MySQL数据库
        private async Task<RestoreResult> RestoreMySqlDatabase(Database database, string dbName, string backupPath)
        {
            try
            {
                // 构建MySQL连接字符串（连接到mysql数据库，而不是目标数据库）
                var connectionString = $"server={database.Host};port={database.Port};user={database.Username};password={database.Password};database=mysql;";
                
                using var connection = new MySqlConnection(connectionString);
                await connection.OpenAsync();
                
                // 读取备份文件内容
                var backupContent = await System.IO.File.ReadAllTextAsync(backupPath);
                
                // 分割SQL语句（简单实现，实际可能需要更复杂的解析）
                var sqlStatements = backupContent.Split(new[] { ";" }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s.Trim());
                
                // 执行每个SQL语句
                using var transaction = await connection.BeginTransactionAsync();
                try
                {
                    foreach (var sql in sqlStatements)
                    {
                        if (string.IsNullOrWhiteSpace(sql))
                            continue;
                            
                        using var command = new MySqlCommand(sql, connection, transaction);
                        await command.ExecuteNonQueryAsync();
                    }
                    
                    await transaction.CommitAsync();
                    return new RestoreResult { Success = true, Message = "MySQL数据库还原成功" };
                }
                catch
                {
                    await transaction.RollbackAsync();
                    throw;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"MySQL数据库还原失败: {ex.Message}");
                return new RestoreResult { Success = false, Message = $"MySQL数据库还原失败: {ex.Message}" };
            }
        }

        // 还原PostgreSQL数据库
        private async Task<RestoreResult> RestorePostgreSqlDatabase(Database database, string dbName, string backupPath)
        {
            try
            {
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

                if (process.ExitCode == 0)
                {
                    return new RestoreResult { Success = true, Message = "PostgreSQL数据库还原成功" };
                }
                else
                {
                    return new RestoreResult { Success = false, Message = "PostgreSQL数据库还原失败: 命令执行失败" };
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"PostgreSQL数据库还原失败: {ex.Message}");
                return new RestoreResult { Success = false, Message = $"PostgreSQL数据库还原失败: {ex.Message}" };
            }
        }

        // 还原SQL Server数据库
        private async Task<RestoreResult> RestoreSqlServerDatabase(Database database, string dbName, string backupPath)
        {
            try
            {
                // 构建SQL Server连接字符串（连接到master数据库）
                var connectionString = $"Data Source={database.Host},{database.Port};User ID={database.Username};Password={database.Password};Database=master;";
                
                // 构建RESTORE DATABASE语句
                var query = $@"RESTORE DATABASE [{dbName}] FROM DISK = '{backupPath}' WITH REPLACE";
                
                using var connection = new SqlConnection(connectionString);
                using var command = new SqlCommand(query, connection);
                
                await connection.OpenAsync();
                await command.ExecuteNonQueryAsync();
                
                return new RestoreResult { Success = true, Message = "SQL Server数据库还原成功" };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SQL Server数据库还原失败: {ex.Message}");
                return new RestoreResult { Success = false, Message = $"SQL Server数据库还原失败: {ex.Message}" };
            }
        }
    }
}