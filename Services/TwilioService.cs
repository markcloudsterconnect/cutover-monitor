using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CutoverMonitor.Services;

public class TwilioService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<TwilioService> _logger;
    private readonly string _accountSid;
    private readonly string _authToken;
    private readonly string _messagingServiceSid;
    private readonly string _alertPhoneNumber;

    public TwilioService(HttpClient httpClient, ILogger<TwilioService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _accountSid = Environment.GetEnvironmentVariable("TwilioAccountSid") ?? throw new InvalidOperationException("TwilioAccountSid not configured");
        _authToken = Environment.GetEnvironmentVariable("TwilioAuthToken") ?? throw new InvalidOperationException("TwilioAuthToken not configured");
        _messagingServiceSid = Environment.GetEnvironmentVariable("TwilioMessagingServiceSid") ?? throw new InvalidOperationException("TwilioMessagingServiceSid not configured");
        _alertPhoneNumber = Environment.GetEnvironmentVariable("AlertPhoneNumber") ?? throw new InvalidOperationException("AlertPhoneNumber not configured");
    }

    public async Task<string?> SendSmsAsync(string message)
    {
        try
        {
            var auth = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{_accountSid}:{_authToken}"));
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);

            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("To", _alertPhoneNumber),
                new KeyValuePair<string, string>("MessagingServiceSid", _messagingServiceSid),
                new KeyValuePair<string, string>("Body", message)
            });

            var response = await _httpClient.PostAsync($"https://api.twilio.com/2010-04-01/Accounts/{_accountSid}/Messages.json", content);
            
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var sid = doc.RootElement.GetProperty("sid").GetString();
                _logger.LogInformation("SMS sent successfully: {MessageSid}", sid);
                return sid;
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync();
                _logger.LogError("Failed to send SMS: {Error}", error);
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception sending SMS");
            return null;
        }
    }
}
