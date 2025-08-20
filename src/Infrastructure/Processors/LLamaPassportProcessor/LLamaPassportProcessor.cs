using System.Globalization;
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
using LineReplyStatus = ChatbotApi.Application.Common.Models.LineReplyStatus;
using LineMessage = ChatbotApi.Application.Common.Models.LineMessage;
using LineTextMessage = ChatbotApi.Application.Common.Models.LineTextMessage;
using LineReplyMessage = ChatbotApi.Application.Common.Models.LineReplyMessage;

namespace ChatbotApi.Infrastructure.Processors.LLamaPassportProcessor;

public class LLamaPassportProcessor : ILineMessageProcessor, IProcessorCancellationHandler
{
    public string Name => Systems.LlamaPassport;

    private readonly IApplicationDbContext _context;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LLamaPassportProcessor> _logger;
    private readonly LLamaPassportGoogleSheetHelper _googleSheetHelper;
    private readonly IDistributedCache _cache;

    private const string OpenRouterApiUrl = "https://openrouter.ai/api/v1/chat/completions";
    private const string OpenRouterModel = "meta-llama/llama-4-scout";

    private const string
        spreadsheetId = "1fo25PBVPeSrvhLpqUL6CNYr4BFHNjCGH0a0CXSBm1hs";

    private const string range = "Sheet1!A:N";

    public static string PassportPrompt =
        "OCR the information from the document only if it contains the word 'Passport' or 'Country Code' or 'Nationality' in the image. " +
        "If any of 'Passport', 'Country Code', 'Nationality' is not present, all the values are null." +
        "countryCode content 3 character represent abbreviate of the country name" +
        "dateOfBirth is in DD/MM/YYYY format." +
        "fullName is the holder full name. fullName is always all uppercase." +
        "nationality is the holder nationality and is a country name. nationality is always all uppercase." +
        "if nationality is not clear and not found, nationality is the country name according to countryCode" +
        "passportNumber contains 2 uppercase letters followed by 7 digits with no space" +
        "sex can be either M or F. sex information is located close to date of birth." +
        "Strict JSON structure is required." +
        "{\"countryCode\":\"\",\"dateOfBirth\":\"\",\"fullName\":\"\",\"nationality\":\"\",\"passportNumber\":\"\",\"sex\":\"\"}";

    public static string AddressPrompt = "ทำการ OCR ข้อมูลจากเอกสารและให้ผลลัพธ์เป็น JSON object" +
                                         "หมายเลขโทรศัพท์ต้องมี 10 หลัก หากข้อมูลคือหมายเลขโทรศัพท์ จะต้องไม่เป็นส่วนหนึ่งของที่อยู่" +
                                         "บางครั้งอาจมีเพียงที่อยู่หรือหมายเลขโทรศัพท์เท่านั้น อย่าพยายามให้หมายเลขโทรศัพท์หากไม่มีระบุไว้" +
                                         "ที่อยู่ในประเทศไทย ให้แยกที่อยู่ตามที่ปรากฏ ไม่ต้องเปลี่ยนแปลงหรือแปล ลบที่อยู่ภาษาอังกฤษออก" +
                                         "ที่อยู่ต้องมีข้อมูลอย่างน้อย ตำบล (ต.) หรือ แขวง, อำเภอ (อ.) หรือ เขต, และจังหวัด อาจมีรหัสไปรษณีย์ บ้านเลขที่ ซอย หมู่บ้าน ถนนก็ได้" +
                                         "หากข้อมูลที่อยู่มีไม่ครบ ให้คืนค่าว่าง คุณเพียงแค่รวมข้อมูลที่อยู่ทั้งหมดเข้าด้วยกันเป็นสตริงเดียว" +
                                         "ต้องใช้โครงสร้าง JSON ที่เข้มงวด" +
                                         "{\"address\":\"\",\"telephone\":\"\"}";

    private static readonly string OpenRouterApiKey = "sk-**";
    public LLamaPassportProcessor(IApplicationDbContext context, IHttpClientFactory httpClientFactory,
        ILogger<LLamaPassportProcessor> logger, IConfiguration configuration, IDistributedCache cache)
    {
        _context = context;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _googleSheetHelper = new LLamaPassportGoogleSheetHelper(logger);
        _cache = cache;
    }

    public async Task<LineReplyStatus> ProcessLineAsync(LineEvent evt, int chatbotId, string message, string userId,
        string replyToken, CancellationToken cancellationToken = default)
    {
        // Original logic for processing images/text if not the save command
        Chatbot? chatbot = await _context.Chatbots
            .FirstOrDefaultAsync(c => c.Id == chatbotId, cancellationToken);

        if (chatbot == null || chatbot.LineChannelAccessToken == null)
        {
            _logger.LogError("Chatbot with ID {ChatbotId} not found", chatbotId);
            return new LineReplyStatus { Status = 404 };
        }

        LLamaPassportResult? currentPassport = null;
        if (message == "สถานะ")
        {
            currentPassport = await _cache.GetObjectAsync<LLamaPassportResult>($"passport_result:{userId}");
            if (currentPassport != null)
            {
                var formattedMessage = BuildPassportDisplayMessage(currentPassport);
                return new LineReplyStatus()
                {
                    Status = 200,
                    ReplyMessage = new LineReplyMessage()
                    {
                        ReplyToken = replyToken,
                        Messages = [new LineTextMessage(formattedMessage.ToString())]
                    }
                };
            }

            return new LineReplyStatus()
            {
                Status = 200,
                ReplyMessage = new LineReplyMessage()
                {
                    ReplyToken = replyToken,
                    Messages = [new LineTextMessage("ยังไม่มีข้อมูลหนังสือเดินทาง")]
                }
            };
        }

        if (message == "บันทึก")
        {
            currentPassport = await _cache.GetObjectAsync<LLamaPassportResult>($"passport_result:{userId}");
            if (currentPassport != null && currentPassport.FullName != null &&
                currentPassport.PassportNumber != null)
            {
                string? profileName =
                    await GetLineProfileName(userId, chatbot.LineChannelAccessToken, cancellationToken);

                var rowData = new List<object>
                {
                    DateTime.UtcNow.AddHours(7)
                        .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.GetCultureInfo("th-TH")),
                    profileName ?? "",
                    currentPassport.FullName ?? "-",
                    currentPassport.PassportNumber ?? "-",
                    currentPassport.Nationality ?? "-",
                    currentPassport.CountryCode ?? "-",
                    currentPassport.Sex ?? "-",
                    currentPassport.DateOfBirth ?? "-",
                    currentPassport.Address ?? "-",
                    currentPassport.Telephone == null ? "-" : "'" + currentPassport.Telephone,
                };

                await _googleSheetHelper.AppendRowAsync(spreadsheetId, range, rowData, cancellationToken);
                var formattedMessage = new StringBuilder(BuildPassportDisplayMessage(currentPassport));
                formattedMessage.AppendLine("ข้อมูลถูกบันทึกลง Google Sheet เรียบร้อยแล้ว");

                await _cache.RemoveAsync($"passport_result:{userId}", cancellationToken);
                await _cache.RemoveAsync($"passport_state:{userId}", cancellationToken);

                return new LineReplyStatus()
                {
                    Status = 200,
                    ReplyMessage = new LineReplyMessage()
                    {
                        ReplyToken = replyToken,
                        Messages = [new LineTextMessage(formattedMessage.ToString())]
                    }
                };
            }

            return new LineReplyStatus()
            {
                Status = 200,
                ReplyMessage = new LineReplyMessage()
                {
                    ReplyToken = replyToken,
                    Messages = [new LineTextMessage("ไม่พบข้อมูลหนังสือเดินทางที่ต้องการบันทึก")]
                }
            };
        }

        LineMessage messageResult = await CallOpenRouterApiAndProcessResponseAsync(
            type: 1,
            evt: evt,
            userId: userId, // Pass userId
            accessToken: chatbot.LineChannelAccessToken,
            cancellationToken: cancellationToken
        );

        return new LineReplyStatus()
        {
            Status = 200,
            ReplyMessage = new LineReplyMessage()
            {
                ReplyToken = replyToken,
                Messages = new List<LineMessage> { messageResult }
            }
        };
    }

    public async Task<LineReplyStatus> ProcessLineImageAsync(LineEvent evt, int chatbotId, string messageId,
        string userId,
        string replyToken, string accessToken, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("LLamaPassportProcessor starting single image processing for messageId: {MessageId}",
            messageId);

        Chatbot? chatbot = await _context.Chatbots
            .FirstOrDefaultAsync(c => c.Id == chatbotId, cancellationToken);

        LineMessage messageResult = await CallOpenRouterApiAndProcessResponseAsync(
            type: 0,
            evt: evt,
            userId: userId, // Pass userId
            accessToken: accessToken,
            cancellationToken: cancellationToken
        );

        return new LineReplyStatus()
        {
            Status = 200,
            ReplyMessage = new LineReplyMessage()
            {
                ReplyToken = replyToken,
                Messages = new List<LineMessage> { messageResult }
            }
        };
    }

    private async Task<ContentResult?> GetContentAsync(LineEvent evt, string accessToken,
        CancellationToken cancellationToken)
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

    private string BuildPassportDisplayMessage(LLamaPassportResult passportResult)
    {
        var formattedMessage = new StringBuilder();
        formattedMessage.AppendLine("ข้อมูลหนังสือเดินทาง:");
        formattedMessage.AppendLine($"ชื่อเต็ม: {passportResult.FullName ?? "-"}");
        formattedMessage.AppendLine($"หมายเลขหนังสือเดินทาง: {passportResult.PassportNumber ?? "-"}");
        formattedMessage.AppendLine($"สัญชาติ: {passportResult.Nationality ?? "-"}");
        formattedMessage.AppendLine($"ประเทศ: {passportResult.CountryCode ?? "-"}");
        formattedMessage.AppendLine($"เพศ: {passportResult.Sex ?? "-"}");
        formattedMessage.AppendLine($"วันเกิด: {passportResult.DateOfBirth ?? "-"}");
        formattedMessage.AppendLine($"ที่อยู่: {passportResult.Address ?? "-"}");
        formattedMessage.AppendLine($"หมายเลขโทรศัพท์: {passportResult.Telephone ?? "-"}");
        return formattedMessage.ToString();
    }

    private async Task<LineMessage> CallOpenRouterApiAndProcessResponseAsync(int type, LineEvent evt, string userId,
        string accessToken, CancellationToken cancellationToken)
    {
        if (evt.Message?.Id == null)
        {
            _logger.LogError("Event message ID is null for one image in the set. Cannot process");
            return new LineTextMessage { Text = "ข้อผิดพลาด: ไม่พบ ID รูปภาพ" };
        }

        string messageId = evt.Message.Id;
        string? messageText = evt.Message.Text;

        ContentResult? content = null;

        PassportState passportState = PassportState.NoPassport;
        var stateString = await _cache.GetStringAsync($"passport_state:{userId}", cancellationToken);
        if (stateString != null)
        {
            passportState = (PassportState)Enum.Parse(typeof(PassportState), stateString);
        }

        string? imageDataUrl = null;
        switch (passportState)
        {
            case PassportState.NoPassport:

                content = await GetContentAsync(evt, accessToken, cancellationToken);

                if (content != null && !string.IsNullOrEmpty(content.ContentType) && content.Content.Length > 0)
                {
                    string base64Content = Convert.ToBase64String(content.Content);
                    string mimeType = content.ContentType;
                    imageDataUrl = $"data:{mimeType};base64,{base64Content}";
                }
                else if (messageText == null && type == 1) // If no image and no text, cannot proceed
                {
                    _logger.LogError("No image content or text message for messageId: {MessageId}", messageId);
                    return new LineTextMessage { Text = $"ไม่พบรูปภาพหรือข้อความสำหรับประมวลผล (ID: {messageId})." };
                }


                var passportSchemaProperties = new
                {
                    result = new
                    {
                        type = "object",
                        properties = new
                        {
                            countryCode = new { type = "string" },
                            dateOfBirth = new { type = "string" },
                            fullName = new { type = "string" },
                            nationality = new { type = "string" },
                            passportNumber = new { type = "string" },
                            sex = new { type = "string" },
                        },
                        required = new[]
                        {
                            "countryCode", "dateOfBirth", "fullName", "nationality", "passportNumber", "sex",
                        }
                    }
                };
                var passportSchemaRequired = new[] { "result" };

                var passportRequestBody = BuildOpenRouterRequestBody(PassportPrompt, passportSchemaProperties,
                    passportSchemaRequired, imageDataUrl, messageText);

                LLamaPassportResult? result = await CallOpenRouterApiAsync<LLamaPassportResult>(
                    passportRequestBody,
                    messageId,
                    cancellationToken
                );

                if (result == null || string.IsNullOrEmpty(result.PassportNumber) ||
                    string.IsNullOrEmpty(result.Nationality))
                {
                    return new LineTextMessage { Text = $"รูปที่อัพโหลดมาไม่ใช่หนังสือเดินทาง (ID: {messageId})." };
                }

                await _cache.SetObjectAsync($"passport_result:{userId}", result);
                passportState = PassportState.PassportNoAddressTelephone;
                await _cache.SetStringAsync($"passport_state:{userId}", passportState.ToString(),
                    new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30) },
                    cancellationToken);

                return FormatPassportMessage(result);

            default:
                var currentPassport = await _cache.GetObjectAsync<LLamaPassportResult>($"passport_result:{userId}");
                if (currentPassport == null)
                {
                    await _cache.RemoveAsync($"passport_state:{userId}", cancellationToken);
                    _logger.LogError(
                        "No passport data found in cache for userId: {UserId}. Cannot append address and telephone",
                        userId);
                    return new LineTextMessage
                    {
                        Text = $"ไม่พบข้อมูลหนังสือเดินทาง (ID: {messageId}). กรุณาส่งรูปภาพหนังสือเดินทางใหม่"
                    };
                }


                var cleansedMessageText = messageText?.Trim().Replace("-", "").Replace(" ", "");
                var validPhone = cleansedMessageText != null && cleansedMessageText.Length == 10 && cleansedMessageText[0] == '0' &&
                                 cleansedMessageText.All(char.IsDigit);

                if (validPhone)
                {
                    currentPassport.Telephone = cleansedMessageText;
                    await _cache.SetObjectAsync($"passport_result:{userId}", currentPassport);
                    return PrintPassport(currentPassport);
                }


                content = await GetContentAsync(evt, accessToken, cancellationToken);

                if (content != null && !string.IsNullOrEmpty(content.ContentType) && content.Content.Length > 0)
                {
                    string base64Content = Convert.ToBase64String(content.Content);
                    string mimeType = content.ContentType;
                    imageDataUrl = $"data:{mimeType};base64,{base64Content}";
                }
                else if (messageText == null && type == 1) // If no image and no text, cannot proceed
                {
                    _logger.LogError("No image content or text message for messageId: {MessageId}", messageId);
                    return new LineTextMessage { Text = $"ไม่พบรูปภาพหรือข้อความสำหรับประมวลผล (ID: {messageId})." };
                }


                var addressSchemaProperties = new
                {
                    result = new
                    {
                        type = "object",
                        properties = new
                        {
                            address = new { type = "string" },
                            telephone = new { type = "string" },
                        }
                    }
                };
                var addressSchemaRequired = new[] { "result" };

                var addressRequestBody = BuildOpenRouterRequestBody(AddressPrompt, addressSchemaProperties,
                    addressSchemaRequired, imageDataUrl, messageText);

                AddressResult? addressResult = await CallOpenRouterApiAsync<AddressResult>(
                    addressRequestBody,
                    messageId,
                    cancellationToken
                );

                if (addressResult == null)
                {
                    return new LineTextMessage
                    {
                        Text = $"ไม่สามารถประมวลผลข้อมูลที่อยู่และเบอร์โทรศัพท์ได้ (ID: {messageId})."
                    };
                }

                if (!string.IsNullOrEmpty(addressResult.Address))
                {
                    currentPassport.Address = addressResult.Address.Replace(",", "").Replace("  ", " ");
                }

                if (!string.IsNullOrEmpty(addressResult.Telephone))
                {
                    currentPassport.Telephone = addressResult.Telephone;
                }

                await _cache.SetObjectAsync($"passport_result:{userId}", currentPassport);
                return PrintPassport(currentPassport);
        }
    }

    private LineMessage PrintPassport(LLamaPassportResult? currentPassport)
    {
        var formattedMessage = new StringBuilder();
        if (currentPassport == null)
        {
            formattedMessage.AppendLine("ไม่พบข้อมูลหนังสือเดินทาง");
            return new LineTextMessage { Text = formattedMessage.ToString() };
        }

        if (currentPassport is { Address: not null, Telephone: not null })
        {
            formattedMessage.Clear();
            formattedMessage.Append(BuildPassportDisplayMessage(currentPassport));
            formattedMessage.AppendLine("หากต้องการบันทึกข้อมูลทันทีพิมพ์ \"บันทึก\"");
            return new LineTextMessage() { Text = formattedMessage.ToString() };
        }

        if (currentPassport.Address == null)
        {
            formattedMessage.Clear();
            formattedMessage.Append(BuildPassportDisplayMessage(currentPassport));
            formattedMessage.AppendLine(
                "ยังขาดข้อมูลที่อยู่ก่อนบันทึกลง Google Sheet, หากต้องการบันทึกข้อมูลพิมพ์ \"บันทึก\"");
            return new LineTextMessage { Text = formattedMessage.ToString() };
        }

        if (currentPassport.Telephone == null)
        {
            formattedMessage.Clear();
            formattedMessage.Append(BuildPassportDisplayMessage(currentPassport));
            formattedMessage.AppendLine(
                "ยังขาดข้อมูลหมายเลขโทรศัพท์ก่อนบันทึกลง Google Sheet, หากต้องการบันทึกข้อมูลพิมพ์ \"บันทึก\"");
            return new LineTextMessage { Text = formattedMessage.ToString() };
        }

        formattedMessage.Clear();
        formattedMessage.Append(BuildPassportDisplayMessage(currentPassport));
        return new LineTextMessage { Text = formattedMessage.ToString() };
    }

    private async Task<string?> GetLineProfileName(string userId, string accessToken,
        CancellationToken cancellationToken)
    {
        var profileName = await _cache.GetStringAsync($"line_id:{userId}", cancellationToken);
        if (!string.IsNullOrEmpty(profileName))
        {
            return profileName;
        }

        var client = _httpClientFactory.CreateClient("resilient_nocompress");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", accessToken);


        var url = $"https://api.line.me/v2/bot/profile/{userId}";
        var lineResponse = await client.GetAsync(url, cancellationToken);

        if (!lineResponse.IsSuccessStatusCode)
        {
            throw new Exception($"Failed to get user profile: {lineResponse.StatusCode}");
        }

        var lineContent = await lineResponse.Content.ReadAsStringAsync(cancellationToken);
        var json = JsonDocument.Parse(lineContent);
        profileName = json.RootElement.GetProperty("displayName").GetString() ?? string.Empty;
        // var pictureUrl = json.RootElement.GetProperty("pictureUrl").GetString();
        await _cache.SetStringAsync($"line_id:{userId}", profileName,
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1) },
            cancellationToken);

        return profileName;
    }

    public Task<LineReplyStatus> ProcessLineImagesAsync(LineEvent mainEvent, int chatbotId, List<LineEvent> imageEvents,
        string userId,
        string replyToken, string accessToken, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new LineReplyStatus()
        {
            Status = 200,
            ReplyMessage = new LineReplyMessage()
            {
                ReplyToken = replyToken,
                Messages = new List<LineMessage>
                {
                    new LineTextMessage("ไม่รองรับการส่งรูปภาพหลายรูปในครั้งเดียว กรุณาส่งรูปภาพทีละรูป")
                }
            }
        });
    }

    private object BuildOpenRouterRequestBody(string prompt, object schemaProperties, string[] schemaRequired,
        string? imageDataUrl = null, string? messageText = null)
    {
        var messages = new List<object>();

        if (imageDataUrl != null)
        {
            messages.Add(new
            {
                role = "user",
                content = new object[] { new { type = "image_url", image_url = new { url = imageDataUrl } } }
            });
        }
        else if (messageText != null)
        {
            messages.Add(new
            {
                role = "user",
                content = new object[] { new { type = "text", text = $"ข้อความ: {messageText}" } }
            });
        }

        messages.Add(new { role = "user", content = new object[] { new { type = "text", text = prompt }, } });

        return new
        {
            messages = messages.ToArray(),
            model = OpenRouterModel,
            temperature = 1,
            max_completion_tokens = 512,
            top_p = 1,
            stream = false,
            stop = (object?)null,
            provider = new { sort = "throughput" },
            response_format = new
            {
                type = "json_object",
                schema = new
                {
                    type = "object",
                    properties = schemaProperties,
                    required = schemaRequired,
                    additionalProperties = false
                }
            }
        };
    }

    private async Task<T?> CallOpenRouterApiAsync<T>(object requestBody, string messageId,
        CancellationToken cancellationToken)
        where T : class
    {
        HttpClient httpClient = _httpClientFactory.CreateClient("resilient_nocompress");
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", OpenRouterApiKey);

        string responseContent;
        try
        {
            var jsonRequest = JsonSerializer.Serialize(requestBody,
                new JsonSerializerOptions
                {
                    WriteIndented = false,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                });
            _logger.LogDebug(
                "Sending request to OpenRouter API for messageId: {MessageId}. Request body (partial): {Request}",
                messageId, jsonRequest);

            HttpResponseMessage response = await httpClient.PostAsync(OpenRouterApiUrl,
                new StringContent(jsonRequest, Encoding.UTF8, "application/json"),
                cancellationToken);

            responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning(
                "Received response from OpenRouter API for messageId: {MessageId}. Status: {StatusCode}. Body (partial): {Response}",
                messageId, response.StatusCode, responseContent);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "OpenRouter API call failed for messageId {MessageId} with status {StatusCode}. Response: {ResponseContent}",
                    messageId, response.StatusCode, responseContent);
                return null; // Indicate failure
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception during OpenRouter API call for messageId: {MessageId}", messageId);
            return null; // Indicate failure
        }

        var OpenRouterResponse = JsonSerializer.Deserialize<OpenAIResponse>(responseContent);
        string? innerJsonContent = OpenRouterResponse?.Choices?.FirstOrDefault()?.Message?.Content;

        if (string.IsNullOrEmpty(innerJsonContent))
        {
            _logger.LogError(
                "OpenRouter response is missing choices or message content for messageId: {MessageId}. Response: {ResponseContent}",
                messageId, responseContent);
            return null; // Indicate failure
        }

        try
        {
            var result = JsonSerializer.Deserialize<T>(innerJsonContent);
            if (result == null)
            {
                _logger.LogError(
                    "Failed to deserialize OpenRouter response content to type {TypeName} for messageId: {MessageId}. Content: {Content}",
                    typeof(T).Name, messageId, innerJsonContent);
                return null; // Indicate failure
            }

            return result;
        }
        catch (JsonException jsonEx)
        {
            _logger.LogError(jsonEx,
                "JSON deserialization failed to type {TypeName} for messageId: {MessageId}. Content: {Content}",
                typeof(T).Name, messageId, innerJsonContent);
            return null; // Indicate failure
        }
    }

    private LineTextMessage FormatPassportMessage(LLamaPassportResult result)
    {
        var formattedMessage = new StringBuilder(BuildPassportDisplayMessage(result));
        formattedMessage.AppendLine(
            "กรุณาส่งข้อมูลที่อยู่และหมายเลขโทรศัพท์ก่อนบันทึกข้อมูลใน Google Sheet, หากต้องการบันทึกข้อมูลทันทีพิมพ์ \"บันทึก\"");
        return new LineTextMessage() { Text = formattedMessage.ToString() };
    }
    
    /// <summary>
    /// Cancels any ongoing operations for a specific user by removing session data
    /// </summary>
    /// <param name="userId">The user ID for which to cancel operations</param>
    /// <param name="cancellationToken">Cancellation token for the operation</param>
    /// <returns>A task representing the asynchronous operation</returns>
    public async Task CancelOperationsAsync(string userId, CancellationToken cancellationToken = default)
    {
        try
        {
            // Remove passport result from cache
            await _cache.RemoveAsync($"passport_result:{userId}", cancellationToken);
            
            // Remove passport state from cache
            await _cache.RemoveAsync($"passport_state:{userId}", cancellationToken);
            
            _logger.LogInformation("Cancelled LLamaPassport operations for user {UserId}", userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cancelling LLamaPassport operations for user {UserId}", userId);
        }
    }
}
