using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using EZPannel.Api.Data;
using EZPannel.Api.Models;

namespace EZPannel.Api.Controllers
{
    [Authorize(Roles = "Admin")]
    [Route("[controller]")]
    [Route("api/[controller]")]
    public class UsersController : Controller
    {
        private readonly AppDbContext _context;

        public UsersController(AppDbContext context)
        {
            _context = context;
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            ViewData["ActiveMenu"] = "users";
            var users = await _context.Users.ToListAsync();
            // 确保 admin 用户存在
            if (!users.Any(u => u.Username == "admin"))
            {
                var admin = new User 
                { 
                    Username = "admin", 
                    PasswordHash = "admin123", // Simplified for now to avoid BCrypt issues
                    Role = "Admin" 
                };
                _context.Users.Add(admin);
                await _context.SaveChangesAsync();
                users = await _context.Users.ToListAsync();
            }
            return View(users);
        }

        [HttpPost("UpdatePassword")]
        public async Task<IActionResult> UpdatePassword(int id, string newPassword)
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null) return NotFound();
            
            user.PasswordHash = newPassword; 
            await _context.SaveChangesAsync();
            return Ok();
        }

        [HttpGet("Profile")]
        public async Task<IActionResult> Profile()
        {
            ViewData["ActiveMenu"] = "profile";
            var username = User.Identity?.Name;
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Username == username);
            return View(user);
        }

        [HttpGet("list")]
        public async Task<ActionResult<IEnumerable<User>>> GetUsers()
        {
            return await _context.Users.ToListAsync();
        }

        [HttpPost("create")]
        public async Task<ActionResult<User>> CreateUser([FromBody] User user)
        {
            if (await _context.Users.AnyAsync(u => u.Username == user.Username))
            {
                return BadRequest("用户名已存在");
            }
            
            _context.Users.Add(user);
            await _context.SaveChangesAsync();
            return Ok(user);
        }

        [HttpDelete("delete/{id}")]
        public async Task<IActionResult> DeleteUser(int id)
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null) return NotFound();
            if (user.Username == "admin") return BadRequest("不能删除超级管理员");

            _context.Users.Remove(user);
            await _context.SaveChangesAsync();
            return NoContent();
        }
    }
}
