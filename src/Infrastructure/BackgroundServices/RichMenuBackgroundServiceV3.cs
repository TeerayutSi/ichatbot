using System.Text;
using System.Text.Json;
using ChatbotApi.Application.Common.Interfaces;
using ChatbotApi.Application.Common.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChatbotApi.Infrastructure.BackgroundServices;

public class RichMenuBackgroundServiceV3 : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<RichMenuBackgroundServiceV3> _logger;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private bool _hasRun = false;

    public RichMenuBackgroundServiceV3(
        IServiceProvider serviceProvider,
        ILogger<RichMenuBackgroundServiceV3> logger,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run immediately but with a small delay to ensure the application is initialized
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        
        // Run only once at startup
        if (!_hasRun)
        {
            try
            {
                _logger.LogInformation("Starting Rich Menu creation process");
                await CreateRichMenuAsync(stoppingToken);
                _hasRun = true;
                _logger.LogInformation("Rich Menu creation process completed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred while creating Rich Menu");
            }
        }
    }

    private async Task CreateRichMenuAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
            
            // Get all chatbots that have LINE channel access tokens
            _logger.LogInformation("Querying database for chatbots with LINE channel access tokens");
            var chatbots = await context.Chatbots
                .Where(c => !string.IsNullOrEmpty(c.LineChannelAccessToken))
                .ToListAsync(stoppingToken);

            _logger.LogInformation("Found {ChatbotCount} chatbots with LINE channel access tokens", chatbots.Count);

            if (chatbots.Count == 0)
            {
                _logger.LogWarning("No chatbots found with LINE channel access tokens. Rich Menu will not be created.");
                return;
            }

            foreach (var chatbot in chatbots)
            {
                try
                {
                    _logger.LogInformation("Creating Rich Menu for chatbot {ChatbotId}", chatbot.Id);
                    await CreateRichMenuForChatbotAsync(chatbot, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error creating Rich Menu for chatbot {ChatbotId}", chatbot.Id);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error querying database for chatbots");
        }
    }

    private async Task CreateRichMenuForChatbotAsync(Domain.Entities.Chatbot chatbot, CancellationToken stoppingToken)
    {
        var accessToken = chatbot.LineChannelAccessToken;
        if (string.IsNullOrEmpty(accessToken))
        {
            _logger.LogWarning("Chatbot {ChatbotId} does not have a LINE channel access token", chatbot.Id);
            return;
        }

        // Validate the access token format (basic validation)
        if (accessToken.Length < 10)
        {
            _logger.LogWarning("Chatbot {ChatbotId} has an invalid LINE channel access token (too short)", chatbot.Id);
            return;
        }

        var httpClient = _httpClientFactory.CreateClient();
        httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");

        // Step 1: Create the Rich Menu
        _logger.LogInformation("Creating Rich Menu for chatbot {ChatbotId}", chatbot.Id);
        var richMenuId = await CreateRichMenuAsync(httpClient, stoppingToken);
        if (string.IsNullOrEmpty(richMenuId))
        {
            _logger.LogError("Failed to create Rich Menu for chatbot {ChatbotId}", chatbot.Id);
            return;
        }

        _logger.LogInformation("Successfully created Rich Menu {RichMenuId} for chatbot {ChatbotId}", richMenuId, chatbot.Id);

        // Step 2: Upload the background image
        var imagePath = _configuration["LineRichMenu:BackgroundImagePath"];
        _logger.LogInformation("Background image path: {ImagePath}", imagePath);
        bool imageUploaded = false;
        
        if (!string.IsNullOrEmpty(imagePath))
        {
            if (File.Exists(imagePath))
            {
                _logger.LogInformation("Uploading background image for Rich Menu {RichMenuId}", richMenuId);
                imageUploaded = await UploadRichMenuImageAsync(httpClient, richMenuId, imagePath, stoppingToken);
            }
            else
            {
                _logger.LogWarning("Background image file does not exist at path: {ImagePath} for chatbot {ChatbotId}", imagePath, chatbot.Id);
            }
        }
        else
        {
            _logger.LogWarning("Background image path is not configured for chatbot {ChatbotId}", chatbot.Id);
        }

        // Step 3: Link the Rich Menu to all users (set as default)
        // Only set as default if image was uploaded successfully or if there's no image to upload
        if (imageUploaded || string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
        {
            _logger.LogInformation("Setting Rich Menu {RichMenuId} as default for all users", richMenuId);
            await SetDefaultRichMenuAsync(httpClient, richMenuId, stoppingToken);
            _logger.LogInformation("Successfully created and linked Rich Menu {RichMenuId} for chatbot {ChatbotId}", richMenuId, chatbot.Id);
        }
        else
        {
            _logger.LogError("Skipping setting Rich Menu {RichMenuId} as default because image upload failed", richMenuId);
        }
    }

    private async Task<string?> CreateRichMenuAsync(HttpClient httpClient, CancellationToken stoppingToken)
    {
        try
        {
            // Create the Rich Menu object with the specified layout
            var richMenu = new LineRichMenu
            {
                Size = new RichMenuSize
                {
                    Width = 2500,
                    Height = 1686
                },
                Selected = _configuration.GetValue<bool>("LineRichMenu:Selected", true),
                Name = _configuration.GetValue<string>("LineRichMenu:Name", "WorkingTimeMenu"),
                ChatBarText = _configuration.GetValue<string>("LineRichMenu:ChatBarText", "เมนูการทำงาน"),
                Areas = new List<RichMenuArea>
                {
                    // Row 1: Registration button (full width)
                    new RichMenuArea
                    {
                        Bounds = new RichMenuBounds
                        {
                            X = 0,
                            Y = 0,
                            Width = 2500,
                            Height = 843
                        },
                        Action = new RichMenuAction
                        {
                            Type = "message",
                            Text = "ลงทะเบียน",
                            Label = "ลงทะเบียน"
                        }
                    },
                    // Row 2: Check-in button (left half)
                    new RichMenuArea
                    {
                        Bounds = new RichMenuBounds
                        {
                            X = 0,
                            Y = 843,
                            Width = 1250,
                            Height = 843
                        },
                        Action = new RichMenuAction
                        {
                            Type = "message",
                            Text = "check-in",
                            Label = "Check-in"
                        }
                    },
                    // Row 2: Check-out button (right half)
                    new RichMenuArea
                    {
                        Bounds = new RichMenuBounds
                        {
                            X = 1250,
                            Y = 843,
                            Width = 1250,
                            Height = 843
                        },
                        Action = new RichMenuAction
                        {
                            Type = "message",
                            Text = "check-out",
                            Label = "Check-out"
                        }
                    }
                }
            };

            var json = JsonSerializer.Serialize(richMenu, new JsonSerializerOptions 
            { 
                WriteIndented = true 
            });
            _logger.LogInformation("Rich Menu JSON: {RichMenuJson}", json);
            
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            _logger.LogInformation("Sending request to create Rich Menu");
            var response = await httpClient.PostAsync("https://api.line.me/v2/bot/richmenu", content, stoppingToken);
            
            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync(stoppingToken);
                _logger.LogInformation("Rich Menu creation response: {ResponseContent}", responseContent);
                
                var responseObject = JsonSerializer.Deserialize<Dictionary<string, string>>(responseContent);
                
                if (responseObject != null && responseObject.ContainsKey("richMenuId"))
                {
                    _logger.LogInformation("Successfully created Rich Menu with ID: {RichMenuId}", responseObject["richMenuId"]);
                    return responseObject["richMenuId"];
                }
                else
                {
                    _logger.LogError("Rich Menu creation response does not contain richMenuId: {ResponseContent}", responseContent);
                }
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync(stoppingToken);
                _logger.LogError("Failed to create Rich Menu. Status: {StatusCode}, Error: {Error}", 
                    response.StatusCode, errorContent);
                
                // Log additional details for debugging
                _logger.LogError("Request URL: {Url}", "https://api.line.me/v2/bot/richmenu");
                _logger.LogError("Request Headers: {Headers}", string.Join(", ", httpClient.DefaultRequestHeaders.Select(h => $"{h.Key}: {string.Join(", ", h.Value)}")));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating Rich Menu");
        }

        return null;
    }

    private async Task<bool> UploadRichMenuImageAsync(HttpClient httpClient, string richMenuId, string imagePath, CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("Reading image file from {ImagePath}", imagePath);
            var fileBytes = await File.ReadAllBytesAsync(imagePath, stoppingToken);
            _logger.LogInformation("Image file read successfully, size: {FileSize} bytes", fileBytes.Length);
            
            // Create multipart form data content without automatic boundary parameter
            var content = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(fileBytes);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            content.Add(fileContent, "image", "richmenu.png");
            
            // Remove the automatically added boundary parameter from Content-Type header
            // LINE API doesn't accept the boundary parameter in the content type
            var contentTypeHeader = content.Headers.ContentType;
            if (contentTypeHeader != null)
            {
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("multipart/form-data");
            }

            _logger.LogInformation("Uploading image to Rich Menu {RichMenuId}", richMenuId);
            var response = await httpClient.PostAsync($"https://api-data.line.me/v2/bot/richmenu/{richMenuId}/content", content, stoppingToken);
            
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Successfully uploaded Rich Menu image");
                return true;
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync(stoppingToken);
                _logger.LogError("Failed to upload Rich Menu image. Status: {StatusCode}, Error: {Error}",
                    response.StatusCode, errorContent);
                
                // Log additional details for debugging
                _logger.LogError("Request URL: {Url}", $"https://api-data.line.me/v2/bot/richmenu/{richMenuId}/content");
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uploading Rich Menu image");
            return false;
        }
    }

    private async Task SetDefaultRichMenuAsync(HttpClient httpClient, string richMenuId, CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("Setting Rich Menu {RichMenuId} as default", richMenuId);
            var response = await httpClient.PostAsync($"https://api.line.me/v2/bot/user/all/richmenu/{richMenuId}", null, stoppingToken);
            
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Successfully set Rich Menu as default");
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync(stoppingToken);
                _logger.LogError("Failed to set default Rich Menu. Status: {StatusCode}, Error: {Error}", 
                    response.StatusCode, errorContent);
                
                // Log additional details for debugging
                _logger.LogError("Request URL: {Url}", $"https://api.line.me/v2/bot/user/all/richmenu/{richMenuId}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting default Rich Menu");
        }
    }
}