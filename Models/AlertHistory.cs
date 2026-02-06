using Azure;
using Azure.Data.Tables;

namespace CutoverMonitor.Models;

/// <summary>
/// Tracks sent alerts to avoid duplicates
/// PartitionKey: CutoverName
/// RowKey: AlertType_Timestamp (e.g., "Failure_20260206143000")
/// </summary>
public class AlertHistory : ITableEntity
{
    public string PartitionKey { get; set; } = string.Empty;  // CutoverName
    public string RowKey { get; set; } = string.Empty;        // AlertType_Timestamp
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }
    
    public string AlertType { get; set; } = string.Empty;  // "Failure", "Cutback", "ScheduleStart", "ScheduleEnd"
    public string Message { get; set; } = string.Empty;
    public bool SmsSent { get; set; }
    public string? SmsMessageId { get; set; }
}
