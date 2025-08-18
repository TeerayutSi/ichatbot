using System.Text;
using System.Text.Json;
using ChatbotApi.Application.Common.Interfaces;
using ChatbotApi.Application.Common.Models;
using ChatbotApi.Domain.Constants;
using ChatbotApi.Infrastructure.BackgroundServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using LineRichMenu = ChatbotApi.Application.Common.Models.LineRichMenu;
using RichMenuSize = ChatbotApi.Application.Common.Models.RichMenuSize;
using RichMenuArea = ChatbotApi.Application.Common.Models.RichMenuArea;
using RichMenuBounds = ChatbotApi.Application.Common.Models.RichMenuBounds;
using RichMenuAction = ChatbotApi.Application.Common.Models.RichMenuAction;

namespace ChatbotApi.Infrastructure.Processors.RichMenuProcessor;

public class RichMenuProcessor : ILineMessageProcessor
{
    public string Name => Systems.RichMenu;

    private readonly IApplicationDbContext _context;
    private readonly ILogger<RichMenuProcessor> _logger;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;

    public RichMenuProcessor(
        IApplicationDbContext context,
        ILogger<RichMenuProcessor> logger,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache)
    {
        _context = context;
        _logger = logger;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _cache = cache;
    }

    public async Task<LineReplyStatus> ProcessLineAsync(LineEvent evt, int chatbotId, string message, string userId, string replyToken,
        CancellationToken cancellationToken = default)
    {
        // Handle "#สร้างเมนู" command
        if (message == "#สร้างเมนู")
        {
            return await HandleCreateMenuCommand(chatbotId, replyToken, userId, cancellationToken);
        }

        // Handle "#แก้ไขเมนู:{richMenuId}" command
        if (message.StartsWith("#แก้ไขเมนู:"))
        {
            var richMenuId = message.Substring("#แก้ไขเมนู:".Length);
            return await HandleEditMenuCommand(chatbotId, richMenuId, replyToken, userId, cancellationToken);
        }

        // Handle "#ลบเมนู:{richMenuId}" command
        if (message.StartsWith("#ลบเมนู:"))
        {
            var richMenuId = message.Substring("#ลบเมนู:".Length);
            return await HandleDeleteMenuCommand(chatbotId, richMenuId, replyToken, userId, cancellationToken);
        }

        // Handle confirmation messages for edit/delete
        if (message == "ยืนยันการแก้ไขเมนู")
        {
            return await HandleEditConfirmation(chatbotId, userId, replyToken, cancellationToken);
        }

        if (message == "ยืนยันการลบเมนู")
        {
            return await HandleDeleteConfirmation(chatbotId, userId, replyToken, cancellationToken);
        }

        // Handle confirmation for menu creation
        if (message == "ยืนยันการสร้างเมนู")
        {
            return await HandleCreateConfirmation(chatbotId, userId, replyToken, cancellationToken);
        }

        return new LineReplyStatus { Status = 404 };
    }

    private async Task<LineReplyStatus> HandleCreateMenuCommand(int chatbotId, string replyToken, string userId, CancellationToken cancellationToken)
    {
        try
        {
            var chatbot = await _context.Chatbots
                .Where(c => c.Id == chatbotId && !string.IsNullOrEmpty(c.LineChannelAccessToken))
                .FirstOrDefaultAsync(cancellationToken);

            if (chatbot == null)
            {
                _logger.LogWarning("Chatbot {ChatbotId} not found or does not have LINE channel access token", chatbotId);
                return new LineReplyStatus { Status = 404 };
            }

            // Store chatbotId in cache to indicate that menu creation is pending confirmation
            // Use a unique key combining chatbotId and userId to support multiple users
            var cacheKey = $"richmenu_create_{chatbotId}_{userId}";
            _cache.Set(cacheKey, chatbotId, TimeSpan.FromMinutes(5)); // Expire after 5 minutes
            
            // Create and send confirmation flex message
            var flexMessage = CreateConfirmationFlexMessage("สร้าง", "new");
            
            return new LineReplyStatus
            {
                Status = 201,
                Raw = flexMessage
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling create menu command for chatbot {ChatbotId}", chatbotId);
            return new LineReplyStatus { Status = 404 };
        }
    }

    private async Task<LineReplyStatus> HandleEditMenuCommand(int chatbotId, string richMenuId, string replyToken, string userId, CancellationToken cancellationToken)
    {
        try
        {
            // Get the chatbot to get the access token
            var chatbot = await _context.Chatbots
                .Where(c => c.Id == chatbotId && !string.IsNullOrEmpty(c.LineChannelAccessToken))
                .FirstOrDefaultAsync(cancellationToken);

            if (chatbot == null)
            {
                _logger.LogWarning("Chatbot {ChatbotId} not found or does not have LINE channel access token", chatbotId);
                return new LineReplyStatus { Status = 404 };
            }

            // Check if the rich menu exists
            var exists = await CheckRichMenuExistsAsync(chatbot.LineChannelAccessToken, richMenuId, cancellationToken);
            if (!exists)
            {
                return new LineReplyStatus
                {
                    Status = 200,
                    ReplyMessage = new LineReplyMessage
                    {
                        ReplyToken = replyToken,
                        Messages = new List<LineMessage>
                        {
                            new LineTextMessage { Text = "ไม่พบ Rich Menu ที่ระบุ" }
                        }
                    }
                };
            }

            // Store the rich menu ID in cache for confirmation
            // Use a unique key combining chatbotId and userId to support multiple users
            var cacheKey = $"richmenu_edit_{chatbotId}_{userId}";
            _cache.Set(cacheKey, richMenuId, TimeSpan.FromMinutes(5)); // Expire after 5 minutes
            
            var flexMessage = CreateConfirmationFlexMessage("แก้ไข", richMenuId);
            
            return new LineReplyStatus
            {
                Status = 201,
                Raw = flexMessage
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling edit menu command for chatbot {ChatbotId}", chatbotId);
            return new LineReplyStatus { Status = 404 };
        }
    }

    private async Task<LineReplyStatus> HandleDeleteMenuCommand(int chatbotId, string richMenuId, string replyToken, string userId, CancellationToken cancellationToken)
    {
        try
        {
            // Get the chatbot to get the access token
            var chatbot = await _context.Chatbots
                .Where(c => c.Id == chatbotId && !string.IsNullOrEmpty(c.LineChannelAccessToken))
                .FirstOrDefaultAsync(cancellationToken);

            if (chatbot == null)
            {
                _logger.LogWarning("Chatbot {ChatbotId} not found or does not have LINE channel access token", chatbotId);
                return new LineReplyStatus { Status = 404 };
            }

            // Check if the rich menu exists
            var exists = await CheckRichMenuExistsAsync(chatbot.LineChannelAccessToken, richMenuId, cancellationToken);
            if (!exists)
            {
                return new LineReplyStatus
                {
                    Status = 200,
                    ReplyMessage = new LineReplyMessage
                    {
                        ReplyToken = replyToken,
                        Messages = new List<LineMessage>
                        {
                            new LineTextMessage { Text = "ไม่พบ Rich Menu ที่ระบุ" }
                        }
                    }
                };
            }

            // Store the rich menu ID in cache for confirmation
            // Use a unique key combining chatbotId and userId to support multiple users
            var cacheKey = $"richmenu_delete_{chatbotId}_{userId}";
            _cache.Set(cacheKey, richMenuId, TimeSpan.FromMinutes(5)); // Expire after 5 minutes
            
            var flexMessage = CreateConfirmationFlexMessage("ลบ", richMenuId);
            
            return new LineReplyStatus
            {
                Status = 201,
                Raw = flexMessage
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling delete menu command for chatbot {ChatbotId}", chatbotId);
            return new LineReplyStatus { Status = 404 };
        }
    }

    private async Task<bool> CheckRichMenuExistsAsync(string accessToken, string richMenuId, CancellationToken cancellationToken)
    {
        try
        {
            var httpClient = _httpClientFactory.CreateClient();
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");

            var response = await httpClient.GetAsync($"https://api.line.me/v2/bot/richmenu/{richMenuId}", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking if Rich Menu {RichMenuId} exists", richMenuId);
            return false;
        }
    }

    private async Task<LineReplyStatus> HandleEditConfirmation(int chatbotId, string userId, string replyToken, CancellationToken cancellationToken)
    {
        try
        {
            // Get the chatbot to get the access token
            var chatbot = await _context.Chatbots
                .Where(c => c.Id == chatbotId && !string.IsNullOrEmpty(c.LineChannelAccessToken))
                .FirstOrDefaultAsync(cancellationToken);

            if (chatbot == null)
            {
                _logger.LogWarning("Chatbot {ChatbotId} not found or does not have LINE channel access token", chatbotId);
                return new LineReplyStatus
                {
                    Status = 200,
                    ReplyMessage = new LineReplyMessage
                    {
                        ReplyToken = replyToken,
                        Messages = new List<LineMessage>
                        {
                            new LineTextMessage { Text = "ไม่พบข้อมูล chatbot" }
                        }
                    }
                };
            }

            // Retrieve the richMenuId from cache
            var cacheKey = $"richmenu_edit_{chatbotId}_{userId}";
            if (!_cache.TryGetValue(cacheKey, out string richMenuId))
            {
                return new LineReplyStatus
                {
                    Status = 200,
                    ReplyMessage = new LineReplyMessage
                    {
                        ReplyToken = replyToken,
                        Messages = new List<LineMessage>
                        {
                            new LineTextMessage { Text = "ไม่พบข้อมูล Rich Menu ที่ต้องการแก้ไข" }
                        }
                    }
                };
            }

            // Remove the cache entry as we've retrieved it
            _cache.Remove(cacheKey);

            // Perform the edit operation - create a new rich menu with the same configuration
            var result = await CreateRichMenuForChatbotAsync(chatbot, cancellationToken);
            
            if (result)
            {
                // Try to delete the old rich menu
                await DeleteRichMenuAsync(chatbot.LineChannelAccessToken, richMenuId, cancellationToken);
                
                return new LineReplyStatus
                {
                    Status = 200,
                    ReplyMessage = new LineReplyMessage
                    {
                        ReplyToken = replyToken,
                        Messages = new List<LineMessage>
                        {
                            new LineTextMessage { Text = "ดำเนินการแก้ไขเมนูเรียบร้อยแล้ว" }
                        }
                    }
                };
            }
            else
            {
                return new LineReplyStatus
                {
                    Status = 200,
                    ReplyMessage = new LineReplyMessage
                    {
                        ReplyToken = replyToken,
                        Messages = new List<LineMessage>
                        {
                            new LineTextMessage { Text = "ไม่สามารถแก้ไขเมนูได้" }
                        }
                    }
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling edit confirmation for chatbot {ChatbotId}", chatbotId);
            return new LineReplyStatus
            {
                Status = 200,
                ReplyMessage = new LineReplyMessage
                {
                    ReplyToken = replyToken,
                    Messages = new List<LineMessage>
                    {
                        new LineTextMessage { Text = "เกิดข้อผิดพลาดในการแก้ไขเมนู" }
                    }
                }
            };
        }
    }

    private async Task<LineReplyStatus> HandleDeleteConfirmation(int chatbotId, string userId, string replyToken, CancellationToken cancellationToken)
    {
        try
        {
            // Get the chatbot to get the access token
            var chatbot = await _context.Chatbots
                .Where(c => c.Id == chatbotId && !string.IsNullOrEmpty(c.LineChannelAccessToken))
                .FirstOrDefaultAsync(cancellationToken);

            if (chatbot == null)
            {
                _logger.LogWarning("Chatbot {ChatbotId} not found or does not have LINE channel access token", chatbotId);
                return new LineReplyStatus
                {
                    Status = 200,
                    ReplyMessage = new LineReplyMessage
                    {
                        ReplyToken = replyToken,
                        Messages = new List<LineMessage>
                        {
                            new LineTextMessage { Text = "ไม่พบข้อมูล chatbot" }
                        }
                    }
                };
            }

            // Retrieve the richMenuId from cache
            var cacheKey = $"richmenu_delete_{chatbotId}_{userId}";
            if (!_cache.TryGetValue(cacheKey, out string richMenuId))
            {
                return new LineReplyStatus
                {
                    Status = 200,
                    ReplyMessage = new LineReplyMessage
                    {
                        ReplyToken = replyToken,
                        Messages = new List<LineMessage>
                        {
                            new LineTextMessage { Text = "ไม่พบข้อมูล Rich Menu ที่ต้องการลบ" }
                        }
                    }
                };
            }

            // Remove the cache entry as we've retrieved it
            _cache.Remove(cacheKey);

            // Perform the delete operation
            var result = await DeleteRichMenuAsync(chatbot.LineChannelAccessToken, richMenuId, cancellationToken);
            
            if (result)
            {
                return new LineReplyStatus
                {
                    Status = 200,
                    ReplyMessage = new LineReplyMessage
                    {
                        ReplyToken = replyToken,
                        Messages = new List<LineMessage>
                        {
                            new LineTextMessage { Text = "ดำเนินการลบเมนูเรียบร้อยแล้ว" }
                        }
                    }
                };
            }
            else
            {
                return new LineReplyStatus
                {
                    Status = 200,
                    ReplyMessage = new LineReplyMessage
                    {
                        ReplyToken = replyToken,
                        Messages = new List<LineMessage>
                        {
                            new LineTextMessage { Text = "ไม่สามารถลบเมนูได้" }
                        }
                    }
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling delete confirmation for chatbot {ChatbotId}", chatbotId);
            return new LineReplyStatus
            {
                Status = 200,
                ReplyMessage = new LineReplyMessage
                {
                    ReplyToken = replyToken,
                    Messages = new List<LineMessage>
                    {
                        new LineTextMessage { Text = "เกิดข้อผิดพลาดในการลบเมนู" }
                    }
                }
            };
        }
    }

    private async Task<LineReplyStatus> HandleCreateConfirmation(int chatbotId, string userId, string replyToken, CancellationToken cancellationToken)
    {
        try
        {
            // Get the chatbot to get the access token
            var chatbot = await _context.Chatbots
                .Where(c => c.Id == chatbotId && !string.IsNullOrEmpty(c.LineChannelAccessToken))
                .FirstOrDefaultAsync(cancellationToken);

            if (chatbot == null)
            {
                _logger.LogWarning("Chatbot {ChatbotId} not found or does not have LINE channel access token", chatbotId);
                return new LineReplyStatus
                {
                    Status = 200,
                    ReplyMessage = new LineReplyMessage
                    {
                        ReplyToken = replyToken,
                        Messages = new List<LineMessage>
                        {
                            new LineTextMessage { Text = "ไม่พบข้อมูล chatbot" }
                        }
                    }
                };
            }

            // Check if creation was pending confirmation
            var cacheKey = $"richmenu_create_{chatbotId}_{userId}";
            if (!_cache.TryGetValue(cacheKey, out int cachedChatbotId) || cachedChatbotId != chatbotId)
            {
                return new LineReplyStatus
                {
                    Status = 200,
                    ReplyMessage = new LineReplyMessage
                    {
                        ReplyToken = replyToken,
                        Messages = new List<LineMessage>
                        {
                            new LineTextMessage { Text = "ไม่พบคำขอสร้างเมนูที่รอดำเนินการ" }
                        }
                    }
                };
            }

            // Remove the cache entry as we've retrieved it
            _cache.Remove(cacheKey);

            // Perform the creation operation
            var result = await CreateRichMenuForChatbotAsync(chatbot, cancellationToken);
            
            if (result)
            {
                return new LineReplyStatus
                {
                    Status = 200,
                    ReplyMessage = new LineReplyMessage
                    {
                        ReplyToken = replyToken,
                        Messages = new List<LineMessage>
                        {
                            new LineTextMessage { Text = "สร้างเมนูเรียบร้อยแล้ว" }
                        }
                    }
                };
            }
            else
            {
                return new LineReplyStatus
                {
                    Status = 200,
                    ReplyMessage = new LineReplyMessage
                    {
                        ReplyToken = replyToken,
                        Messages = new List<LineMessage>
                        {
                            new LineTextMessage { Text = "ไม่สามารถสร้างเมนูได้" }
                        }
                    }
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling create confirmation for chatbot {ChatbotId}", chatbotId);
            return new LineReplyStatus
            {
                Status = 200,
                ReplyMessage = new LineReplyMessage
                {
                    ReplyToken = replyToken,
                    Messages = new List<LineMessage>
                    {
                        new LineTextMessage { Text = "เกิดข้อผิดพลาดในการสร้างเมนู" }
                    }
                }
            };
        }
    }

    private string CreateConfirmationFlexMessage(string action, string richMenuId)
    {
        // Special handling for "สร้าง" action to use the correct confirmation message
        string confirmText = action == "สร้าง" ? "ยืนยันการสร้างเมนู" : $"ยืนยันการ{action}เมนู";
        
        var json = $@"{{
  ""type"": ""bubble"",
  ""body"": {{
    ""type"": ""box"",
    ""layout"": ""vertical"",
    ""contents"": [
      {{
        ""type"": ""text"",
        ""text"": ""ยืนยันการ{action} Rich menu"",
        ""weight"": ""bold"",
        ""size"": ""md""
      }},
      {{
        ""type"": ""text"",
        ""text"": ""โดยคลิกปุ่ม<b>ยืนยัน</b> หรือพิมพ์ข้อความ <b>{confirmText}</b>"",
        ""wrap"": true,
        ""size"": ""sm"",
        ""margin"": ""md""
      }}
    ]
  }},
  ""footer"": {{
    ""type"": ""box"",
    ""layout"": ""vertical"",
    ""contents"": [
      {{
        ""type"": ""button"",
        ""action"": {{
          ""type"": ""message"",
          ""label"": ""ยืนยัน"",
          ""text"": ""{confirmText}""
        }},
        ""style"": ""primary""
      }}
    ]
  }}
}}";
        return json;
    }

    private async Task<bool> DeleteRichMenuAsync(string accessToken, string richMenuId, CancellationToken cancellationToken)
    {
        try
        {
            var httpClient = _httpClientFactory.CreateClient();
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");

            var response = await httpClient.DeleteAsync($"https://api.line.me/v2/bot/richmenu/{richMenuId}", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting Rich Menu {RichMenuId}", richMenuId);
            return false;
        }
    }

    // Simplified version of the rich menu creation logic from RichMenuBackgroundServiceV3
    private async Task<bool> CreateRichMenuForChatbotAsync(Domain.Entities.Chatbot chatbot, CancellationToken cancellationToken)
    {
        try
        {
            var accessToken = chatbot.LineChannelAccessToken;
            if (string.IsNullOrEmpty(accessToken))
            {
                _logger.LogWarning("Chatbot {ChatbotId} does not have a LINE channel access token", chatbot.Id);
                return false;
            }

            var httpClient = _httpClientFactory.CreateClient();
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");

            // Step 1: Create the Rich Menu
            _logger.LogInformation("Creating Rich Menu for chatbot {ChatbotId}", chatbot.Id);
            var richMenuId = await CreateRichMenuAsync(httpClient, cancellationToken);
            if (string.IsNullOrEmpty(richMenuId))
            {
                _logger.LogError("Failed to create Rich Menu for chatbot {ChatbotId}", chatbot.Id);
                return false;
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

                if (File.Exists(fullPath))
                {
                    _logger.LogInformation("Uploading background image for Rich Menu {RichMenuId}", richMenuId);
                    imageUploaded = await UploadRichMenuImageAsync(httpClient, richMenuId, fullPath, cancellationToken);
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
                await SetDefaultRichMenuAsync(httpClient, richMenuId, cancellationToken);
                _logger.LogInformation("Successfully linked Rich Menu {RichMenuId} for chatbot {ChatbotId}", richMenuId, chatbot.Id);
                return true;
            }
            else
            {
                _logger.LogError("Skipping setting Rich Menu {RichMenuId} as default because image upload failed", richMenuId);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating Rich Menu for chatbot {ChatbotId}", chatbot.Id);
            return false;
        }
    }

    private async Task<string?> CreateRichMenuAsync(HttpClient httpClient, CancellationToken cancellationToken)
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
            var response = await httpClient.PostAsync("https://api.line.me/v2/bot/richmenu", content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
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
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
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

    private async Task<bool> UploadRichMenuImageAsync(HttpClient httpClient, string richMenuId, string imagePath, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Reading image file from {ImagePath}", imagePath);
            if (!File.Exists(imagePath))
            {
                _logger.LogError("Image file does not exist at path: {ImagePath}", imagePath);
                return false;
            }

            var fileBytes = await File.ReadAllBytesAsync(imagePath, cancellationToken);
            _logger.LogInformation("Image file read successfully, size: {FileSize} bytes", fileBytes.Length);

            // Create ByteArrayContent directly without multipart form data
            var content = new ByteArrayContent(fileBytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");

            _logger.LogInformation("Uploading image to Rich Menu {RichMenuId}", richMenuId);
            var response = await httpClient.PostAsync($"https://api-data.line.me/v2/bot/richmenu/{richMenuId}/content", content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Successfully uploaded Rich Menu image");
                return true;
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Failed to upload Rich Menu image. Status: {StatusCode}, Error: {Error}",
                    response.StatusCode, errorContent);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uploading Rich Menu image");
            return false;
        }
    }

    private async Task SetDefaultRichMenuAsync(HttpClient httpClient, string richMenuId, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Setting Rich Menu {RichMenuId} as default", richMenuId);
            var response = await httpClient.PostAsync($"https://api.line.me/v2/bot/user/all/richmenu/{richMenuId}", null, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Successfully set Rich Menu as default");
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Failed to set default Rich Menu. Status: {StatusCode}, Error: {Error}",
                    response.StatusCode, errorContent);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting default Rich Menu");
        }
    }

    public Task<LineReplyStatus> ProcessLineImageAsync(LineEvent evt, int chatbotId, string messageId, string userId,
        string replyToken, string accessToken, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new LineReplyStatus { Status = 404 });
    }
}