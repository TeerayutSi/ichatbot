using System;
using System.Text;
using System.Text.Json;
using System.Drawing;
using System.Drawing.Imaging;
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
            _logger.LogInformation("AppContext.BaseDirectory: {BaseDirectory}", AppContext.BaseDirectory);
            
            // Resolve relative path to full path
            string fullPath = Path.IsPathRooted(imagePath) ? imagePath : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", imagePath);
            _logger.LogInformation("Resolved image path: {FullPath}", fullPath);
            
            // If the resolved path is still not rooted (e.g., relative path that doesn't resolve properly), try to resolve it differently
            if (!Path.IsPathRooted(fullPath))
            {
                fullPath = Path.GetFullPath(fullPath);
                _logger.LogInformation("Further resolved image path: {FullPath}", fullPath);
            }
            
            // Create composite image if it doesn't exist
            if (!File.Exists(fullPath))
            {
                _logger.LogInformation("Background image does not exist, creating composite image at {ImagePath}", fullPath);
                await CreateCompositeRichMenuImageAsync(fullPath, stoppingToken);
                
                // Check again if the composite image was created successfully
                if (!File.Exists(fullPath))
                {
                    _logger.LogWarning("Failed to create composite image at {ImagePath}", fullPath);
                }
            }
            
            if (File.Exists(fullPath))
            {
                _logger.LogInformation("Uploading background image for Rich Menu {RichMenuId}", richMenuId);
                imageUploaded = await UploadRichMenuImageAsync(httpClient, richMenuId, fullPath, stoppingToken);
            }
            else
            {
                _logger.LogWarning("Background image file does not exist at path: {ImagePath} for chatbot {ChatbotId}", fullPath, chatbot.Id);
            }
        }
        else
        {
            _logger.LogWarning("Background image path is not configured for chatbot {ChatbotId}", chatbot.Id);
        }

        // Step 3: Link the Rich Menu to all users (set as default)
        // Only set as default if image was uploaded successfully
        if (imageUploaded)
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
            _logger.LogInformation("Rich Menu Areas Count: {AreasCount}", richMenu.Areas.Count);
            foreach (var area in richMenu.Areas)
            {
                _logger.LogInformation("Area Bounds: X={X}, Y={Y}, Width={Width}, Height={Height}",
                    area.Bounds.X, area.Bounds.Y, area.Bounds.Width, area.Bounds.Height);
                _logger.LogInformation("Area Action: Type={Type}, Text={Text}, Label={Label}",
                    area.Action.Type, area.Action.Text, area.Action.Label);
            }
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
            if (!File.Exists(imagePath))
            {
                _logger.LogError("Image file does not exist at path: {ImagePath}", imagePath);
                return false;
            }
            
            var fileBytes = await File.ReadAllBytesAsync(imagePath, stoppingToken);
            _logger.LogInformation("Image file read successfully, size: {FileSize} bytes", fileBytes.Length);
            
            // Create ByteArrayContent directly without multipart form data
            var content = new ByteArrayContent(fileBytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            
            // Log the content type for debugging
            _logger.LogInformation("Content-Type header: {ContentType}", content.Headers.ContentType?.ToString() ?? "null");
            _logger.LogInformation("Content-Length: {ContentLength}", content.Headers.ContentLength?.ToString() ?? "null");

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
                _logger.LogError("Response Headers: {Headers}", string.Join(", ", response.Headers.Select(h => $"{h.Key}: {string.Join(", ", h.Value)}")));
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
                _logger.LogError("Response Headers: {Headers}", string.Join(", ", response.Headers.Select(h => $"{h.Key}: {string.Join(", ", h.Value)}")));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting default Rich Menu");
        }
    }

    private async Task CreateCompositeRichMenuImageAsync(string outputPath, CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("Creating composite Rich Menu image at {OutputPath}", outputPath);
            
            // Get the paths to the individual images from configuration or use defaults
            var registerImagePath = _configuration["LineRichMenu:RegisterButtonImagePath"] ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "src", "Asset", "Images", "Register_2500x843(1).png");
            var checkInImagePath = _configuration["LineRichMenu:CheckInButtonImagePath"] ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "src", "Asset", "Images", "Check-in_1250x843(2).png");
            var checkOutImagePath = _configuration["LineRichMenu:CheckOutButtonImagePath"] ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "src", "Asset", "Images", "Check-out_1250x843(2).png");

            _logger.LogInformation("Looking for images at paths: Register={RegisterPath}, CheckIn={CheckInPath}, CheckOut={CheckOutPath}",
                registerImagePath, checkInImagePath, checkOutImagePath);

            // Verify that all required images exist
            if (!File.Exists(registerImagePath) || !File.Exists(checkInImagePath) || !File.Exists(checkOutImagePath))
            {
                _logger.LogWarning("One or more required images are missing for composite image creation");
                if (!File.Exists(registerImagePath))
                    _logger.LogWarning("Register image not found at {Path}", registerImagePath);
                if (!File.Exists(checkInImagePath))
                    _logger.LogWarning("Check-in image not found at {Path}", checkInImagePath);
                if (!File.Exists(checkOutImagePath))
                    _logger.LogWarning("Check-out image not found at {Path}", checkOutImagePath);
                return;
            }

            // Create a new bitmap with the required Rich Menu dimensions (2500x1686)
            using (var compositeImage = new Bitmap(2500, 1686))
            using (var graphics = Graphics.FromImage(compositeImage))
            {
                // Draw the registration image at the top (full width)
                using (var registerImage = Image.FromFile(registerImagePath))
                {
                    graphics.DrawImage(registerImage, new Rectangle(0, 0, 2500, 843));
                }

                // Draw the check-in image in the bottom left
                using (var checkInImage = Image.FromFile(checkInImagePath))
                {
                    graphics.DrawImage(checkInImage, new Rectangle(0, 843, 1250, 843));
                }

                // Draw the check-out image in the bottom right
                using (var checkOutImage = Image.FromFile(checkOutImagePath))
                {
                    graphics.DrawImage(checkOutImage, new Rectangle(1250, 843, 1250, 843));
                }

                // Ensure the directory exists
                var outputDirectory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(outputDirectory) && !Directory.Exists(outputDirectory))
                {
                    Directory.CreateDirectory(outputDirectory);
                }

                // Save the composite image as PNG
                _logger.LogInformation("Saving composite image to {OutputPath}", outputPath);
                compositeImage.Save(outputPath, ImageFormat.Png);
                _logger.LogInformation("Successfully created composite Rich Menu image at {ImagePath}", outputPath);
                
                // Verify the file was created
                if (File.Exists(outputPath))
                {
                    var fileInfo = new FileInfo(outputPath);
                    _logger.LogInformation("Composite image created successfully. File size: {FileSize} bytes", fileInfo.Length);
                }
                else
                {
                    _logger.LogError("Failed to create composite image at {OutputPath}", outputPath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating composite Rich Menu image");
        }
    }
}