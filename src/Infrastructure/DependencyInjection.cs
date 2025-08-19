using System.Net;
using System.Threading.Channels;
using ChatbotApi.Application.Chatbots.Commands.CreatePreMessageFromFileCommand;
using ChatbotApi.Application.Common.Extensions;
using ChatbotApi.Domain.Entities;
using ChatbotApi.Domain.Models;
using ChatbotApi.Domain.Settings;
using ChatbotApi.Infrastructure.BackgroundServices;
using ChatbotApi.Infrastructure.Data;
using ChatbotApi.Infrastructure.Data.Interceptors;
using ChatbotApi.Infrastructure.Identity;
using ChatbotApi.Infrastructure.Line;
using ChatbotApi.Infrastructure.OpenAI;
using ChatbotApi.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Caching.Distributed;
using Utharn.Library.Localizer;

namespace ChatbotApi.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructureServices(this IServiceCollection services,
        IConfiguration configuration, IWebHostEnvironment environment)
    {
        services.AddScoped<ISaveChangesInterceptor, AuditableEntityInterceptor>();
        services.AddScoped<ISaveChangesInterceptor, DispatchDomainEventsInterceptor>();

        Dictionary<string, string> databaseSettings = configuration.GetSection("Dependencies:DefaultConnection")
            .GetChildren()
            .ToDictionary(x => x.Key, x => x.Value ?? string.Empty);
        foreach (KeyValuePair<string, string> pair in databaseSettings)
        {
            Guard.Against.NullOrEmpty(pair.Value, $"{pair.Key} cannot be empty.");
        }

        string? databaseConnection = string.Join(";", databaseSettings.Select(x => x.Key + "=" + x.Value).ToArray());
        services.AddDbContext<ApplicationDbContext>((sp, options) =>
            {
                options.AddInterceptors(sp.GetServices<ISaveChangesInterceptor>());
                options.UseNpgsql(databaseConnection,
                    b =>
                    {
                        b.UseVector();
                        b.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName);
                        b.UseQuerySplittingBehavior(QuerySplittingBehavior.SingleQuery);
                    });
            })
            .AddDataProtection()
            .PersistKeysToDbContext<ApplicationDbContext>();

        services
            .AddIdentity<ApplicationUser, IdentityRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddDefaultTokenProviders();
        services.AddScoped<UserManager<ApplicationUser>, SimpleUserManager>();

        services.ConfigureApplicationCookie(options =>
        {
            options.AccessDeniedPath = new PathString("/Home/Error");
            options.Cookie.Name = "chatbotApi";
            options.Cookie.HttpOnly = true;
            options.ExpireTimeSpan = TimeSpan.FromHours(24);
            options.LoginPath = new PathString("/Member/Login");
            options.ReturnUrlParameter = CookieAuthenticationDefaults.ReturnUrlParameter;
            options.SlidingExpiration = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
        });
        // Reduce password complexity to lowest
        services.Configure<IdentityOptions>(options =>
        {
            options.Password.RequireDigit = false;
            options.Password.RequiredLength = 1;
            options.Password.RequireLowercase = false;
            options.Password.RequireNonAlphanumeric = false;
            options.Password.RequireUppercase = false;
            options.Password.RequiredUniqueChars = 0;
        });

        services.AddScoped<IApplicationDbContext>(provider => provider.GetRequiredService<ApplicationDbContext>());
        services.AddScoped<ApplicationDbContextInitialiser>();
        services.AddMemoryCache();

        string? redisConnection = configuration.GetConnectionString("Redis");
        Guard.Against.Null(redisConnection, message: "RedisConnection not found.");
        services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = redisConnection;
            options.InstanceName = "chatbotapi";
        });
        services.AddScoped<IOpenAiService, ModelHarborApiService>();
        services.AddScoped<ILineMessenger, LineMessagingApi>();
        // Removed GoogleSheetHelper from DI as it is now instantiated directly where needed.

        // Automatically register all ILineMessageProcessor implementations
        // No need to register each processor type individually
        var processorTypes = typeof(DependencyInjection).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(ILineMessageProcessor).IsAssignableFrom(t));

        foreach (var type in processorTypes)
        {
            // Special handling for RichMenuProcessor to avoid circular dependency
            if (type == typeof(ChatbotApi.Infrastructure.Processors.RichMenuProcessor.RichMenuProcessor))
            {
                services.AddScoped(typeof(ILineMessageProcessor),
                    serviceProvider => new ChatbotApi.Infrastructure.Processors.RichMenuProcessor.RichMenuProcessor(
                        serviceProvider.GetRequiredService<IApplicationDbContext>(),
                        serviceProvider.GetRequiredService<ILogger<ChatbotApi.Infrastructure.Processors.RichMenuProcessor.RichMenuProcessor>>(),
                        serviceProvider.GetRequiredService<IConfiguration>(),
                        serviceProvider.GetRequiredService<IHttpClientFactory>(),
                        serviceProvider.GetRequiredService<IMemoryCache>(),
                        serviceProvider.GetRequiredService<IDistributedCache>(),
                        serviceProvider.GetRequiredService<ISystemService>(),
                        serviceProvider
                    ));
            }
            // Special handling for EmailRegistrationProcessor to provide IMemoryCache
            else if (type == typeof(IChatBot.Infrastructure.Processors.EmailProcessors.EmailRegistrationProcessor))
            {
                services.AddScoped(typeof(ILineMessageProcessor),
                    serviceProvider => new IChatBot.Infrastructure.Processors.EmailProcessors.EmailRegistrationProcessor(
                        serviceProvider.GetRequiredService<IConfiguration>(),
                        serviceProvider.GetRequiredService<IHttpClientFactory>(),
                        serviceProvider.GetRequiredService<ILogger<IChatBot.Infrastructure.Processors.EmailProcessors.EmailRegistrationProcessor>>(),
                        serviceProvider.GetRequiredService<ILineMessenger>(),
                        serviceProvider.GetRequiredService<IApplicationDbContext>(),
                        serviceProvider.GetRequiredService<IMemoryCache>()
                    ));
            }
            else
            {
                services.AddScoped(typeof(ILineMessageProcessor), type);
            }
        }
        
        var facebookMessengerTypes = typeof(DependencyInjection).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IFacebookMessenger).IsAssignableFrom(t));
        foreach (var type in facebookMessengerTypes)
        {
            services.AddScoped(typeof(IFacebookMessenger), type);
        }

        var facebookProcessorTypes = typeof(DependencyInjection).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IFacebookMessengerProcessor).IsAssignableFrom(t));
        foreach (var type in facebookProcessorTypes)
        {
            services.AddScoped(typeof(IFacebookMessengerProcessor), type);
        }

        var openAiProcessorTypes = typeof(DependencyInjection).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IOpenAiMessageProcessor).IsAssignableFrom(t));
        foreach (var type in openAiProcessorTypes)
        {
            services.AddScoped(typeof(IOpenAiMessageProcessor), type);
        }

        var postProcessorTypes = typeof(DependencyInjection).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IPostProcessor).IsAssignableFrom(t));
        foreach (var type in postProcessorTypes)
        {
            services.AddScoped(typeof(IPostProcessor), type);
        }

        // Automatically register all ILineEmailProcessor implementations
        var lineEmailProcessorTypes = typeof(DependencyInjection).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(ILineEmailProcessor).IsAssignableFrom(t));
        foreach (var type in lineEmailProcessorTypes)
        {
            services.AddScoped(typeof(ILineEmailProcessor), type);
        }
        services.AddScoped<IChatCompletion, VectorChatService>();

        var channel = Channel.CreateUnbounded<ProcessFileBackgroundTask>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        services.AddSingleton(_ => channel.Writer);
        services.AddSingleton(_ => channel.Reader);
        services.AddHostedService<ProcessFileBackgroundService>();
        services.AddHostedService<CronRefreshBackgroundService>();

        // Email fetching channel
        var emailChannel = Channel.CreateUnbounded<ObtainedEmail>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = true
        });
        services.AddSingleton<ChannelWriter<ObtainedEmail>>(_ => emailChannel.Writer);
        services.AddSingleton<ChannelReader<ObtainedEmail>>(_ => emailChannel.Reader);
        services.AddHostedService<FetchEmailBackgroundService>();

        // Register EmailConsumerService to process emails using ILineEmailProcessor implementations
        services.AddHostedService<EmailConsumerService>();
        
        // Register TestRichMenuCreation for manual testing (uncomment to enable)
        // services.AddHostedService<TestRichMenuCreation>();
        
        // add AppSetting

        AppSetting? appSetting = configuration.GetSection("AppSettings").Get<AppSetting>();
        Guard.Against.Null(appSetting, message: "AppSetting not found.");
        services.AddSingleton<AppSetting>(appSetting);

        services.AddAuthentication()
            .AddCookie();
        services.AddAuthorizationBuilder();
        services
            .AddIdentityCore<ApplicationUser>()
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddApiEndpoints();

        services.AddSingleton(TimeProvider.System);
        services.AddTransient<ISystemService, SystemService>();

        // Gmail OAuth Configuration
        GmailSettings? gmailSettings = configuration.GetSection("GmailSettings").Get<GmailSettings>();
        if (gmailSettings != null)
        {
            services.Configure<GmailSettings>(configuration.GetSection("GmailSettings"));
            services.AddScoped<IGmailService, Services.GmailService>();
        }

        // Calendar OAuth Configuration
        CalendarSettings? calendarSettings = configuration.GetSection("CalendarSettings").Get<CalendarSettings>();
        if (calendarSettings != null)
        {
            services.Configure<CalendarSettings>(configuration.GetSection("CalendarSettings"));
            services.AddScoped<ICalendarService, Services.GoogleCalendarService>();
        }

        DriveSettings? driveSettings = configuration.GetSection("DriveSettings").Get<DriveSettings>();
        if (driveSettings != null)
        {
            services.Configure<DriveSettings>(configuration.GetSection("DriveSettings"));
            services.AddScoped<IGoogleDriveService, Services.GoogleDriveService>();
        }

        services.RegisterLocalization();
        services.AddHttpClient();
        services.AddHttpClient("resilient", client =>
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "Mozilla/5.0 (Macintosh; Intel Mac OS X 10.15; rv:122.0) Gecko/20100101 Firefox/122.0");
                client.DefaultRequestHeaders.Accept.ParseAdd(
                    "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
                client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
                client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip, deflate, br");
                client.DefaultRequestHeaders.Connection.ParseAdd("keep-alive");
                client.Timeout = TimeSpan.FromMinutes(10);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                ClientCertificateOptions = ClientCertificateOption.Manual,
                ServerCertificateCustomValidationCallback =
                    (httpRequestMessage, cert, cetChain, policyErrors) => true
            })
            .SetHandlerLifetime(TimeSpan.FromMinutes(20))
            .AddPolicyHandler(HttpClientExtension.GetRetryPolicy());

        services.AddHttpClient("resilient_nocompress", client =>
            {
                client.Timeout = TimeSpan.FromMinutes(10);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                ClientCertificateOptions = ClientCertificateOption.Manual,
                ServerCertificateCustomValidationCallback =
                    (httpRequestMessage, cert, cetChain, policyErrors) => true
            })
            .SetHandlerLifetime(TimeSpan.FromMinutes(20))
            .AddPolicyHandler(HttpClientExtension.GetRetryPolicy());

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders =
                ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        });
        return services;
    }
}
