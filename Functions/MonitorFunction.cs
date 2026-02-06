using CutoverMonitor.Models;
using CutoverMonitor.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace CutoverMonitor.Functions;

public class MonitorFunction
{
    private readonly ILogger<MonitorFunction> _logger;
    private readonly LogicAppService _logicAppService;
    private readonly TwilioService _twilioService;
    private readonly TableStorageService _tableService;

    public MonitorFunction(
        ILogger<MonitorFunction> logger,
        LogicAppService logicAppService,
        TwilioService twilioService,
        TableStorageService tableService)
    {
        _logger = logger;
        _logicAppService = logicAppService;
        _twilioService = twilioService;
        _tableService = tableService;
    }

    /// <summary>
    /// Timer-triggered monitor that runs every 5 minutes
    /// </summary>
    [Function("MonitorCutovers")]
    public async Task MonitorCutovers([TimerTrigger("0 */5 * * * *")] TimerInfo timer)
    {
        _logger.LogInformation("Cutover monitor triggered at {Time}", DateTime.UtcNow);

        var schedules = await _tableService.GetAllSchedulesAsync();
        
        foreach (var schedule in schedules.Where(s => s.IsActive))
        {
            await CheckCutoverHealthAsync(schedule);
        }

        // Check for scheduled starts/ends
        foreach (var schedule in schedules.Where(s => !s.IsActive && s.ScheduledStart.HasValue))
        {
            if (DateTime.UtcNow >= schedule.ScheduledStart && DateTime.UtcNow < schedule.ScheduledStart.Value.AddMinutes(10))
            {
                await StartCutoverAsync(schedule, "Schedule");
            }
        }

        foreach (var schedule in schedules.Where(s => s.IsActive && s.ScheduledEnd.HasValue))
        {
            if (DateTime.UtcNow >= schedule.ScheduledEnd)
            {
                await EndCutoverAsync(schedule, "Schedule");
            }
        }
    }

    private async Task CheckCutoverHealthAsync(CutoverSchedule schedule)
    {
        _logger.LogInformation("Checking health of {Cutover}", schedule.RowKey);

        // Get v4 state
        var v4State = await _logicAppService.GetLogicAppStateAsync(schedule.ResourceGroup, schedule.V4LogicAppName);
        if (v4State != "Enabled")
        {
            _logger.LogWarning("{Cutover} v4 is not enabled, skipping health check", schedule.RowKey);
            return;
        }

        // Get recent runs
        var (v4Runs, v4Failures) = await _logicAppService.GetRecentRunsAsync(schedule.ResourceGroup, schedule.V4LogicAppName, 30);
        var (failoverRuns, _) = await _logicAppService.GetRecentRunsAsync(schedule.ResourceGroup, schedule.CutoverLogicAppName, 30);

        // Update schedule stats
        schedule.TotalV4Runs += v4Runs;
        schedule.TotalFailures += v4Failures;
        schedule.TotalFailovers += failoverRuns;
        schedule.LastChecked = DateTime.UtcNow;

        var hasIssues = v4Failures > 0 || failoverRuns > 0;
        
        if (hasIssues)
        {
            var issueDetails = $"Failures: {v4Failures}, Failovers: {failoverRuns}";
            schedule.LastError = issueDetails;
            
            _logger.LogWarning("{Cutover} has issues: {Issues}", schedule.RowKey, issueDetails);

            // Check if we should alert (avoid duplicate alerts)
            var shouldAlert = !await _tableService.HasRecentAlertAsync(schedule.RowKey, "Failure", 30);
            
            if (shouldAlert && (v4Failures >= schedule.FailureThreshold || failoverRuns > 0))
            {
                var message = $"CUTOVER ALERT: {schedule.RowKey}\n{issueDetails}";
                var smsId = await _twilioService.SendSmsAsync(message);
                await _tableService.AddAlertAsync(schedule.RowKey, "Failure", message, smsId);
                await _tableService.AddAuditLogAsync(schedule.RowKey, "AlertSent", message, "Monitor");

                // Auto-cutback if enabled
                if (schedule.AutoCutback)
                {
                    await EndCutoverAsync(schedule, "AutoCutback");
                    var cutbackMessage = $"AUTO-CUTBACK: {schedule.RowKey} reverted due to failures";
                    await _twilioService.SendSmsAsync(cutbackMessage);
                }
            }
        }
        else
        {
            schedule.LastError = null;
        }

        await _tableService.UpsertScheduleAsync(schedule);
    }

    private async Task StartCutoverAsync(CutoverSchedule schedule, string triggeredBy)
    {
        _logger.LogInformation("Starting cutover {Cutover} (triggered by {TriggeredBy})", schedule.RowKey, triggeredBy);

        // Enable v4 and cutover
        _ = await _logicAppService.SetLogicAppStateAsync(schedule.ResourceGroup, schedule.V4LogicAppName, "Enabled");
        _ = await _logicAppService.SetLogicAppStateAsync(schedule.ResourceGroup, schedule.CutoverLogicAppName, "Enabled");
        
        // Disable v3
        _ = await _logicAppService.SetLogicAppStateAsync(schedule.ResourceGroup, schedule.V3LogicAppName, "Disabled");

        // Update schedule
        schedule.IsActive = true;
        schedule.ActualStart = DateTime.UtcNow;
        schedule.TotalV4Runs = 0;
        schedule.TotalFailures = 0;
        schedule.TotalFailovers = 0;
        await _tableService.UpsertScheduleAsync(schedule);

        // Log and alert
        await _tableService.AddAuditLogAsync(schedule.RowKey, "CutoverStart", $"Cutover started", triggeredBy);
        
        var message = $"CUTOVER STARTED: {schedule.RowKey}";
        if (schedule.ScheduledEnd.HasValue)
        {
            message += $"\nScheduled end: {schedule.ScheduledEnd:HH:mm}";
        }
        await _twilioService.SendSmsAsync(message);
    }

    private async Task EndCutoverAsync(CutoverSchedule schedule, string triggeredBy)
    {
        _logger.LogInformation("Ending cutover {Cutover} (triggered by {TriggeredBy})", schedule.RowKey, triggeredBy);

        // Disable v4 and cutover
        _ = await _logicAppService.SetLogicAppStateAsync(schedule.ResourceGroup, schedule.V4LogicAppName, "Disabled");
        _ = await _logicAppService.SetLogicAppStateAsync(schedule.ResourceGroup, schedule.CutoverLogicAppName, "Disabled");
        
        // Enable v3
        _ = await _logicAppService.SetLogicAppStateAsync(schedule.ResourceGroup, schedule.V3LogicAppName, "Enabled");

        // Update schedule
        schedule.IsActive = false;
        schedule.ActualEnd = DateTime.UtcNow;
        schedule.ScheduledStart = null;  // Clear schedule after completion
        schedule.ScheduledEnd = null;
        await _tableService.UpsertScheduleAsync(schedule);

        // Log and alert
        var details = $"Runs: {schedule.TotalV4Runs}, Failures: {schedule.TotalFailures}, Failovers: {schedule.TotalFailovers}";
        await _tableService.AddAuditLogAsync(schedule.RowKey, "CutoverEnd", details, triggeredBy);
        
        var message = $"CUTOVER ENDED: {schedule.RowKey}\n{details}";
        await _twilioService.SendSmsAsync(message);
    }
}
