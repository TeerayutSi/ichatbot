using System;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatbotApi.Application.Common.Interfaces;
using ChatbotApi.Application.Common.Models;
using ChatbotApi.Domain.Constants;
using ChatbotApi.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ContentResult = ChatbotApi.Infrastructure.Processors.LLamaPassportProcessor.ContentResult;

namespace ChatbotApi.Infrastructure.Processors.WorkingTimeProcessor;

public class WorkingTimeProcessor : ILineMessageProcessor
{
    public string Name => "WorkingTime";

    private readonly IApplicationDbContext _context;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WorkingTimeProcessor> _logger;
    private readonly IDistributedCache _cache;
    private readonly IConfiguration _configuration;
    private readonly ISystemService _systemService;

    public WorkingTimeProcessor(
        IApplicationDbContext context,
        IHttpClientFactory httpClientFactory,
        ILogger<WorkingTimeProcessor> logger,
        IDistributedCache cache,
        IConfiguration configuration,
        ISystemService systemService)
    {
        _context = context;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _cache = cache;
        _configuration = configuration;
        _systemService = systemService;
    }

    public async Task<LineReplyStatus> ProcessLineAsync(LineEvent evt, int chatbotId, string message, string userId,
        string replyToken, CancellationToken cancellationToken = default)
    {
        Chatbot? chatbot = await _context.Chatbots
            .FirstOrDefaultAsync(c => c.Id == chatbotId, cancellationToken);

        if (chatbot == null || chatbot.LineChannelAccessToken == null)
        {
            _logger.LogError("Chatbot with ID {ChatbotId} not found", chatbotId);
            return new LineReplyStatus { Status = 404 };
        }

        string accessToken = chatbot.LineChannelAccessToken;

        // Check if message is a check-in command
        if (IsCheckInCommand(message))
        {
            return await HandleCheckInCommand(userId, replyToken, WorkingTimeType.CheckIn, accessToken, cancellationToken);
        }

        // Check if message is a check-out command
        if (IsCheckOutCommand(message))
        {
            return await HandleCheckInCommand(userId, replyToken, WorkingTimeType.CheckOut, accessToken, cancellationToken);
        }

        // Check if message is postback from FLEX message (agency selection)
        if (evt.Type == "postback")
        {
            return await HandleAgencySelection(evt, userId, replyToken, cancellationToken);
        }

        // Return 404 for unrecognized messages
        return new LineReplyStatus { Status = 404 };
    }

    public async Task<LineReplyStatus> ProcessLineImageAsync(LineEvent evt, int chatbotId, string messageId, string userId,
        string replyToken, string accessToken, CancellationToken cancellationToken = default)
    {
        // Retrieve session from cache
        var session = await _cache.GetObjectAsync<WorkingTimeSession>($"workingtime_session:{userId}");
        if (session == null)
        {
            return new LineReplyStatus
            {
                Status = 200,
                ReplyMessage = new LineReplyMessage
                {
                    ReplyToken = replyToken,
                    Messages = new List<LineMessage>
                    {
                        new LineTextMessage("ไม่พบเซสชันการทำงาน กรุณาเริ่มใหม่ด้วยคำสั่งเช็คอินหรือเช็คเอาต์")
                    }
                }
            };
        }

        // Check if we're waiting for a photo
        if (session.Step != WorkingTimeStep.WaitingForPhoto)
        {
            return new LineReplyStatus
            {
                Status = 200,
                ReplyMessage = new LineReplyMessage
                {
                    ReplyToken = replyToken,
                    Messages = new List<LineMessage>
                    {
                        new LineTextMessage("ไม่คาดหวังรูปภาพในขณะนี้")
                    }
                }
            };
        }

        // Download image content from LINE
        var content = await GetContentAsync(evt, accessToken, cancellationToken);
        if (content == null || string.IsNullOrEmpty(content.ContentType) || content.Content.Length == 0)
        {
            return new LineReplyStatus
            {
                Status = 200,
                ReplyMessage = new LineReplyMessage
                {
                    ReplyToken = replyToken,
                    Messages = new List<LineMessage>
                    {
                        new LineTextMessage("ไม่สามารถดาวน์โหลดรูปภาพได้ กรุณาลองใหม่อีกครั้ง")
                    }
                }
            };
        }

        // Update session with photo data
        session.PhotoContent = content.Content;
        session.PhotoContentType = content.ContentType;

        // Save updated session
        await _cache.SetObjectAsync($"workingtime_session:{userId}", session, 30, false);

        // Submit data to HR system
        var success = await SubmitToHrSystem(session, userId, cancellationToken);

        // Clear session from cache
        await _cache.RemoveAsync($"workingtime_session:{userId}", cancellationToken);

        // Return success/failure message
        if (success)
        {
            return new LineReplyStatus
            {
                Status = 200,
                ReplyMessage = new LineReplyMessage
                {
                    ReplyToken = replyToken,
                    Messages = new List<LineMessage>
                    {
                        new LineTextMessage($"บันทึก{GetActionText(session.Type)}เรียบร้อยแล้ว")
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
                        new LineTextMessage("ไม่สามารถบันทึกข้อมูลได้ กรุณาลองใหม่อีกครั้ง")
                    }
                }
            };
        }
    }

    public async Task<LineReplyStatus> ProcessLocationAsync(LineEvent evt, int chatbotId, double latitude, double longitude, string? address, string userId, string replyToken, CancellationToken cancellationToken = default)
    {
        // Retrieve session from cache
        var session = await _cache.GetObjectAsync<WorkingTimeSession>($"workingtime_session:{userId}");
        if (session == null)
        {
            return new LineReplyStatus
            {
                Status = 200,
                ReplyMessage = new LineReplyMessage
                {
                    ReplyToken = replyToken,
                    Messages = new List<LineMessage>
                    {
                        new LineTextMessage("ไม่พบเซสชันการทำงาน กรุณาเริ่มใหม่ด้วยคำสั่งเช็คอินหรือเช็คเอาต์")
                    }
                }
            };
        }

        // Update session with location data
        session.Latitude = latitude;
        session.Longitude = longitude;
        session.Address = address;
        session.Step = WorkingTimeStep.WaitingForOfficeSelection;

        // Save updated session
        await _cache.SetObjectAsync($"workingtime_session:{userId}", session, 30, false);

        // Find nearby government offices
        var offices = await FindNearbyOffices(latitude, longitude, cancellationToken);

        if (offices.Count == 0)
        {
            return new LineReplyStatus
            {
                Status = 200,
                ReplyMessage = new LineReplyMessage
                {
                    ReplyToken = replyToken,
                    Messages = new List<LineMessage>
                    {
                        new LineTextMessage("ไม่พบหน่วยงานที่อยู่ใกล้เคียง กรุณาลองใหม่อีกครั้ง")
                    }
                }
            };
        }

        // Create FLEX message with carousel of offices
        var flexMessage = CreateOfficeSelectionFlexMessage(offices);

        return new LineReplyStatus
        {
            Status = 201, // Special status for FLEX messages
            Raw = flexMessage
        };
    }

    #region Helper Methods

    private bool IsCheckInCommand(string message)
    {
        var checkInCommands = new[] { "เช็คอิน", "check-in", "check in", "checkin" };
        return checkInCommands.Contains(message.ToLowerInvariant().Trim());
    }

    private bool IsCheckOutCommand(string message)
    {
        var checkOutCommands = new[] { "เช็คเอาต์", "เช็คเอาท์", "check-out", "check out", "checkout" };
        return checkOutCommands.Contains(message.ToLowerInvariant().Trim());
    }

    private async Task<LineReplyStatus> HandleCheckInCommand(string userId, string replyToken, WorkingTimeType type, string accessToken, CancellationToken cancellationToken)
    {
        // Create new session
        var session = new WorkingTimeSession
        {
            UserId = userId,
            Type = type,
            Step = WorkingTimeStep.WaitingForLocation,
            CreatedAt = DateTime.UtcNow
        };

        // Save session to cache
        await _cache.SetObjectAsync($"workingtime_session:{userId}", session, 30, false);

        // Get user's display name
        string? displayName = await GetLineProfileName(userId, accessToken, cancellationToken);
        string greeting = !string.IsNullOrEmpty(displayName) ? $"👋 สวัสดีคุณ {displayName} " : "";

        // Request location from user
        return new LineReplyStatus
        {
            Status = 200,
            ReplyMessage = new LineReplyMessage
            {
                ReplyToken = replyToken,
                Messages = new List<LineMessage>
                {
                    new LineTextMessage($"{greeting}กรุณาส่งตำแหน่งที่ตั้งของคุณเพื่อ{GetActionText(type)}")
                }
            }
        };
    }

    private string GetActionText(WorkingTimeType type)
    {
        return type == WorkingTimeType.CheckIn ? "เช็คอิน" : "เช็คเอาต์";
    }

    private async Task<LineReplyStatus> HandleAgencySelection(LineEvent evt, string userId, string replyToken, CancellationToken cancellationToken)
    {
        // Retrieve session from cache
        var session = await _cache.GetObjectAsync<WorkingTimeSession>($"workingtime_session:{userId}");
        if (session == null)
        {
            return new LineReplyStatus
            {
                Status = 200,
                ReplyMessage = new LineReplyMessage
                {
                    ReplyToken = replyToken,
                    Messages = new List<LineMessage>
                    {
                        new LineTextMessage("ไม่พบเซสชันการทำงาน กรุณาเริ่มใหม่ด้วยคำสั่งเช็คอินหรือเช็คเอาต์")
                    }
                }
            };
        }

        // Parse postback data to get selected office
        var postbackData = evt.Postback?.Data ?? string.Empty;
        
        // For now, we'll simulate parsing the postback data
        // In a real implementation, you would parse the actual postback data
        var selectedOfficeName = "Selected Office"; // This would come from postback data
        var selectedOfficePlaceId = "selected_place_id"; // This would come from postback data
        var selectedOfficeLatitude = session.Latitude; // This would come from postback data
        var selectedOfficeLongitude = session.Longitude; // This would come from postback data

        // Update session with selected office
        session.SelectedOfficeName = selectedOfficeName;
        session.SelectedOfficePlaceId = selectedOfficePlaceId;
        session.SelectedOfficeLatitude = selectedOfficeLatitude;
        session.SelectedOfficeLongitude = selectedOfficeLongitude;
        session.Step = WorkingTimeStep.WaitingForPhoto;

        // Save updated session
        await _cache.SetObjectAsync($"workingtime_session:{userId}", session, 30, false);

        // Request selfie photo from user
        return new LineReplyStatus
        {
            Status = 200,
            ReplyMessage = new LineReplyMessage
            {
                ReplyToken = replyToken,
                Messages = new List<LineMessage>
                {
                    new LineTextMessage("กรุณาถ่ายรูปเซลฟี่เพื่อยืนยันตัวตน")
                }
            }
        };
    }

    private async Task<ContentResult?> GetContentAsync(LineEvent evt, string accessToken, CancellationToken cancellationToken)
    {
        if (evt.Message?.Id == null)
        {
            _logger.LogError("Event message ID is null in GetContentAsync. Cannot get content");
            return null;
        }

        string messageId = evt.Message.Id;

        HttpClient client = _httpClientFactory.CreateClient("resilient_nocompress");
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");

        string contentUrl = $"https://api-data.line.me/v2/bot/message/{messageId}/content";

        HttpResponseMessage response;
        byte[] contentBytes;
        string? contentType;

        try
        {
            _logger.LogDebug("Fetching content from LINE API for messageId: {MessageId}", messageId);
            response = await client.GetAsync(contentUrl, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                string errorDetail = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError(
                    "Failed to get content from LINE API for messageId {MessageId}. Status: {StatusCode}. Body: {Body}",
                    messageId, response.StatusCode, errorDetail);
                return null;
            }

            contentBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            contentType = response.Content.Headers.ContentType?.MediaType;

            if (string.IsNullOrEmpty(contentType) || contentBytes.Length == 0)
            {
                _logger.LogWarning("LINE API returned empty content or no content type for messageId: {MessageId}",
                    messageId);
                return null;
            }

            _logger.LogDebug(
                "Successfully fetched content ({ContentType}, {ContentLength} bytes) for messageId: {MessageId}",
                contentType, contentBytes.Length, messageId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching content from LINE API for messageId: {MessageId}", messageId);
            return null;
        }

        return new ContentResult { Content = contentBytes, ContentType = contentType };
    }

    private async Task<List<GovernmentOffice>> FindNearbyOffices(double latitude, double longitude, CancellationToken cancellationToken)
    {
        var offices = new List<GovernmentOffice>();

        // Get API key from configuration
        var apiKey = _configuration["WorkingTime:GoogleApiKey"];
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogError("Google API key not configured");
            return offices;
        }

        // Types of places to search for
        var placeTypes = new[] { "government_office", "school", "university", "company" };

        foreach (var type in placeTypes)
        {
            var url = $"https://maps.googleapis.com/maps/api/place/nearbysearch/json?location={latitude},{longitude}&radius=500&type={type}&key={apiKey}";

            try
            {
                var httpClient = _httpClientFactory.CreateClient("resilient_nocompress");
                var response = await httpClient.GetAsync(url, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync(cancellationToken);
                    var placesResponse = JsonSerializer.Deserialize<GooglePlacesResponse>(content);

                    if (placesResponse?.Results != null)
                    {
                        foreach (var result in placesResponse.Results)
                        {
                            // Avoid duplicates
                            if (!offices.Any(o => o.PlaceId == result.PlaceId))
                            {
                                offices.Add(new GovernmentOffice
                                {
                                    Name = result.Name,
                                    PlaceId = result.PlaceId,
                                    Latitude = result.Geometry?.Location?.Latitude ?? 0,
                                    Longitude = result.Geometry?.Location?.Longitude ?? 0
                                });
                            }
                        }
                    }
                }
                else
                {
                    _logger.LogError("Google Places API request failed with status code: {StatusCode}", response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calling Google Places API for type: {Type}", type);
            }
        }

        return offices.Take(10).ToList(); // Limit to 10 results for FLEX message
    }

    private string CreateOfficeSelectionFlexMessage(List<GovernmentOffice> offices)
    {
        // Create a simple FLEX message with a carousel of offices
        var contents = new List<object>();
        
        foreach (var office in offices)
        {
            contents.Add(new
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
                            text = office.Name,
                            weight = "bold",
                            size = "md"
                        },
                        new
                        {
                            type = "text",
                            text = $"Lat: {office.Latitude:F6}, Lng: {office.Longitude:F6}",
                            size = "sm",
                            color = "#666666"
                        }
                    }
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
                                label = "เลือก",
                                data = $"office_selected_{office.PlaceId}"
                            },
                            style = "primary"
                        }
                    }
                }
            });
        }

        var flexMessage = new
        {
            type = "carousel",
            contents = contents.ToArray()
        };

        return JsonSerializer.Serialize(flexMessage, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false
        });
    }

    private async Task<bool> SubmitToHrSystem(WorkingTimeSession session, string userId, CancellationToken cancellationToken)
    {
        // Get API URL from configuration
        var apiUrl = _configuration["WorkingTime:HrSystemApiUrl"];
        if (string.IsNullOrEmpty(apiUrl))
        {
            _logger.LogError("HR System API URL not configured");
            return false;
        }

        // Convert photo to base64
        var base64Photo = session.PhotoContent != null ? Convert.ToBase64String(session.PhotoContent) : string.Empty;
        
        // Determine file extension for the photo
        var fileExtension = "jpg";
        if (!string.IsNullOrEmpty(session.PhotoContentType))
        {
            // Map content type to file extension
            fileExtension = session.PhotoContentType switch
            {
                "image/png" => "png",
                "image/jpeg" => "jpg",
                "image/jpg" => "jpg",
                _ => "jpg"
            };
        }
        
        // Create file name with timestamp
        var fileName = $"checkin_checkout_{DateTime.UtcNow:yyyyMMddHHmmss}.{fileExtension}";

        // Get the actual user ID from the ApplicationUser entity
        // The userId parameter is the LINE OA user ID, but we need the actual user ID
        var actualUserId = userId; // Default to the provided userId
        try
        {
            // Try to find a user whose LineAccessToken matches the provided userId (LINE OA user ID)
            // Using AsNoTracking for better performance since we only need to read data
            var user = await _context.Users
                .AsNoTracking()
                .Where(u => u.LineAccessToken == userId)
                .Select(u => new { u.Id }) // Only select the Id to improve performance
                .FirstOrDefaultAsync(cancellationToken);
            
            // If we found a user with a matching LineAccessToken, use their actual ID
            if (user != null)
            {
                actualUserId = user.Id;
                _logger.LogInformation("Mapped LINE OA user ID {LineUserId} to actual user ID {ActualUserId}", userId, actualUserId);
            }
            else
            {
                _logger.LogWarning("No user found with LineAccessToken matching LINE OA user ID {LineUserId}. Using LINE OA user ID as fallback.", userId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while trying to map LINE OA user ID {LineUserId} to actual user ID. Using provided userId as fallback.", userId);
        }

        // Prepare data for HR System API
        var hrSystemRequest = new HrSystemCheckInCheckOutRequest
        {
            UserId = actualUserId, // Using actual user ID instead of LINE OA user ID
            LatLong = $"{session.SelectedOfficeLatitude ?? session.Latitude ?? 0},{session.SelectedOfficeLongitude ?? session.Longitude ?? 0}", // Latitude Longitude
            Location = session.SelectedOfficeName ?? "Unknown Location", // AgencyName
            IpAddress = "0.0.0.0", // IP address is not available in the session data
            OrganizationName = "NTI", // Organization name
            ProjectName = "NTI Project", // Project name
            FileName = fileName, // generate picture file name
            Base64 = base64Photo // take a photo byte[] > base64
        };

        try
        {
            var httpClient = _httpClientFactory.CreateClient("resilient_nocompress");
            httpClient.DefaultRequestHeaders.Add("accept", "application/json");
            
            // Add Bearer token authentication
            var bearerToken = _configuration["WorkingTime:HrSystemBearerToken"];
            if (!string.IsNullOrEmpty(bearerToken))
            {
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            }

            // Serialize data to JSON
            var json = JsonSerializer.Serialize(hrSystemRequest);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            // Send POST request
            var response = await httpClient.PostAsync(apiUrl, content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Successfully submitted working time data for user {UserId}", actualUserId);
                return true;
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("HR System API request failed with status code: {StatusCode}, content: {Content}",
                    response.StatusCode, errorContent);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error submitting working time data to HR System API for user {UserId}", actualUserId);
            return false;
        }
    }
    private async Task<string?> GetLineProfileName(string userId, string accessToken, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("resilient_nocompress");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", accessToken);

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

    #endregion
}
