using System.IO;
using System.IO.Compression;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EZPannel.Api.Controllers
{
    [Authorize]
    public class HomeController : Controller
    {
        public IActionResult Index()
        {
            return View();
        }

        // 打包下载目标机器上运行的 EZPannelClient（Agent）客户端
        public IActionResult DownloadClient()
        {
            var clientDir = Path.Combine(AppContext.BaseDirectory, "Client");
            if (!Directory.Exists(clientDir))
            {
                return NotFound("客户端文件不存在，请确认已随程序一起发布 Client 目录");
            }

            using var ms = new MemoryStream();
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                foreach (var file in Directory.GetFiles(clientDir))
                {
                    var name = Path.GetFileName(file);
                    if (name.EndsWith(".pyc", System.StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                    using var entryStream = entry.Open();
                    using var source = System.IO.File.OpenRead(file);
                    source.CopyTo(entryStream);
                }
            }

            ms.Position = 0;
            return File(ms.ToArray(), "application/zip", "ezpannel-client.zip");
        }
    }
}