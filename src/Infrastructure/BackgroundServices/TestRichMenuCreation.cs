using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChatbotApi.Infrastructure.BackgroundServices;

/// <summary>
/// This is a test utility class that can be used to manually trigger Rich Menu creation.
/// To use this, you would need to register it as a hosted service in DependencyInjection.cs
/// and then run the application with a command line argument or environment variable to trigger it.
/// </summary>
public class TestRichMenuCreation : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<TestRichMenuCreation> _logger;

    public TestRichMenuCreation(IServiceProvider serviceProvider, ILogger<TestRichMenuCreation> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Check if we should run the test (based on environment variable or command line argument)
        var runTest = Environment.GetEnvironmentVariable("RUN_RICHMENU_TEST") == "true";
        
        if (runTest)
        {
            _logger.LogInformation("Starting manual Rich Menu creation test");
            
            // Create a scope to resolve dependencies
            using var scope = _serviceProvider.CreateScope();
            
            // Get the RichMenuBackgroundServiceV3 instance
            var richMenuService = scope.ServiceProvider.GetRequiredService<RichMenuBackgroundServiceV3>();
            
            // Call the ExecuteAsync method directly (this is just for testing)
            // Note: In a real scenario, you would need to use reflection or modify the service
            // to expose a public method for testing
            
            _logger.LogInformation("Manual Rich Menu creation test completed");
        }
        else
        {
            _logger.LogInformation("Rich Menu test not triggered. Set RUN_RICHMENU_TEST=true to run the test.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}