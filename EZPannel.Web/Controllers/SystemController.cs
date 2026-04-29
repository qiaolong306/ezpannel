


using EZPannel.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;


[Authorize]
public class SystemController : Controller {
    private readonly AppDbContext _appContext;
    private readonly ConfigService _configService;
    public SystemController(AppDbContext appContext, ConfigService configService) {
        _appContext = appContext;
        _configService = configService;
    }
    public IActionResult Index() {
        var configs = _appContext.Configs.ToDictionary(c => c.Key, c => c.Value);
        return View(configs);
    }
    public async Task<IActionResult> UpdateConfig([FromForm] string key, string val) {
        _configService.SetConfig(key, val);
        return Ok();
    }


    public async Task<IActionResult> SaveConfig([FromForm] Dictionary<string, string> configs) {
        foreach (var item in configs) {
            var config = _appContext.Configs.FirstOrDefault(c => c.Key == item.Key);
            if (config == null) {
                config = new ConfigModel {
                    Key = item.Key,
                    Value = item.Value
                };
                _appContext.Configs.Add(config);
            }
            else {
                config.Value = item.Value;
            }
        }
        await _appContext.SaveChangesAsync();
        return Json(new { success = true ,message="保存成功"});
    }
}