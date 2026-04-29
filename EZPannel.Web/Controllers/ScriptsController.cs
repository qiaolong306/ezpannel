using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using EZPannel.Api.Data;
using EZPannel.Api.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace EZPannel.Api.Controllers
{
    [Authorize]
    [Route("[controller]")]
    [Route("api/[controller]")]
    public class ScriptsController : Controller
    {
        private readonly AppDbContext _context;
        private readonly ScriptAutoRunService _scriptAutoRunService;

        public ScriptsController(AppDbContext context, ScriptAutoRunService scriptAutoRunService)
        {
            _context = context;
            _scriptAutoRunService = scriptAutoRunService;
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            ViewData["ActiveMenu"] = "scripts";
            var categories = await _context.ScriptCategories.Include(c => c.Scripts).ToListAsync();
            // 如果没分类，创建一个默认的
            if (categories.Count == 0)
            {
                var defaultCat = new ScriptCategory { Name = "默认分类" };
                _context.ScriptCategories.Add(defaultCat);
                await _context.SaveChangesAsync();
                categories = await _context.ScriptCategories.Include(c => c.Scripts).ToListAsync();
            }
            return View(categories);
        }

        [HttpPost("Category")]
        public async Task<IActionResult> CreateCategory([FromForm] string name)
        {
            var category = new ScriptCategory { Name = name };
            _context.ScriptCategories.Add(category);
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }

        [HttpGet("History")]
        public async Task<IActionResult> History(int? page, int? size)
        {
            ViewData["ActiveMenu"] = "scripts_history";
            int pageNumber = page ?? 1;
            int pageSize = size ?? 10;
            int skip = (pageNumber - 1) * pageSize;

            var totalCount = await _context.ScriptHistories.CountAsync();
            var history = await _context.ScriptHistories
                .Include(h => h.Script)
                .Include(h => h.Server)
                .OrderByDescending(h => h.ExecutedAt)
                .Skip(skip)
                .Take(pageSize)
                .ToListAsync();

            var totalPages = (int)Math.Ceiling((double)totalCount / pageSize);

            var viewModel = new
            {
                Histories = history,
                CurrentPage = pageNumber,
                TotalPages = totalPages,
                TotalCount = totalCount,
                PageSize = pageSize
            };

            return View(viewModel);
        }

        [HttpGet("History/api")]
        public async Task<IActionResult> GetHistory(int? page, int? size, int? serverId)
        {
            int pageNumber = page ?? 1;
            int pageSize = size ?? 10;
            int skip = (pageNumber - 1) * pageSize;

            var query = _context.ScriptHistories.AsQueryable();

            // 如果提供了serverId，则过滤该服务器的历史记录
            if (serverId.HasValue)
            {
                query = query.Where(h => h.ServerId == serverId.Value);
            }

            var totalCount = await query.CountAsync();
            var history = await query
                .Include(h => h.Script)
                .Include(h => h.Server)
                .OrderByDescending(h => h.ExecutedAt)
                .Skip(skip)
                .Take(pageSize)
                .ToListAsync();

            var totalPages = (int)Math.Ceiling((double)totalCount / pageSize);

            return Ok(new
            {
                Data = history,
                TotalCount = totalCount,
                PageNumber = pageNumber,
                PageSize = pageSize,
                TotalPages = totalPages
            });
        }

        [HttpPost("LogHistory")]
        public async Task<IActionResult> LogHistory([FromForm] int scriptId, [FromForm] int serverId)
        {
            var history = new ScriptHistory
            {
                ScriptId = scriptId,
                ServerId = serverId,
                Executor = User.Identity?.Name ?? "System",
                Success = true
            };
            _context.ScriptHistories.Add(history);
            await _context.SaveChangesAsync();
            return Ok();
        }

        [HttpPost("upload-to-server")]
        public async Task<IActionResult> UploadToServer([FromForm] int serverId, [FromForm] string fileName, [FromForm] string content)
        {
            try
            {
                content = content.Replace("\r", "\n");
                // 获取服务器信息
                var server = await _context.Servers.FindAsync(serverId);
                if (server == null)
                {
                    return BadRequest(new { message = "Server not found" });
                }

                // 使用SSH连接到服务器并上传文件
                using (var client = new Renci.SshNet.SshClient(server.Host, server.Port, server.Username, server.Password))
                {
                    client.Connect();

                    // 确定上传路径 - 对于不同操作系统使用不同的路径
                    string remotePath;
                    if (server.OperatingSystem == "Windows")
                    {
                        remotePath = "ez-script-tmp\\" + fileName;
                        // 确保C:\ez-script-tmp\目录存在
                        using (var cmd = client.CreateCommand("if not exist \".\ez-script-tmp\" mkdir \"ez-script-tmp\""))
                        {
                            cmd.Execute();
                        }
                    }
                    else
                    {
                        remotePath = "ez-script-tmp/" + fileName;
                        // 确保/tmp目录存在
                        using (var cmd = client.CreateCommand("mkdir -p ez-script-tmp"))
                        {
                            cmd.Execute();
                        }
                    }

                    // 将脚本内容写入临时文件并上传
                    using (var sftp = new Renci.SshNet.SftpClient(server.Host, server.Port, server.Username, server.Password))
                    {
                        sftp.Connect();
                        // 将内容写入字节数组
                        byte[] contentBytes = System.Text.Encoding.UTF8.GetBytes(content);
                        using (var stream = new System.IO.MemoryStream(contentBytes))
                        {
                            sftp.UploadFile(stream, remotePath, true);
                        }
                        
                        sftp.Disconnect();
                    }

                    client.Disconnect();

                    return Ok(new { filePath = remotePath });
                }
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> CreateScript([FromForm] Script script)
        {
            if (script.Id > 0)
            {
                _context.Scripts.Update(script);
            }
            else
            {
                _context.Scripts.Add(script);
            }
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteScript(int id)
        {
            var script = await _context.Scripts.FindAsync(id);
            if (script == null) return NotFound();
            _context.Scripts.Remove(script);
            await _context.SaveChangesAsync();
            return Ok();
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetScript(int id)
        {
            var script = await _context.Scripts.FindAsync(id);
            if (script == null) return NotFound();
            return Ok(script);
        }

        // 自动任务相关方法
        [HttpGet("AutoRun")]
        public async Task<IActionResult> AutoRun()
        {
            ViewData["ActiveMenu"] = "scripts_autorun";
            var autoRuns = await _context.ScriptAutoRuns.ToListAsync();
            var scripts = await _context.Scripts.ToListAsync();
            var servers = await _context.Servers.ToListAsync();
            
            var viewModel = new
            {
                AutoRuns = autoRuns,
                Scripts = scripts,
                Servers = servers
            };
            
            return View(viewModel);
        }

        [HttpPost("AutoRun")]
        public async Task<IActionResult> CreateAutoRun([FromForm] ScriptAutoRunModel model)
        {
            if (model.Id > 0)
            {
                _context.ScriptAutoRuns.Update(model);
            }
            else
            {
                _context.ScriptAutoRuns.Add(model);
            }
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(AutoRun));
        }

        [HttpDelete("AutoRun/{id}")]
        public async Task<IActionResult> DeleteAutoRun(int id)
        {
            var autoRun = await _context.ScriptAutoRuns.FindAsync(id);
            if (autoRun == null) return NotFound();
            _context.ScriptAutoRuns.Remove(autoRun);
            await _context.SaveChangesAsync();
            return Ok();
        }

        [HttpGet("AutoRun/{id}")]
        public async Task<IActionResult> GetAutoRun(int id)
        {
            var autoRun = await _context.ScriptAutoRuns.FindAsync(id);
            if (autoRun == null) return NotFound();
            return Ok(autoRun);
        }

        [HttpPost("AutoRun/Execute/{id}")]
        public async Task<IActionResult> ExecuteAutoRun(int id)
        {
            try
            {
                var model = await _context.ScriptAutoRuns.FindAsync(id);
                if (model == null) return NotFound();
                var result = await _scriptAutoRunService.ExecuteAutoRunAsync(model);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
            return Ok();
        }
    }
}