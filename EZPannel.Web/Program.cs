using System.Text;
using EZPannel.Api.Data;
using EZPannel.Api.Hubs;
using EZPannel.Api.Services;
using Lazy.Captcha.Core.Generator;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Quartz;

var builder = WebApplication.CreateBuilder(args);

// 同时输出到控制台与 logs 目录下的按天日志文件（系统设置 - 查看日志 读取此目录）
builder.Logging.AddProvider(new FileLoggerProvider(FileLoggerProvider.DefaultDirectory));

// 注册编码提供程序以支持 GBK 等编码
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.AddOpenApi();
builder.Services.AddSignalR();
builder.Services.AddSession();

// 配置验证码服务
builder.Services.AddCaptcha(options =>
{
    options.CaptchaType = CaptchaType.DEFAULT;
    options.CodeLength = 4;
    options.ExpirySeconds = 120;
    options.IgnoreCase = true;
});

// Register Agent Services (HTTP/WebSocket，目标机器上运行的 EZPannelClient)
builder.Services.AddHttpClient();
builder.Services.AddScoped<AgentService>();
builder.Services.AddScoped<AgentFileService>();

// Register Database Backup Services
builder.Services.AddScoped<IDatabaseBackupService, DatabaseBackupService>();
builder.Services.AddScoped<ScriptAutoRunService>();
builder.Services.AddScoped<ISystemSettingsService, SystemSettingsService>();

// Configure Quartz Scheduler
builder.Services.AddQuartz(q => {
    q.UseMicrosoftDependencyInjectionJobFactory();
});

builder.Services.AddQuartzHostedService(q => {
    q.WaitForJobsToComplete = true;
});

// Add DbContext
builder.Services.AddDbContext<AppDbContext>(options => {
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")).EnableSensitiveDataLogging();
   } );


// Add CORS
builder.Services.AddCors(options => {
    options.AddPolicy("AllowVue", policy => {
        policy.WithOrigins("http://localhost:5173")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials(); // SignalR requires this
    });
});

// Add JWT & Cookie Authentication
var jwtSettings = builder.Configuration.GetSection("Jwt");
var key = Encoding.ASCII.GetBytes(jwtSettings["Key"] ?? "super_secret_key_ezpannel_2026_long_enough");

builder.Services.AddAuthentication(x => {
    x.DefaultAuthenticateScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    x.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
})
.AddCookie(options => {
    options.LoginPath = "/Auth/Login";
    options.AccessDeniedPath = "/Auth/AccessDenied";
})
.AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, x => {
    x.RequireHttpsMetadata = false;
    x.SaveToken = true;
    x.TokenValidationParameters = new TokenValidationParameters {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(key),
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidIssuer = jwtSettings["Issuer"],
        ValidAudience = jwtSettings["Audience"]
    };

    // 允许 SignalR 通过 QueryString 传递 Token
    x.Events = new JwtBearerEvents {
        OnMessageReceived = context => {
            var accessToken = context.Request.Query["access_token"];
            var path = context.HttpContext.Request.Path;
            if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/terminalHub")) {
                context.Token = accessToken;
            }
            return Task.CompletedTask;
        }
    };
});
var app = builder.Build();

// 自动创建数据库
using (var scope = app.Services.CreateScope()) {
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();

    // EnsureCreated 对已存在的库不会补建新表，这里为系统设置表做一次幂等建表
    db.Database.ExecuteSqlRaw(
        "CREATE TABLE IF NOT EXISTS \"SystemSettings\" (" +
        "\"SettingKey\" TEXT NOT NULL CONSTRAINT \"PK_SystemSettings\" PRIMARY KEY, " +
        "\"Value\" TEXT NULL, " +
        "\"UpdatedAt\" TEXT NOT NULL);");
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment()) {
    app.MapOpenApi();
}
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseSession();
app.UseCors("AllowVue");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");
app.MapControllers();
app.MapHub<TerminalHub>("/terminalHub");

// Run the application with backup initialization
await RunWithBackupInitialization(app);




async Task RunWithBackupInitialization(WebApplication application) {
    // Initialize backup jobs
    using (var scope = application.Services.CreateScope()) {
        var backupService = scope.ServiceProvider.GetRequiredService<IDatabaseBackupService>();
        var autoRunService = scope.ServiceProvider.GetRequiredService<ScriptAutoRunService>();
        await backupService.InitializeBackupJobs();
        await autoRunService.InitializeAutoRunJobs();
    }
    await application.RunAsync();
}