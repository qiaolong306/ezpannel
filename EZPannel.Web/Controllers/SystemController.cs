using System.IO;
using System.Linq;
using EZPannel.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EZPannel.Api.Controllers
{
    [Authorize]
    [Route("[controller]")]
    [Route("api/[controller]")]
    public class SystemController : Controller {
        private readonly ISystemSettingsService _systemSettings;

        public SystemController(ISystemSettingsService systemSettings) {
            _systemSettings = systemSettings;
        }

        [HttpGet("Logs")]
        public IActionResult Logs() {
            ViewData["ActiveMenu"] = "settings_logs";
            return View();
        }

        // 服务器设置页面
        [HttpGet("ServerSettings")]
        public IActionResult ServerSettings() {
            ViewData["ActiveMenu"] = "settings_server";
            return View();
        }

        // 读取服务器设置
        [HttpGet("GetServerSettings")]
        public async Task<IActionResult> GetServerSettings() {
            var expireDays = await _systemSettings.GetDatabaseBackupExpireDaysAsync();
            return Json(new {
                success = true,
                databaseBackupExpireDays = expireDays,
                minExpireDays = SystemSettingsService.MinDatabaseBackupExpireDays,
                maxExpireDays = SystemSettingsService.MaxDatabaseBackupExpireDays
            });
        }

        // 保存服务器设置
        [HttpPost("SaveServerSettings")]
        public async Task<IActionResult> SaveServerSettings(int databaseBackupExpireDays) {
            try {
                await _systemSettings.SetDatabaseBackupExpireDaysAsync(databaseBackupExpireDays);
                return Json(new { success = true, message = "保存成功" });
            }
            catch (System.ArgumentOutOfRangeException ex) {
                return Json(new { success = false, message = ex.Message.Split('：')[0] });
            }
            catch (System.Exception ex) {
                return Json(new { success = false, message = "保存失败: " + ex.Message });
            }
        }

        // 日志文件列表（logs 目录下的按天日志）
        [HttpGet("LogFiles")]
        public IActionResult LogFiles() {
            var dir = FileLoggerProvider.DefaultDirectory;
            if (!Directory.Exists(dir)) {
                return Ok(new object[0]);
            }
            var files = Directory.GetFiles(dir, "*.log")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTime)
                .Select(f => new {
                    name = f.Name,
                    size = FormatSize(f.Length),
                    updated = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")
                });
            return Ok(files);
        }

        // 读取指定日志文件的尾部内容
        [HttpGet("LogContent")]
        public IActionResult LogContent(string file, int lines = 300) {
            var dir = FileLoggerProvider.DefaultDirectory;
            var name = Path.GetFileName(file ?? string.Empty);
            var path = Path.Combine(dir, name);
            if (string.IsNullOrEmpty(name) || !System.IO.File.Exists(path)) {
                return Ok(string.Empty);
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var all = new List<string>();
            while (!reader.EndOfStream) {
                all.Add(reader.ReadLine() ?? string.Empty);
            }
            var take = all.Skip(System.Math.Max(0, all.Count - System.Math.Clamp(lines, 1, 5000)));
            return Ok(string.Join("\n", take));
        }

        private static string FormatSize(long bytes) {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
            return (bytes / 1024.0 / 1024.0).ToString("0.0") + " MB";
        }
    }
}