using System.Net;
using System.Text.Json;
using CutoverMonitor.Models;
using CutoverMonitor.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CutoverMonitor.Functions;

public class ApiFunction
{
    private readonly ILogger<ApiFunction> _logger;
    private readonly LogicAppService _logicAppService;
    private readonly TwilioService _twilioService;
    private readonly TableStorageService _tableService;

    public ApiFunction(
        ILogger<ApiFunction> logger,
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
    /// GET /api/status - Get status of all cutovers
    /// </summary>
    [Function("GetStatus")]
    public async Task<HttpResponseData> GetStatus(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "status")] HttpRequestData req)
    {
        var schedules = await _tableService.GetAllSchedulesAsync();
        var status = new List<object>();

        foreach (var schedule in schedules)
        {
            var v4State = await _logicAppService.GetLogicAppStateAsync(schedule.ResourceGroup, schedule.V4LogicAppName);
            var v3State = await _logicAppService.GetLogicAppStateAsync(schedule.ResourceGroup, schedule.V3LogicAppName);
            
            status.Add(new
            {
                Name = schedule.RowKey,
                IsActive = schedule.IsActive,
                V4State = v4State,
                V3State = v3State,
                ScheduledStart = schedule.ScheduledStart,
                ScheduledEnd = schedule.ScheduledEnd,
                TotalRuns = schedule.TotalV4Runs,
                TotalFailures = schedule.TotalFailures,
                TotalFailovers = schedule.TotalFailovers,
                LastChecked = schedule.LastChecked,
                LastError = schedule.LastError
            });
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(status);
        return response;
    }

    /// <summary>
    /// POST /api/cutover/{name}/start - Start a cutover
    /// Body: { "durationMinutes": 120, "autoCutback": true }
    /// </summary>
    [Function("StartCutover")]
    public async Task<HttpResponseData> StartCutover(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "cutover/{name}/start")] HttpRequestData req,
        string name)
    {
        var schedule = await _tableService.GetScheduleAsync(name);
        if (schedule == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync($"Cutover '{name}' not found");
            return notFound;
        }

        // Parse request body
        var body = await req.ReadAsStringAsync();
        int durationMinutes = 120;
        bool autoCutback = false;

        if (!string.IsNullOrEmpty(body))
        {
            var options = JsonSerializer.Deserialize<JsonElement>(body);
            if (options.TryGetProperty("durationMinutes", out var duration))
                durationMinutes = duration.GetInt32();
            if (options.TryGetProperty("autoCutback", out var cutback))
                autoCutback = cutback.GetBoolean();
        }

        // Update schedule
        schedule.IsActive = true;
        schedule.ActualStart = DateTime.UtcNow;
        schedule.ScheduledEnd = DateTime.UtcNow.AddMinutes(durationMinutes);
        schedule.AutoCutback = autoCutback;
        schedule.TotalV4Runs = 0;
        schedule.TotalFailures = 0;
        schedule.TotalFailovers = 0;

        // Enable v4, disable v3
        _ = await _logicAppService.SetLogicAppStateAsync(schedule.ResourceGroup, schedule.V4LogicAppName, "Enabled");
        _ = await _logicAppService.SetLogicAppStateAsync(schedule.ResourceGroup, schedule.CutoverLogicAppName, "Enabled");
        _ = await _logicAppService.SetLogicAppStateAsync(schedule.ResourceGroup, schedule.V3LogicAppName, "Disabled");

        await _tableService.UpsertScheduleAsync(schedule);
        await _tableService.AddAuditLogAsync(name, "CutoverStart", $"Duration: {durationMinutes}min, AutoCutback: {autoCutback}", "API");

        var message = $"CUTOVER STARTED: {name}\nEnds: {schedule.ScheduledEnd:HH:mm}";
        await _twilioService.SendSmsAsync(message);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { message = "Cutover started", scheduledEnd = schedule.ScheduledEnd });
        return response;
    }

    /// <summary>
    /// POST /api/cutover/{name}/stop - Stop a cutover
    /// </summary>
    [Function("StopCutover")]
    public async Task<HttpResponseData> StopCutover(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "cutover/{name}/stop")] HttpRequestData req,
        string name)
    {
        var schedule = await _tableService.GetScheduleAsync(name);
        if (schedule == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync($"Cutover '{name}' not found");
            return notFound;
        }

        // Disable v4, enable v3
        _ = await _logicAppService.SetLogicAppStateAsync(schedule.ResourceGroup, schedule.V4LogicAppName, "Disabled");
        _ = await _logicAppService.SetLogicAppStateAsync(schedule.ResourceGroup, schedule.CutoverLogicAppName, "Disabled");
        _ = await _logicAppService.SetLogicAppStateAsync(schedule.ResourceGroup, schedule.V3LogicAppName, "Enabled");

        // Update schedule
        var details = $"Runs: {schedule.TotalV4Runs}, Failures: {schedule.TotalFailures}, Failovers: {schedule.TotalFailovers}";
        schedule.IsActive = false;
        schedule.ActualEnd = DateTime.UtcNow;
        schedule.ScheduledStart = null;
        schedule.ScheduledEnd = null;

        await _tableService.UpsertScheduleAsync(schedule);
        await _tableService.AddAuditLogAsync(name, "CutoverEnd", details, "API");

        var message = $"CUTOVER STOPPED: {name}\n{details}";
        await _twilioService.SendSmsAsync(message);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { message = "Cutover stopped", details });
        return response;
    }

    /// <summary>
    /// PUT /api/cutover/{name} - Create or update a cutover schedule
    /// Body: { "v4": "LogicAppName-v4", "v3": "LogicAppName-v3", "cutover": "LogicAppName-v3cutover", "failureThreshold": 1 }
    /// </summary>
    [Function("UpsertCutover")]
    public async Task<HttpResponseData> UpsertCutover(
        [HttpTrigger(AuthorizationLevel.Function, "put", Route = "cutover/{name}")] HttpRequestData req,
        string name)
    {
        var body = await req.ReadAsStringAsync();
        if (string.IsNullOrEmpty(body))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Request body required");
            return badRequest;
        }

        var options = JsonSerializer.Deserialize<JsonElement>(body);
        
        var schedule = await _tableService.GetScheduleAsync(name) ?? new CutoverSchedule { RowKey = name };
        
        if (options.TryGetProperty("resourceGroup", out var rg))
            schedule.ResourceGroup = rg.GetString() ?? "";
        if (options.TryGetProperty("v4", out var v4))
            schedule.V4LogicAppName = v4.GetString() ?? "";
        if (options.TryGetProperty("v3", out var v3))
            schedule.V3LogicAppName = v3.GetString() ?? "";
        if (options.TryGetProperty("cutover", out var cutover))
            schedule.CutoverLogicAppName = cutover.GetString() ?? "";
        if (options.TryGetProperty("failureThreshold", out var threshold))
            schedule.FailureThreshold = threshold.GetInt32();
        if (options.TryGetProperty("autoCutback", out var autoCutback))
            schedule.AutoCutback = autoCutback.GetBoolean();

        await _tableService.UpsertScheduleAsync(schedule);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(schedule);
        return response;
    }

    /// <summary>
    /// GET /api/audit - Get recent audit logs
    /// </summary>
    [Function("GetAuditLogs")]
    public async Task<HttpResponseData> GetAuditLogs(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "audit")] HttpRequestData req)
    {
        var logs = await _tableService.GetRecentAuditLogsAsync(7);
        
        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(logs);
        return response;
    }

    /// <summary>
    /// POST /api/test/sms - Test SMS sending
    /// </summary>
    [Function("TestSms")]
    public async Task<HttpResponseData> TestSms(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "test/sms")] HttpRequestData req)
    {
        var messageId = await _twilioService.SendSmsAsync($"Test SMS from Cutover Monitor - {DateTime.UtcNow:HH:mm:ss}");
        
        var response = req.CreateResponse(messageId != null ? HttpStatusCode.OK : HttpStatusCode.InternalServerError);
        await response.WriteAsJsonAsync(new { success = messageId != null, messageId });
        return response;
    }

    /// <summary>
    /// POST /api/test/logicapp - Test Logic App state change
    /// Body: { "resourceGroup": "TLI", "workflowName": "TLI-Prd-Toryburch-850-to-cw1-v3", "state": "Enabled" }
    /// </summary>
    [Function("TestLogicApp")]
    public async Task<HttpResponseData> TestLogicApp(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "test/logicapp")] HttpRequestData req)
    {
        var body = await req.ReadAsStringAsync();
        var options = JsonSerializer.Deserialize<JsonElement>(body!);
        
        var rg = options.GetProperty("resourceGroup").GetString()!;
        var workflow = options.GetProperty("workflowName").GetString()!;
        var state = options.GetProperty("state").GetString()!;
        
        _logger.LogInformation("Testing Logic App state change: {RG}/{Workflow} -> {State}", rg, workflow, state);
        
        var currentState = await _logicAppService.GetLogicAppStateAsync(rg, workflow);
        var (success, error) = await _logicAppService.SetLogicAppStateAsync(rg, workflow, state);
        var newState = await _logicAppService.GetLogicAppStateAsync(rg, workflow);
        
        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { 
            currentState, 
            requestedState = state, 
            success, 
            error,
            newState,
            changed = currentState != newState
        });
        return response;
    }
}
