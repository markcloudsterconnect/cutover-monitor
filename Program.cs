using CutoverMonitor.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();
        
        // Register services with typed HttpClient
        services.AddSingleton<TableStorageService>();
        services.AddHttpClient<LogicAppService>();
        services.AddHttpClient<TwilioService>();
    })
    .Build();

host.Run();
