using Microsoft.EntityFrameworkCore;
using EZPannel.Api.Models;

namespace EZPannel.Api.Data
{
    public class AppDbContext : DbContext {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
        public DbSet<User> Users { get; set; }
        public DbSet<Server> Servers { get; set; }
        public DbSet<Script> Scripts { get; set; }
        public DbSet<ScriptCategory> ScriptCategories { get; set; }
        public DbSet<ScriptHistory> ScriptHistories { get; set; }
        public DbSet<Database> Databases { get; set; }
        public DbSet<SavedQuery> SavedQueries { get; set; }
        public DbSet<DatabaseBackupConfig> DatabaseBackupConfigs { get; set; }
        public DbSet<BackupLog> BackupLogs { get; set; }
        public DbSet<ConfigModel> Configs { get;  set; }
        public DbSet<ScriptAutoRunModel> ScriptAutoRuns { get; set; }
    }
}