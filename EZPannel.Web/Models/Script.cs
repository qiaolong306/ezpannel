using System;
using System.Collections.Generic;

namespace EZPannel.Api.Models
{
    public class Script
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Content { get; set; }
        public string Type { get; set; } // ps1, bat, sh, py
        public string OS { get; set; } // Windows, Linux, Common
        public int CategoryId { get; set; }
        public ScriptCategory Category { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }

    public class ScriptCategory
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public ICollection<Script> Scripts { get; set; }
    }

    public class ScriptHistory
    {
        public int Id { get; set; }
        public int ScriptId { get; set; }
        public Script Script { get; set; }
        public int ServerId { get; set; }
        public Server Server { get; set; }
        public string Executor { get; set; }
        public DateTime ExecutedAt { get; set; } = DateTime.Now;
        public bool Success { get; set; }
    }
}
