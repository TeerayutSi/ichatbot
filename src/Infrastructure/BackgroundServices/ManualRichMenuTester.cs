using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ChatbotApi.Application.Common.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChatbotApi.Infrastructure.BackgroundServices;

/// <summary>
/// A manual tester utility that can be used to create Rich Menu without running the full application.
/// This can be used for testing purposes by running it as a separate console application.
/// </summary>
public class ManualRichMenuTester
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<ManualRichMenuTester> _logger;
    private readonly HttpClient _httpClient;

    public ManualRichMenuTester(IConfiguration configuration, ILogger<ManualRichMenuTester> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _httpClient = new HttpClient();
    }

    public async Task TestRichMenuCreationAsync(string accessToken)
    {
        try
        {
            _logger.LogInformation("Starting manual Rich Menu creation test");
            
            // Set the authorization header
            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");

            // Step 1: Create the Rich Menu
            var richMenuId = await CreateRichMenuAsync();
            if (string.IsNullOrEmpty(richMenuId))
            {
                _logger.LogError("Failed to create Rich Menu");
                return;
            }

            _logger.LogInformation("Successfully created Rich Menu: {RichMenuId}", richMenuId);

            // Step 2: Upload the background image
            var imagePath = _configuration["LineRichMenu:BackgroundImagePath"];
            if (!string.IsNullOrEmpty(imagePath) && System.IO.File.Exists(imagePath))
            {
                await UploadRichMenuImageAsync(richMenuId, imagePath);
            }
            else
            {
                _logger.LogWarning("Background image path is not configured or file does not exist");
            }

            // Step 3: Link the Rich Menu to all users (set as default)
            await SetDefaultRichMenuAsync(richMenuId);

            _logger.LogInformation("Manual Rich Menu creation test completed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during manual Rich Menu creation test");
        }
    }

    private async Task<string?> CreateRichMenuAsync()
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
                Areas = new System.Collections.Generic.List<RichMenuArea>
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

            var json = JsonSerializer.Serialize(richMenu);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("https://api.line.me/v2/bot/richmenu", content);
            
            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                var responseObject = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, string>>(responseContent);
                
                if (responseObject != null && responseObject.ContainsKey("richMenuId"))
                {
                    return responseObject["richMenuId"];
                }
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError("Failed to create Rich Menu. Status: {StatusCode}, Error: {Error}", 
                    response.StatusCode, errorContent);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating Rich Menu");
        }

        return null;
    }

    private async Task UploadRichMenuImageAsync(string richMenuId, string imagePath)
    {
        try
        {
            var fileBytes = await System.IO.File.ReadAllBytesAsync(imagePath);
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

            var response = await _httpClient.PostAsync($"https://api-data.line.me/v2/bot/richmenu/{richMenuId}/content", content);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError("Failed to upload Rich Menu image. Status: {StatusCode}, Error: {Error}", 
                    response.StatusCode, errorContent);
            }
            else
            {
                _logger.LogInformation("Successfully uploaded Rich Menu image");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uploading Rich Menu image");
        }
    }

    private async Task SetDefaultRichMenuAsync(string richMenuId)
    {
        try
        {
            var response = await _httpClient.PostAsync($"https://api.line.me/v2/bot/user/all/richmenu/{richMenuId}", null);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError("Failed to set default Rich Menu. Status: {StatusCode}, Error: {Error}", 
                    response.StatusCode, errorContent);
            }
            else
            {
                _logger.LogInformation("Successfully set Rich Menu as default");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting default Rich Menu");
        }
    }
}