using System.Text;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Mvc;
using EZPannel.Api.Data;
using Lazy.Captcha.Core;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication;
using EZPannel.Api.DTOs;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.EntityFrameworkCore;



namespace EZPannel.Api.Controllers
{
    [Route("[controller]")]
    public class AuthController : Controller
    {
        private readonly AppDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly ICaptcha _captcha;

        public AuthController(AppDbContext context, IConfiguration configuration, ICaptcha captcha)
        {
            _context = context;
            _configuration = configuration;
            _captcha = captcha;
        }

        /// <summary>
    /// 运行时检测系统，返回对应默认字体名
    /// </summary>
    private static string GetSystemDefaultFontName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Windows系统：原生自带Arial，无需额外安装
            return "Arial";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // Linux系统：绝大多数发行版自带DejaVu Sans
            return "DejaVu Sans";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // macOS系统：原生自带Helvetica
            return "Helvetica";
        }
            return "DejaVu Sans";
       
    }

        [HttpGet("Login")]
        public IActionResult Login()
        {
            return View();
        }

        [HttpGet("Code")]
        public async Task<IActionResult> Code() {
            var  id = Guid.NewGuid().ToString();
            // 生成验证码
            var captchaResult = _captcha.Generate(id);
            // 存储验证码到会话
            HttpContext.Session.SetString($"CaptchaId", id);
            // 返回验证码图片
            return File(captchaResult.Bytes, "image/png");
        }
 
        [HttpPost("Login")]
        public async Task<IActionResult> Login([FromForm] string username, [FromForm] string password, [FromForm] string code)
        {
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Username == username);

            if (user == null && username == "admin")
            {
                user = new Models.User { Username = "admin", PasswordHash = "admin123", Role = "Admin" };
                _context.Users.Add(user);
                await _context.SaveChangesAsync();
            }
           // 验证验证码
            if (string.IsNullOrEmpty(code))
            {
                return Unauthorized(new { success = false, message = "请输入验证码" });
            }
            string storedCaptchaId = HttpContext.Session.GetString("CaptchaId");
            if (string.IsNullOrEmpty(storedCaptchaId) || !_captcha.Validate(storedCaptchaId, code))
            {
                return Unauthorized(new { success = false, message = "验证码错误" });
            }
            if (user == null || user.PasswordHash != password) {
                ViewBag.Error = "用户名或密码错误";
                return View();
            }
            HttpContext.Session.Remove("Code");
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Role, user.Role),
            };

            var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);

            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(claimsIdentity));

            // Also generate token for JS use (Axios/SignalR)
            var token = GenerateJwtToken(user);
            // In MVC we can't easily return JSON and Redirect at the same time without JS.
            // But we can store token in Cookie or localStorage via a bridge page.
            // For simplicity, we'll store it in a cookie that JS can read, or just pass it to the view.
            Response.Cookies.Append("access_token", token);
            
            return RedirectToAction("Index", "Home");
        }

        [HttpPost("api/login")]
        public async Task<IActionResult> ApiLogin([FromBody] LoginDto loginDto)
        {
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Username == loginDto.Username);
            if (user == null || user.PasswordHash != loginDto.Password) return Unauthorized();
            var token = GenerateJwtToken(user);
            return Ok(new { token });
        }

        [HttpGet("Logout")]
        public async Task<IActionResult> Logout()
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            Response.Cookies.Delete("access_token");
            return RedirectToAction("Login");
        }

        private string GenerateJwtToken(Models.User user)
        {
            var jwtSettings = _configuration.GetSection("Jwt");
            var key = Encoding.ASCII.GetBytes(jwtSettings["Key"] ?? "super_secret_key_ezpannel_2026_long_enough");
            
            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.Name, user.Username),
                    new Claim(ClaimTypes.Role, user.Role)
                }),
                Expires = DateTime.UtcNow.AddDays(7),
                SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature),
                Issuer = jwtSettings["Issuer"],
                Audience = jwtSettings["Audience"]
            };

            var tokenHandler = new JwtSecurityTokenHandler();
            var token = tokenHandler.CreateToken(tokenDescriptor);
            return tokenHandler.WriteToken(token);
        }
    }
}