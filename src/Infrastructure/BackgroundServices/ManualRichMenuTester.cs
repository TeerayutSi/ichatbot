using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ChatbotApi.Application.Common.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Drawing;
using System.Drawing.Imaging;

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
            if (!string.IsNullOrEmpty(imagePath))
            {
                // Resolve relative path to full path
                string fullPath = System.IO.Path.IsPathRooted(imagePath) ? imagePath : System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", imagePath);
                
                // If the resolved path is still not rooted (e.g., relative path that doesn't resolve properly), try to resolve it differently
                if (!System.IO.Path.IsPathRooted(fullPath))
                {
                    fullPath = System.IO.Path.GetFullPath(fullPath);
                }
                
                // Create composite image if it doesn't exist
                if (!System.IO.File.Exists(fullPath))
                {
                    _logger.LogInformation("Background image does not exist, creating composite image at {ImagePath}", fullPath);
                    await CreateCompositeRichMenuImageAsync(fullPath);
                    
                    // Check again if the composite image was created successfully
                    if (!System.IO.File.Exists(fullPath))
                    {
                        _logger.LogWarning("Failed to create composite image at {ImagePath}", fullPath);
                    }
                }
                
                if (System.IO.File.Exists(fullPath))
                {
                    await UploadRichMenuImageAsync(richMenuId, fullPath);
                }
                else
                {
                    _logger.LogWarning("Background image file does not exist at path: {ImagePath}", fullPath);
                }
            }
            else
            {
                _logger.LogWarning("Background image path is not configured");
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

            _logger.LogInformation("Rich Menu JSON: {RichMenuJson}", json);
            _logger.LogInformation("Rich Menu Areas Count: {AreasCount}", richMenu.Areas.Count);
            foreach (var area in richMenu.Areas)
            {
                _logger.LogInformation("Area Bounds: X={X}, Y={Y}, Width={Width}, Height={Height}",
                    area.Bounds.X, area.Bounds.Y, area.Bounds.Width, area.Bounds.Height);
                _logger.LogInformation("Area Action: Type={Type}, Text={Text}, Label={Label}",
                    area.Action.Type, area.Action.Text, area.Action.Label);
            }
            
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
            _logger.LogInformation("Reading image file from {ImagePath}", imagePath);
            if (!System.IO.File.Exists(imagePath))
            {
                _logger.LogError("Image file does not exist at path: {ImagePath}", imagePath);
                return;
            }
            
            var fileBytes = await System.IO.File.ReadAllBytesAsync(imagePath);
            _logger.LogInformation("Image file read successfully, size: {FileSize} bytes", fileBytes.Length);
            
            // Create ByteArrayContent directly without multipart form data
            var content = new ByteArrayContent(fileBytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            
            // Log the content type for debugging
            _logger.LogInformation("Content-Type header: {ContentType}", content.Headers.ContentType?.ToString() ?? "null");
            _logger.LogInformation("Content-Length: {ContentLength}", content.Headers.ContentLength?.ToString() ?? "null");

            var response = await _httpClient.PostAsync($"https://api-data.line.me/v2/bot/richmenu/{richMenuId}/content", content);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError("Failed to upload Rich Menu image. Status: {StatusCode}, Error: {Error}",
                    response.StatusCode, errorContent);
                
                // Log additional details for debugging
                _logger.LogError("Request URL: {Url}", $"https://api-data.line.me/v2/bot/richmenu/{richMenuId}/content");
                _logger.LogError("Response Headers: {Headers}", string.Join(", ", response.Headers.Select(h => $"{h.Key}: {string.Join(", ", h.Value)}")));
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
    
    private async Task CreateCompositeRichMenuImageAsync(string outputPath)
    {
        try
        {
            _logger.LogInformation("Creating composite Rich Menu image at {OutputPath}", outputPath);
            
            // Get the paths to the individual images from configuration or use defaults
            var registerImagePath = _configuration["LineRichMenu:RegisterButtonImagePath"] ?? System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "src", "Asset", "Images", "Register_2500x843(1).png");
            var checkInImagePath = _configuration["LineRichMenu:CheckInButtonImagePath"] ?? System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "src", "Asset", "Images", "Check-in_1250x843(2).png");
            var checkOutImagePath = _configuration["LineRichMenu:CheckOutButtonImagePath"] ?? System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "src", "Asset", "Images", "Check-out_1250x843(2).png");

            _logger.LogInformation("Looking for images at paths: Register={RegisterPath}, CheckIn={CheckInPath}, CheckOut={CheckOutPath}",
                registerImagePath, checkInImagePath, checkOutImagePath);

            // Verify that all required images exist
            if (!System.IO.File.Exists(registerImagePath) || !System.IO.File.Exists(checkInImagePath) || !System.IO.File.Exists(checkOutImagePath))
            {
                _logger.LogWarning("One or more required images are missing for composite image creation");
                if (!System.IO.File.Exists(registerImagePath))
                    _logger.LogWarning("Register image not found at {Path}", registerImagePath);
                if (!System.IO.File.Exists(checkInImagePath))
                    _logger.LogWarning("Check-in image not found at {Path}", checkInImagePath);
                if (!System.IO.File.Exists(checkOutImagePath))
                    _logger.LogWarning("Check-out image not found at {Path}", checkOutImagePath);
                return;
            }

            // Create a new bitmap with the required Rich Menu dimensions (2500x1686)
            using (var compositeImage = new System.Drawing.Bitmap(2500, 1686))
            using (var graphics = System.Drawing.Graphics.FromImage(compositeImage))
            {
                // Draw the registration image at the top (full width)
                using (var registerImage = System.Drawing.Image.FromFile(registerImagePath))
                {
                    graphics.DrawImage(registerImage, new System.Drawing.Rectangle(0, 0, 2500, 843));
                }

                // Draw the check-in image in the bottom left
                using (var checkInImage = System.Drawing.Image.FromFile(checkInImagePath))
                {
                    graphics.DrawImage(checkInImage, new System.Drawing.Rectangle(0, 843, 1250, 843));
                }

                // Draw the check-out image in the bottom right
                using (var checkOutImage = System.Drawing.Image.FromFile(checkOutImagePath))
                {
                    graphics.DrawImage(checkOutImage, new System.Drawing.Rectangle(1250, 843, 1250, 843));
                }

                // Ensure the directory exists
                var outputDirectory = System.IO.Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(outputDirectory) && !System.IO.Directory.Exists(outputDirectory))
                {
                    System.IO.Directory.CreateDirectory(outputDirectory);
                }

                // Save the composite image as PNG
                _logger.LogInformation("Saving composite image to {OutputPath}", outputPath);
                compositeImage.Save(outputPath, System.Drawing.Imaging.ImageFormat.Png);
                _logger.LogInformation("Successfully created composite Rich Menu image at {ImagePath}", outputPath);
                
                // Verify the file was created
                if (System.IO.File.Exists(outputPath))
                {
                    var fileInfo = new System.IO.FileInfo(outputPath);
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
                
                // Log additional details for debugging
                _logger.LogError("Request URL: {Url}", $"https://api.line.me/v2/bot/user/all/richmenu/{richMenuId}");
                _logger.LogError("Response Headers: {Headers}", string.Join(", ", response.Headers.Select(h => $"{h.Key}: {string.Join(", ", h.Value)}")));
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