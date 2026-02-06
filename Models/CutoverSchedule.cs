using Azure;
using Azure.Data.Tables;

namespace CutoverMonitor.Models;

/// <summary>
/// Represents a scheduled cutover in Table Storage
/// PartitionKey: "Schedule"
/// RowKey: CutoverName (e.g., "ToryBurch-850")
/// </summary>
public class CutoverSchedule : ITableEntity
{
    public string PartitionKey { get; set; } = "Schedule";
    public string RowKey { get; set; } = string.Empty;  // CutoverName
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }
    
    // Cutover configuration
    public string V4LogicAppName { get; set; } = string.Empty;
    public string V3LogicAppName { get; set; } = string.Empty;
    public string CutoverLogicAppName { get; set; } = string.Empty;
    
    // Schedule
    public bool IsActive { get; set; }
    public DateTime? ScheduledStart { get; set; }
    public DateTime? ScheduledEnd { get; set; }
    public DateTime? ActualStart { get; set; }
    public DateTime? ActualEnd { get; set; }
    
    // Settings
    public bool AutoCutback { get; set; }
    public int FailureThreshold { get; set; } = 1;
    
    // Status tracking
    public int TotalV4Runs { get; set; }
    public int TotalFailures { get; set; }
    public int TotalFailovers { get; set; }
    public DateTime? LastChecked { get; set; }
    public string? LastError { get; set; }
}
