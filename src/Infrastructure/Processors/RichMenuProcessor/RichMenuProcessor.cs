using System.Text;
using System.Text.Json;
using ChatbotApi.Application.Common.Extensions;
using ChatbotApi.Application.Common.Interfaces;
using ChatbotApi.Application.Common.Models;
using ChatbotApi.Domain.Constants;
using ChatbotApi.Infrastructure.BackgroundServices;
using ChatbotApi.Infrastructure.Processors.WorkingTimeProcessor;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using LineRichMenu = ChatbotApi.Application.Common.Models.LineRichMenu;
using RichMenuSize = ChatbotApi.Application.Common.Models.RichMenuSize;
using RichMenuArea = ChatbotApi.Application.Common.Models.RichMenuArea;
using RichMenuBounds = ChatbotApi.Application.Common.Models.RichMenuBounds;
using RichMenuAction = ChatbotApi.Application.Common.Models.RichMenuAction;
using DocumentFormat.OpenXml.Drawing.Diagrams;

namespace ChatbotApi.Infrastructure.Processors.RichMenuProcessor;

public class RichMenuProcessor : ILineMessageProcessor
{
    public string Name => Systems.RichMenu;

    private readonly IApplicationDbContext _context;
    private readonly ILogger<RichMenuProcessor> _logger;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly IDistributedCache _distributedCache;
    private readonly ISystemService _systemService;
    private readonly IServiceProvider _serviceProvider;

    public RichMenuProcessor(
        IApplicationDbContext context,
        ILogger<RichMenuProcessor> logger,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        IDistributedCache distributedCache,
        ISystemService systemService,
        IServiceProvider serviceProvider)
    {
        _context = context;
        _logger = logger;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _distributedCache = distributedCache;
        _systemService = systemService;
        _serviceProvider = serviceProvider;
    }
    
    private ILineMessageProcessor GetEmailRegistrationProcessor()
    {
        var processors = _serviceProvider.GetServices<ILineMessageProcessor>();
        var emailRegistrationProcessor = processors.FirstOrDefault(p => p.Name == Systems.EmailRegistration);
        
        // Ensure we found the processor
        if (emailRegistrationProcessor == null)
        {
            throw new InvalidOperationException("EmailRegistrationProcessor not found in registered processors.");
        }
        
        return emailRegistrationProcessor;
    }

    public async Task<LineReplyStatus> ProcessLineAsync(LineEvent evt, int chatbotId, string message, string userId, string replyToken,
        CancellationToken cancellationToken = default)
    {
        // Handle postback events from Rich Menu clicks
        if (evt.Type == "postback" && evt.Postback?.Data != null)
        {
            var postbackData = evt.Postback.Data;
            
            // Handle Rich Menu actions directly without showing reply messages
            if (postbackData == "menu_register")
            {
                // For registration, we process the action without showing reply message
                return await HandleRegisterAction(userId, replyToken, cancellationToken);
            }
            
            if (postbackData == "menu_checkin")
            {
                // Handle check-in action immediately without showing reply message
                return await HandleCheckInAction(userId, replyToken, cancellationToken);
            }
            
            if (postbackData == "menu_checkout")
            {
                // Handle check-out action immediately without showing reply message
                return await HandleCheckOutAction(userId, replyToken, cancellationToken);
            }
            
            if (postbackData == "menu_event")
            {
                // Handle event action immediately without showing reply message
                return await HandleEventAction(userId, replyToken, cancellationToken);
            }
            
            if (postbackData == "menu_calendar")
            {
                // Handle calendar action immediately without showing reply message
                return await HandleCalendarAction(userId, replyToken, cancellationToken);
            }
            
            if (postbackData == "menu_help")
            {
                // Handle help action immediately without showing reply message
                return await HandleHelpAction(userId, replyToken, cancellationToken);
            }
            
            // Handle email registration confirmation
            if (postbackData.StartsWith("confirm_email_registration_"))
            {
                var email = postbackData.Substring("confirm_email_registration_".Length);
                // Clean email: trim whitespace and convert to lowercase
                var cleanEmail = email.Trim().ToLower();
                return await HandleEmailRegistration(cleanEmail, userId, replyToken, cancellationToken);
            }
        }
        
        // Check if message is an email address (for registration)
        if (IsValidEmail(message))
        {
            // Delegate to EmailRegistrationProcessor to process the email input
            // We need to create a minimal LineEvent for the call
            var emailEvent = new LineEvent
            {
                Type = "message",
                Message = new LineEventMessage
                {
                    Type = "text",
                    Text = message
                }
            };
            
            // Call ProcessLineAsync instead of ProcessEmailInputAsync directly
            var emailRegistrationProcessor = GetEmailRegistrationProcessor();
            await emailRegistrationProcessor.ProcessLineAsync(emailEvent, 0, message, userId, "", CancellationToken.None);
            // Return success status without reply message since the processor handles messaging
            return new LineReplyStatus { Status = 204 }; // 204 No Content - successful but no reply
        }
        
        // Handle text messages that should trigger Rich Menu actions
        // Handle Event related messages (case-insensitive for English, exact match for Thai)
        if (string.Equals(message, "Event", StringComparison.OrdinalIgnoreCase) || message == "นัดหมาย" || message == "บันทึกนัดหมาย")
        {
            // Handle event action immediately without showing reply message
            return await HandleEventAction(userId, replyToken, cancellationToken);
        }
        
        // Handle Calendar related messages (case-insensitive for English, exact match for Thai)
        if (string.Equals(message, "Calendar", StringComparison.OrdinalIgnoreCase) || message == "แจ้งตารางงาน" || message == "บันทึกตารางงาน")
        {
            // Handle calendar action immediately without showing reply message
            return await HandleCalendarAction(userId, replyToken, cancellationToken);
        }

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
            await _cache.SetObjectAsync(cacheKey, chatbotId, 5, true, false); // Expire after 5 minutes

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
            await _cache.SetObjectAsync(cacheKey, richMenuId, 5, true, false); // Expire after 5 minutes

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
            await _cache.SetObjectAsync(cacheKey, richMenuId, 5, true, false); // Expire after 5 minutes

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
            var richMenuId = await _cache.GetObjectAsync<string>(cacheKey);
            if (richMenuId == null)
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
            var richMenuId = await _cache.GetObjectAsync<string>(cacheKey);
            if (richMenuId == null)
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
            var cachedChatbotId = await _cache.GetObjectAsync<int>(cacheKey);
            if (cachedChatbotId == 0 || cachedChatbotId != chatbotId)
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
        ""text"": ""โดยคลิกปุ่มยืนยัน หรือพิมพ์ข้อความ {confirmText}"",
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
                    // Row 1: Register button (left)
                    new RichMenuArea
                    {
                        Bounds = new RichMenuBounds
                        {
                            X = 0,
                            Y = 0,
                            Width = 833,
                            Height = 843
                        },
                        Action = new RichMenuAction
                        {
                            Type = "postback",
                            Data = "menu_register",
                            DisplayText = "ลงทะเบียน",
                            Label = "ลงทะเบียน"
                        }
                    },
                    // Row 1: Check-in button (center)
                    new RichMenuArea
                    {
                        Bounds = new RichMenuBounds
                        {
                            X = 834,
                            Y = 0,
                            Width = 833,
                            Height = 843
                        },
                        Action = new RichMenuAction
                        {
                            Type = "postback",
                            DisplayText = "check-in",
                            Label = "Check-in",
                            Data= "menu_checkin"
                        }
                    },
                    // Row 1: Check-out button (right)
                    new RichMenuArea
                    {
                        Bounds = new RichMenuBounds
                        {
                            X = 1667,
                            Y = 0,
                            Width = 833,
                            Height = 843
                        },
                        Action = new RichMenuAction
                        {
                            Type = "postback",
                            DisplayText = "check-out",
                            Label = "Check-out",
                            Data= "menu_checkout"
                        }
                    },
                    // Row 2: Event button (left)
                    new RichMenuArea
                    {
                        Bounds = new RichMenuBounds
                        {
                            X = 0,
                            Y = 843,
                            Width = 833,
                            Height = 843
                        },
                        Action = new RichMenuAction
                        {
                            Type = "postback",
                            DisplayText = "event",
                            Label = "Event",
                            Data= "menu_event"
                        }
                    },
                    // Row 2: calendar button (center)
                    new RichMenuArea
                    {
                        Bounds = new RichMenuBounds
                        {
                            X = 834,
                            Y = 843,
                            Width = 833,
                            Height = 843
                        },
                        Action = new RichMenuAction
                        {
                            Type = "postback",
                            DisplayText = "calendar",
                            Label = "Calendar",
                            Data= "menu_calendar"
                        }
                    },
                    // Row 2: help button (right)
                    new RichMenuArea
                    {
                        Bounds = new RichMenuBounds
                        {
                            X = 1667,
                            Y = 843,
                            Width = 833,
                            Height = 843
                        },
                        Action = new RichMenuAction
                        {
                            Type = "uri",
                            DisplayText = "help",
                            Label = "Help",
                            Uri = "https://www.nti.co.th/devsupport/TAutoBot"
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
    
    private async Task<LineReplyStatus> HandleCheckInAction(string userId, string replyToken, CancellationToken cancellationToken)
    {
        // For immediate action without reply, we return a success status with no reply message
        // In a real implementation, you would perform the check-in action here
        _logger.LogInformation("Processing check-in action for user {UserId}", userId);
        
        // Return success status without reply message
        return new LineReplyStatus { Status = 204 }; // 204 No Content - successful but no reply
    }

    /// <summary>
    /// Checks if a user is registered by calling the SSO API
    /// </summary>
    /// <param name="lineUserId">The LINE user ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if user is registered, false otherwise</returns>
    private async Task<bool> CheckUserRegistration(string lineUserId, CancellationToken cancellationToken)
    {
        try
        {
            // Get API URL and token from configuration
            var baseUrl = _configuration["WorkingTime:SSOApiUrl"];
            var apiToken = _configuration["WorkingTime:SSOApiUrlToken"];
            
            if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(apiToken))
            {
                _logger.LogError("SSO API configuration is missing");
                return false;
            }

            // Construct the full URL with the line user ID
            var registerUrl = $"{baseUrl.TrimEnd('/')}/GetUserid";
            var url = $"{registerUrl}?LineUserId={lineUserId}";
            
            // Create HTTP client
            var httpClient = _httpClientFactory.CreateClient("resilient_nocompress");
            
            // Add authorization header
            httpClient.DefaultRequestHeaders.Add("Authorization", apiToken);
            
            // Make the request
            var response = await httpClient.GetAsync(url, cancellationToken);
            
            // Log the response for debugging
            _logger.LogInformation("SSO API response status: {StatusCode} for user {UserId}", response.StatusCode, lineUserId);
            
            // Return true if we get a 200 OK response, false for 404 or any other status
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking user registration for user {UserId}", lineUserId);
            return false;
        }
    }

    private async Task<string?> GetLineProfileName(string userId, CancellationToken cancellationToken)
    {
        // First, we need to get the chatbot to get the access token
        // For simplicity, we'll try to get any chatbot with a valid access token
        var chatbot = await _context.Chatbots
            .Where(c => !string.IsNullOrEmpty(c.LineChannelAccessToken))
            .FirstOrDefaultAsync(cancellationToken);
            
        if (chatbot == null || string.IsNullOrEmpty(chatbot.LineChannelAccessToken))
        {
            _logger.LogWarning("No chatbot with valid access token found for getting user profile");
            return null;
        }
        
        var client = _httpClientFactory.CreateClient("resilient_nocompress");
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {chatbot.LineChannelAccessToken}");

        var url = $"https://api.line.me/v2/bot/profile/{userId}";
        var lineResponse = await client.GetAsync(url, cancellationToken);

        if (!lineResponse.IsSuccessStatusCode)
        {
            _logger.LogWarning("Failed to get user profile: {StatusCode}", lineResponse.StatusCode);
            return null;
        }

        var lineContent = await lineResponse.Content.ReadAsStringAsync(cancellationToken);
        var json = JsonDocument.Parse(lineContent);
        return json.RootElement.GetProperty("displayName").GetString() ?? string.Empty;
    }
    
    private async Task<LineReplyStatus> HandleCheckOutAction(string userId, string replyToken, CancellationToken cancellationToken)
    {
        // For immediate action without reply, we return a success status with no reply message
        // In a real implementation, you would perform the check-out action here
        _logger.LogInformation("Processing check-out action for user {UserId}", userId);
        
        // Return success status without reply message
        return new LineReplyStatus { Status = 204 }; // 204 No Content - successful but no reply
    }
    
    private async Task<LineReplyStatus> HandleEventAction(string userId, string replyToken, CancellationToken cancellationToken)
    {
        // For immediate action without reply, we return a success status with no reply message
        // In a real implementation, you would perform the event action here
        _logger.LogInformation("Processing event action for user {UserId}", userId);
        
        // Return message indicating the feature is not available yet
        return new LineReplyStatus
        {
            Status = 200,
            ReplyMessage = new LineReplyMessage
            {
                ReplyToken = replyToken,
                Messages = new List<LineMessage>
                {
                    new LineTextMessage { Text = "ยังไม่พร้อมใช้งานในขณะนี้ ฟังก์ชั่นนี้จะได้รับการพัฒนาและเปิดใช้ในอนาคต" }
                }
            }
        };
    }
    
    private async Task<LineReplyStatus> HandleCalendarAction(string userId, string replyToken, CancellationToken cancellationToken)
    {
        // For immediate action without reply, we return a success status with no reply message
        // In a real implementation, you would perform the calendar action here
        _logger.LogInformation("Processing calendar action for user {UserId}", userId);
        
        // Return message indicating the feature is not available yet
        return new LineReplyStatus
        {
            Status = 200,
            ReplyMessage = new LineReplyMessage
            {
                ReplyToken = replyToken,
                Messages = new List<LineMessage>
                {
                    new LineTextMessage { Text = "ยังไม่พร้อมใช้งานในขณะนี้ ฟังก์ชั่นนี้จะได้รับการพัฒนาและเปิดใช้ในอนาคต" }
                }
            }
        };
    }
    
    private async Task<LineReplyStatus> HandleHelpAction(string userId, string replyToken, CancellationToken cancellationToken)
    {
        // For immediate action without reply, we return a success status with no reply message
        // In a real implementation, you would perform the help action here
        _logger.LogInformation("Processing help action for user {UserId}", userId);
        
        // Return success status without reply message
        return new LineReplyStatus { Status = 204 }; // 204 No Content - successful but no reply
    }
    
    private async Task<LineReplyStatus> HandleRegisterAction(string userId, string replyToken, CancellationToken cancellationToken)
    {
        _logger.LogInformation("HandleRegisterAction called for user {UserId}", userId);
        
        // Delegate to EmailRegistrationProcessor to handle the registration flow
        // We need to create a minimal LineEvent for the call with a registration command
        var registerEvent = new LineEvent
        {
            Type = "message",
            Message = new LineEventMessage
            {
                Type = "text",
                Text = "ลงทะเบียน" // Registration command in Thai
            }
        };
        
        // Call ProcessLineAsync instead of HandleRegistrationMenuClickAsync directly
        var emailRegistrationProcessor = GetEmailRegistrationProcessor();
        await emailRegistrationProcessor.ProcessLineAsync(registerEvent, 0, "ลงทะเบียน", userId, "", CancellationToken.None);
        
        _logger.LogInformation("HandleRegisterAction completed for user {UserId}", userId);
        
        // Return success status without reply message since the processor handles messaging
        return new LineReplyStatus { Status = 204 }; // 204 No Content - successful but no reply
    }
    
    /// <summary>
    /// Validates if a string is a valid email address
    /// </summary>
    /// <param name="email">The email string to validate</param>
    /// <returns>True if valid email, false otherwise</returns>
    private bool IsValidEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return false;
            
        // Check if email ends with @nti.co.th
        if (!email.EndsWith("@nti.co.th", StringComparison.OrdinalIgnoreCase))
            return false;
            
        try
        {
            // Use simple regex to validate email format
            var emailRegex = new System.Text.RegularExpressions.Regex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$");
            return emailRegex.IsMatch(email);
        }
        catch
        {
            return false;
        }
    }
    
    /// <summary>
    /// Shows a confirmation flex message for email registration
    /// </summary>
    /// <param name="email">The company email address</param>
    /// <param name="lineUserId">The LINE user ID</param>
    /// <param name="replyToken">The reply token for sending response</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>LineReplyStatus with confirmation flex message</returns>
    private async Task<LineReplyStatus> ShowEmailConfirmation(string email, string lineUserId, string replyToken, CancellationToken cancellationToken)
    {
        // Clean email: trim whitespace and convert to lowercase
        var cleanEmail = email.Trim().ToLower();
        
        // Create a flex message with confirmation button
        var flexMessage = new
        {
            type = "bubble",
            body = new
            {
                type = "box",
                layout = "vertical",
                contents = new object[]
                {
                    new
                    {
                        type = "text",
                        text = "ยืนยันผูก LineId กับอีเมล",
                        weight = "bold",
                        size = "lg",
                        margin = "md"
                    },
                    new
                    {
                        type = "box",
                        layout = "vertical",
                        margin = "sm",
                        contents = new object[]
                        {
                            new
                            {
                                type = "text",
                                text = cleanEmail,
                                wrap = true,
                                color = "#007bff",
                                size = "xl",
                                weight = "bold"
                            }
                        }
                    }
                },
                paddingAll = "20px"
            },
            footer = new
            {
                type = "box",
                layout = "vertical",
                contents = new object[]
                {
                    new
                    {
                        type = "button",
                        action = new
                        {
                            type = "postback",
                            label = "ยืนยัน",
                            data = $"confirm_email_registration_{cleanEmail}"
                        },
                        style = "primary"
                    }
                }
            }
        };

        return new LineReplyStatus
        {
            Status = 201, // Special status for FLEX messages
            Raw = JsonSerializer.Serialize(flexMessage, new JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                WriteIndented = false
            })
        };
    }
    
    /// <summary>
    /// Handles the email registration process
    /// </summary>
    /// <param name="email">The company email address</param>
    /// <param name="lineUserId">The LINE user ID</param>
    /// <param name="replyToken">The reply token for sending response</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>LineReplyStatus with appropriate response</returns>
    private async Task<LineReplyStatus> HandleEmailRegistration(string email, string lineUserId, string replyToken, CancellationToken cancellationToken)
    {
        try
        {
            // Get the SSO API URL and token from configuration
            var baseUrl = _configuration["WorkingTime:SSOApiUrl"];
            var apiToken = _configuration["WorkingTime:SSOApiUrlToken"];
            
            if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(apiToken))
            {
                _logger.LogError("SSO API configuration is missing");
                return new LineReplyStatus
                {
                    Status = 200,
                    ReplyMessage = new LineReplyMessage
                    {
                        ReplyToken = replyToken,
                        Messages = new List<LineMessage>
                        {
                            new LineTextMessage("เกิดข้อผิดพลาดในการเชื่อมต่อกับระบบ กรุณาลองใหม่อีกครั้ง")
                        }
                    }
                };
            }
            
            // Get the base API URL and construct the RegisterLinebotUserId endpoint
            var registerUrl = $"{baseUrl.TrimEnd('/')}/RegisterLinebotUserId";
            
            // Clean email: trim whitespace and convert to lowercase
            var cleanEmail = email.Trim().ToLower();
            
            // Prepare the request data
            var requestData = new
            {
                LinebotUserId = lineUserId,
                Email = cleanEmail
            };
            
            // Create JSON content
            var jsonContent = JsonSerializer.Serialize(requestData);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
            
            // Create HTTP client
            var httpClient = _httpClientFactory.CreateClient("resilient_nocompress");
            
            // Create request message
            var request = new HttpRequestMessage(HttpMethod.Post, registerUrl)
            {
                Content = content
            };
            
            // Add headers
            request.Headers.Add("Authorization", apiToken);
            
            // Make POST request to register the user
            var response = await httpClient.SendAsync(request, cancellationToken);
            
            if (response.IsSuccessStatusCode)
            {
                // Registration successful
                _logger.LogInformation("User {UserId} successfully registered with email {Email}", lineUserId, email);
                
                return new LineReplyStatus
                {
                    Status = 200,
                    ReplyMessage = new LineReplyMessage
    {
        ReplyToken = replyToken,
        Messages = new List<LineMessage>
        {
            new LineTextMessage("ลงทะเบียนเรียบร้อยแล้ว")
        }
    }
                };
            }
            else
            {
                // Registration failed
                _logger.LogError("Failed to register user {UserId} with email {Email}. Status: {StatusCode}",
                    lineUserId, email, response.StatusCode);
                    
                return new LineReplyStatus
                {
                    Status = 200,
                    ReplyMessage = new LineReplyMessage
                    {
                        ReplyToken = replyToken,
                        Messages = new List<LineMessage>
                        {
                            new LineTextMessage("ไม่สามารถลงทะเบียนได้ กรุณาตรวจสอบอีเมลและลองอีกครั้ง")
                        }
                    }
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during email registration for user {UserId}", lineUserId);
            return new LineReplyStatus
            {
                Status = 200,
                ReplyMessage = new LineReplyMessage
                {
                    ReplyToken = replyToken,
                    Messages = new List<LineMessage>
                    {
                        new LineTextMessage("เกิดข้อผิดพลาดในการลงทะเบียน กรุณาลองใหม่อีกครั้ง")
                    }
                }
            };
        }
    }
}