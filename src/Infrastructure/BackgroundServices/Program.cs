using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace ChatbotApi.Infrastructure.BackgroundServices;

/// <summary>
/// A simple console application to manually test Rich Menu creation.
/// </summary>
public class Program
{
    public static async Task Main(string[] args)
    {
        // Setup configuration
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddJsonFile($"appsettings.Development.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables()
            .Build();

        // Setup logging
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Debug);
        });
        
        services.AddSingleton<IConfiguration>(configuration);
        
        var serviceProvider = services.BuildServiceProvider();
        var logger = serviceProvider.GetRequiredService<ILogger<Program>>();

        // Get LINE channel access token from user input or environment variable
        string? accessToken = Environment.GetEnvironmentVariable("LINE_CHANNEL_ACCESS_TOKEN");
        
        if (string.IsNullOrEmpty(accessToken))
        {
            Console.WriteLine("Please enter your LINE channel access token:");
            accessToken = Console.ReadLine();
        }

        if (string.IsNullOrEmpty(accessToken))
        {
            logger.LogError("LINE channel access token is required");
            return;
        }

        // Create and run the manual tester
        var tester = new ManualRichMenuTester(configuration, serviceProvider.GetRequiredService<ILogger<ManualRichMenuTester>>());
        await tester.TestRichMenuCreationAsync(accessToken);

        Console.WriteLine("Press any key to exit...");
        Console.ReadKey();
    }
}