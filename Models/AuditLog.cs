using Azure;
using Azure.Data.Tables;

namespace CutoverMonitor.Models;

/// <summary>
/// Audit log for all cutover actions
/// PartitionKey: Date (yyyy-MM-dd)
/// RowKey: Timestamp_CutoverName (e.g., "143000_ToryBurch-850")
/// </summary>
public class AuditLog : ITableEntity
{
    public string PartitionKey { get; set; } = string.Empty;  // Date
    public string RowKey { get; set; } = string.Empty;        // Timestamp_CutoverName
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }
    
    public string CutoverName { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;  // "CutoverStart", "CutoverEnd", "AutoCutback", "ManualCutback", "FailureDetected", "AlertSent"
    public string Details { get; set; } = string.Empty;
    public string? TriggeredBy { get; set; }  // "Schedule", "Manual", "AutoCutback"
}
