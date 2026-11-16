using System;
using System.Threading.Tasks;
using EZPannel.Api.Data;
using EZPannel.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace EZPannel.Api.Services
{
    public interface ISystemSettingsService
    {
        Task<int> GetDatabaseBackupExpireDaysAsync();
        Task SetDatabaseBackupExpireDaysAsync(int days);
    }

    public class SystemSettingsService : ISystemSettingsService
    {
        public const string DatabaseBackupExpireDaysKey = "DatabaseBackupExpireDays";
        public const int DefaultDatabaseBackupExpireDays = 7;
        public const int MinDatabaseBackupExpireDays = 1;
        public const int MaxDatabaseBackupExpireDays = 3650;

        private readonly AppDbContext _context;

        public SystemSettingsService(AppDbContext context) {
            _context = context;
        }

        public async Task<int> GetDatabaseBackupExpireDaysAsync() {
            var setting = await _context.SystemSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.SettingKey == DatabaseBackupExpireDaysKey);

            if (setting == null || !int.TryParse(setting.Value, out var days)) {
                return DefaultDatabaseBackupExpireDays;
            }
            if (days < MinDatabaseBackupExpireDays || days > MaxDatabaseBackupExpireDays) {
                return DefaultDatabaseBackupExpireDays;
            }
            return days;
        }

        public async Task SetDatabaseBackupExpireDaysAsync(int days) {
            if (days < MinDatabaseBackupExpireDays || days > MaxDatabaseBackupExpireDays) {
                throw new ArgumentOutOfRangeException(nameof(days),
                    $"数据库备份过期天数必须在 {MinDatabaseBackupExpireDays} 到 {MaxDatabaseBackupExpireDays} 之间");
            }

            var setting = await _context.SystemSettings
                .FirstOrDefaultAsync(s => s.SettingKey == DatabaseBackupExpireDaysKey);

            if (setting == null) {
                _context.SystemSettings.Add(new SystemSetting {
                    SettingKey = DatabaseBackupExpireDaysKey,
                    Value = days.ToString(),
                    UpdatedAt = DateTime.Now
                });
            }
            else {
                setting.Value = days.ToString();
                setting.UpdatedAt = DateTime.Now;
            }

            await _context.SaveChangesAsync();
        }
    }
}