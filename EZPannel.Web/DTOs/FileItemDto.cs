using System;

namespace EZPannel.Api.DTOs
{
    public class FileItemDto
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public long Size { get; set; }
        public bool IsDirectory { get; set; }
        public DateTime LastModified { get; set; }
        public string Permissions { get; set; }
    }
}
