


using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using EZPannel.Api.Data;

namespace EZPannel.App.Controllers
{
    [Authorize]
    public class ProxyController : Controller
    {
        private readonly ConfigService _configService;

        public ProxyController( ConfigService configService)
        {
            _configService = configService;
        }

        // GET: Proxy (ProxyUsers Index)
        public async Task<IActionResult> Index()
        {
            var proxyServerUrl = _configService.GetConfig(ConfigService.key_ProxyServerUrl);
            ViewBag.ProxyServerUrl = proxyServerUrl;
            return View();
        }

    }
}