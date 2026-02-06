using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;

namespace CutoverMonitor.Services;

public class LogicAppService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<LogicAppService> _logger;
    private readonly string _subscriptionId;
    private AccessToken? _cachedToken;
    private DateTime _tokenExpiry = DateTime.MinValue;

    public LogicAppService(HttpClient httpClient, ILogger<LogicAppService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _subscriptionId = Environment.GetEnvironmentVariable("AzureSubscriptionId") ?? throw new InvalidOperationException("AzureSubscriptionId not configured");
    }

    private async Task<string> GetAccessTokenAsync()
    {
        if (_cachedToken.HasValue && DateTime.UtcNow < _tokenExpiry.AddMinutes(-5))
        {
            return _cachedToken.Value.Token;
        }

        var credential = new DefaultAzureCredential();
        _cachedToken = await credential.GetTokenAsync(new TokenRequestContext(new[] { "https://management.azure.com/.default" }));
        _tokenExpiry = _cachedToken.Value.ExpiresOn.DateTime;
        
        return _cachedToken.Value.Token;
    }

    private string GetWorkflowUrl(string resourceGroup, string workflowName) =>
        $"https://management.azure.com/subscriptions/{_subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Logic/workflows/{workflowName}";

    public async Task<string?> GetLogicAppStateAsync(string resourceGroup, string workflowName)
    {
        try
        {
            var token = await GetAccessTokenAsync();
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await _httpClient.GetAsync($"{GetWorkflowUrl(resourceGroup, workflowName)}?api-version=2019-05-01");
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("properties").GetProperty("state").GetString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Logic App state for {WorkflowName}", workflowName);
            return null;
        }
    }

    public async Task<bool> SetLogicAppStateAsync(string resourceGroup, string workflowName, string state)
    {
        try
        {
            var token = await GetAccessTokenAsync();
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            // Get current definition
            var getResponse = await _httpClient.GetAsync($"{GetWorkflowUrl(resourceGroup, workflowName)}?api-version=2019-05-01");
            if (!getResponse.IsSuccessStatusCode) return false;

            var json = await getResponse.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Build update body
            var updateBody = new
            {
                location = root.GetProperty("location").GetString(),
                properties = new
                {
                    state = state,
                    definition = root.GetProperty("properties").GetProperty("definition"),
                    parameters = root.GetProperty("properties").GetProperty("parameters"),
                    integrationAccount = new { id = root.GetProperty("properties").GetProperty("integrationAccount").GetProperty("id").GetString() }
                }
            };

            var content = new StringContent(JsonSerializer.Serialize(updateBody), Encoding.UTF8, "application/json");
            var putResponse = await _httpClient.PutAsync($"{GetWorkflowUrl(resourceGroup, workflowName)}?api-version=2019-05-01", content);

            return putResponse.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set Logic App state for {WorkflowName}", workflowName);
            return false;
        }
    }

    public async Task<(int Total, int Failed)> GetRecentRunsAsync(string resourceGroup, string workflowName, int minutesBack = 30)
    {
        try
        {
            var token = await GetAccessTokenAsync();
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var since = DateTime.UtcNow.AddMinutes(-minutesBack).ToString("yyyy-MM-ddTHH:mm:ssZ");
            var url = $"{GetWorkflowUrl(resourceGroup, workflowName)}/runs?api-version=2019-05-01&$filter=startTime ge {since}";

            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode) return (0, 0);

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            
            var runs = doc.RootElement.GetProperty("value");
            int total = 0;
            int failed = 0;

            foreach (var run in runs.EnumerateArray())
            {
                total++;
                var status = run.GetProperty("properties").GetProperty("status").GetString();
                if (status == "Failed") failed++;
            }

            return (total, failed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get runs for {WorkflowName}", workflowName);
            return (0, 0);
        }
    }
}
