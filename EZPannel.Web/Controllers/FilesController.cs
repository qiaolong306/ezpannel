using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using EZPannel.Api.Data;
using EZPannel.Api.Services;
using System.IO;
using EZPannel.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace EZPannel.Api.Controllers {
    public class SaveFileRequest {
        public string Path { get; set; }
        public string Content { get; set; }
    }


    [Authorize]
    [Route("api/[controller]")]
    [ApiController]
    public class FilesController : ControllerBase {
        private readonly AppDbContext _context;
        private readonly SftpService _sftpService;

        public FilesController(AppDbContext context, SftpService sftpService) {
            _context = context;
            _sftpService = sftpService;
        }

        [HttpGet("{serverId}/list")]
        public async Task<IActionResult> ListFiles(int serverId, [FromQuery] string path = "/") {
            var server = await _context.Servers.FindAsync(serverId);
            if (server == null) return NotFound();

            try {
                var files = await _sftpService.ListFilesAsync(server, path);
                return Ok(files);
            }
            catch (System.Exception ex) {
                return BadRequest(ex.Message);
            }
        }

        [HttpPost("{serverId}/upload")]
        public async Task<IActionResult> UploadFile(int serverId, IFormFile file, [FromQuery] string path) {
            var server = await _context.Servers.FindAsync(serverId);
            if (server == null) return NotFound();

            if (file == null || file.Length == 0) return BadRequest("No file uploaded");

            try {
                using var stream = file.OpenReadStream();
                var remotePath = Path.Combine(path, file.FileName).Replace("\\", "/");
                await _sftpService.UploadFileAsync(server, remotePath, stream);
                return Ok();
            }
            catch (System.Exception ex) {
                return BadRequest(ex.Message);
            }
        }

        [HttpGet("{serverId}/download")]
        public async Task<IActionResult> DownloadFile(int serverId, [FromQuery] string path) {
            var server = await _context.Servers.FindAsync(serverId);
            if (server == null) return NotFound();

            try {
                var data = await _sftpService.DownloadFileAsync(server, path);
                var fileName = Path.GetFileName(path);
                return File(data, "application/octet-stream", fileName);
            }
            catch (System.Exception ex) {
                return BadRequest(ex.Message);
            }
        }

        [HttpDelete("{serverId}")]
        public async Task<IActionResult> DeleteFile(int serverId, [FromQuery] string path, [FromQuery] bool isDirectory = false) {
            var server = await _context.Servers.FindAsync(serverId);
            if (server == null) return NotFound();

            try {
                await _sftpService.DeleteAsync(server, path, isDirectory);
                return Ok();
            }
            catch (System.Exception ex) {
                return BadRequest(ex.Message);
            }
        }

        [HttpGet("{serverId}/content")]
        public async Task<IActionResult> GetFileContent(int serverId, [FromQuery] string path) {
            var server = await _context.Servers.FindAsync(serverId);
            if (server == null) return NotFound();

            try {
                var content = await _sftpService.GetFileContentAsync(server, path);
                return Ok(new { content = content });
            }
            catch (System.Exception ex) {
                return BadRequest(ex.Message);
            }
        }

        [HttpPost("{serverId}/save")]
        public async Task<IActionResult> SaveFileContent(int serverId, [FromBody] SaveFileRequest request) {
            var server = await _context.Servers.FindAsync(serverId);
            if (server == null) return NotFound();

            try {
                await _sftpService.SaveFileContentAsync(server, request.Path, request.Content);
                return Ok();
            }
            catch (System.Exception ex) {
                return BadRequest(ex.Message);
            }
        }

        [HttpPost("{serverId}/mkdir")]
        public async Task<IActionResult> CreateDirectory(int serverId, [FromQuery] string path) {
            var server = await _context.Servers.FindAsync(serverId);
            if (server == null) return NotFound();

            try {
                await _sftpService.CreateDirectoryAsync(server, path);
                return Ok();
            }
            catch (System.Exception ex) {
                return BadRequest(ex.Message);
            }
        }
    }
}