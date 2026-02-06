using Azure.Data.Tables;
using CutoverMonitor.Models;
using Microsoft.Extensions.Logging;

namespace CutoverMonitor.Services;

public class TableStorageService
{
    private readonly TableClient _scheduleTable;
    private readonly TableClient _alertTable;
    private readonly TableClient _auditTable;
    private readonly ILogger<TableStorageService> _logger;

    public TableStorageService(ILogger<TableStorageService> logger)
    {
        _logger = logger;
        var connectionString = Environment.GetEnvironmentVariable("TableStorageConnection") 
            ?? throw new InvalidOperationException("TableStorageConnection not configured");

        var serviceClient = new TableServiceClient(connectionString);
        
        _scheduleTable = serviceClient.GetTableClient("CutoverSchedules");
        _alertTable = serviceClient.GetTableClient("AlertHistory");
        _auditTable = serviceClient.GetTableClient("AuditLog");
        
        // Ensure tables exist
        _scheduleTable.CreateIfNotExists();
        _alertTable.CreateIfNotExists();
        _auditTable.CreateIfNotExists();
    }

    // Schedule operations
    public async Task<List<CutoverSchedule>> GetAllSchedulesAsync()
    {
        var schedules = new List<CutoverSchedule>();
        await foreach (var schedule in _scheduleTable.QueryAsync<CutoverSchedule>(s => s.PartitionKey == "Schedule"))
        {
            schedules.Add(schedule);
        }
        return schedules;
    }

    public async Task<CutoverSchedule?> GetScheduleAsync(string cutoverName)
    {
        try
        {
            return await _scheduleTable.GetEntityAsync<CutoverSchedule>("Schedule", cutoverName);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task UpsertScheduleAsync(CutoverSchedule schedule)
    {
        await _scheduleTable.UpsertEntityAsync(schedule);
    }

    public async Task DeleteScheduleAsync(string cutoverName)
    {
        await _scheduleTable.DeleteEntityAsync("Schedule", cutoverName);
    }

    // Alert history operations
    public async Task<bool> HasRecentAlertAsync(string cutoverName, string alertType, int minutesBack = 30)
    {
        var since = DateTime.UtcNow.AddMinutes(-minutesBack);
        await foreach (var alert in _alertTable.QueryAsync<AlertHistory>(a => 
            a.PartitionKey == cutoverName && 
            a.AlertType == alertType))
        {
            if (alert.Timestamp >= since) return true;
        }
        return false;
    }

    public async Task AddAlertAsync(string cutoverName, string alertType, string message, string? smsMessageId = null)
    {
        var alert = new AlertHistory
        {
            PartitionKey = cutoverName,
            RowKey = $"{alertType}_{DateTime.UtcNow:yyyyMMddHHmmss}",
            AlertType = alertType,
            Message = message,
            SmsSent = smsMessageId != null,
            SmsMessageId = smsMessageId
        };
        await _alertTable.AddEntityAsync(alert);
    }

    // Audit log operations
    public async Task AddAuditLogAsync(string cutoverName, string action, string details, string? triggeredBy = null)
    {
        var log = new AuditLog
        {
            PartitionKey = DateTime.UtcNow.ToString("yyyy-MM-dd"),
            RowKey = $"{DateTime.UtcNow:HHmmss}_{cutoverName}",
            CutoverName = cutoverName,
            Action = action,
            Details = details,
            TriggeredBy = triggeredBy
        };
        await _auditTable.AddEntityAsync(log);
    }

    public async Task<List<AuditLog>> GetRecentAuditLogsAsync(int days = 7)
    {
        var logs = new List<AuditLog>();
        var startDate = DateTime.UtcNow.AddDays(-days).ToString("yyyy-MM-dd");
        
        await foreach (var log in _auditTable.QueryAsync<AuditLog>(l => 
            string.Compare(l.PartitionKey, startDate) >= 0))
        {
            logs.Add(log);
        }
        return logs.OrderByDescending(l => l.PartitionKey + l.RowKey).ToList();
    }
}
