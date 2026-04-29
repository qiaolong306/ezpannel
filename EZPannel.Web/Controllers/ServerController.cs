using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using EZPannel.Api.Data;
using Microsoft.EntityFrameworkCore;
using System;
using System.Threading.Tasks;
using EZPannel.Api.Models;
namespace EZPannel.Api.Controllers
{
    [Authorize]
    public class ServerController : Controller
    {
        private readonly AppDbContext _context;
        public ServerController(AppDbContext context)
        {
            _context = context;
        }

        public async Task<IActionResult> Index(int serverId)
        {
            SetData(serverId);
            return View();
        }
        
        public async Task<IActionResult> FileManager(int serverId)
        {
            SetData(serverId);
            return View();
        }

        
        public async Task<IActionResult> Scripts(int serverId)
        {
            SetData(serverId);

            var server = await _context.Servers.FindAsync(serverId);

            // 获取所有脚本分类和脚本
            var categories = await _context.ScriptCategories.Include(c => c.Scripts).ToListAsync();
            foreach (var category in categories){
                category.Scripts = category.Scripts.Where(s => s.OS.Contains(server.OperatingSystem, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            // 如果没有分类，创建一个默认分类
            if (categories.Count == 0)
            {
                var defaultCat = new ScriptCategory { Name = "默认分类" };
                _context.ScriptCategories.Add(defaultCat);
                await _context.SaveChangesAsync();
                categories = await _context.ScriptCategories.Include(c => c.Scripts).ToListAsync();
            }
            
            return View(categories);
        }
        private void SetData(int serverId){
            ViewData["ServerId"] = serverId;
            var server = _context.Servers.Find(serverId);
            if (server == null) return;
            ViewData["ServerName"] = server.Name;
            ViewData["ServerVersion"] = server.OperatingSystem;
            // 根据操作系统设置图标和颜色
            if (server.OperatingSystem.Contains("Windows", StringComparison.OrdinalIgnoreCase))
            {
                ViewData["OsIcon"] = "bi-windows";
                ViewData["OsIconColor"] = "text-blue";
            }
            else // Linux
            {
                ViewData["OsIcon"] = "bi-server";
                ViewData["OsIconColor"] = "text-orange";
            }
        }
    }
}