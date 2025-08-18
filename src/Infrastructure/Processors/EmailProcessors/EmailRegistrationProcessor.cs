using System;
using System.Threading.Tasks;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using ChatbotApi.Application.Common.Models;
using ChatbotApi.Application.Common.Interfaces;
using ChatbotApi.Domain.Entities;
using ChatbotApi.Domain.Constants;

namespace IChatBot.Infrastructure.Processors.EmailProcessors
{
    public class EmailRegistrationProcessor : ILineMessageProcessor
    {
        public string Name => Systems.EmailRegistration;
        
        private readonly IConfiguration _configuration;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<EmailRegistrationProcessor> _logger;
        private readonly ILineMessenger _lineMessenger;
        private readonly IApplicationDbContext _context;
        
        public EmailRegistrationProcessor(
            IConfiguration configuration,
            IHttpClientFactory httpClientFactory,
            ILogger<EmailRegistrationProcessor> logger,
            ILineMessenger lineMessenger,
            IApplicationDbContext context)
        {
            _configuration = configuration;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _lineMessenger = lineMessenger;
            _context = context;
        }
        
        // Method to handle the registration rich menu click
        public async Task HandleRegistrationMenuClickAsync(string lineUserId)
        {
            _logger.LogInformation("HandleRegistrationMenuClickAsync called for user {UserId}", lineUserId);
            
            // Show message asking user to type their email for registration
            await ShowEmailInputMessageAsync(lineUserId);
            
            _logger.LogInformation("HandleRegistrationMenuClickAsync completed for user {UserId}", lineUserId);
        }
        
        // Method to show email input message
        private async Task ShowEmailInputMessageAsync(string lineUserId)
        {
            // Show message asking user to type their email for registration
            var message = "กรุณาพิมพ์อีเมลของคุณเพื่อลงทะเบียน";
            await SendPushMessageAsync(lineUserId, message);
        }
        
        // Method to process user's email input
        public async Task ProcessEmailInputAsync(string lineUserId, string email)
        {
            // Check if email is valid
            if (!IsValidEmail(email))
            {
                await ShowInvalidEmailMessageAsync(lineUserId);
                return;
            }
            
            // Check registration status via API
            var isAlreadyRegistered = await CheckRegistrationStatusAsync(lineUserId);
            
            if (isAlreadyRegistered)
            {
                // 3.1. If found (already registered): Show "อีเมลนี้เคยลงทะเบียนในระบบแล้ว"
                await ShowAlreadyRegisteredMessageAsync(lineUserId);
            }
            else
            {
                // 3.2. If not registered: Call API with email and line userid to register
                var registrationResult = await RegisterUserAsync(email, lineUserId);
                
                if (registrationResult)
                {
                    // Show success message
                    await ShowRegistrationSuccessMessageAsync(lineUserId);
                }
                else
                {
                    // Show registration failed message
                    await ShowRegistrationFailedMessageAsync(lineUserId);
                }
            }
        }
        
        // Method to check registration status via API
        private async Task<bool> CheckRegistrationStatusAsync(string lineUserId)
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
                var response = await httpClient.GetAsync(url);
                
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
        
        // Method to register user via API
        private async Task<bool> RegisterUserAsync(string email, string lineUserId)
        {
            try
            {
                // Get the SSO API URL and token from configuration
                var baseUrl = _configuration["WorkingTime:SSOApiUrl"];
                var apiToken = _configuration["WorkingTime:SSOApiUrlToken"];
                
                if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(apiToken))
                {
                    _logger.LogError("SSO API configuration is missing");
                    return false;
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
                var response = await httpClient.SendAsync(request);
                
                if (response.IsSuccessStatusCode)
                {
                    // Registration successful
                    _logger.LogInformation("User {UserId} successfully registered with email {Email}", lineUserId, email);
                    return true;
                }
                else
                {
                    // Registration failed
                    _logger.LogError("Failed to register user {UserId} with email {Email}. Status: {StatusCode}",
                        lineUserId, email, response.StatusCode);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during email registration for user {UserId}", lineUserId);
                return false;
            }
        }
        
        // Method to show "already registered" message
        private async Task ShowAlreadyRegisteredMessageAsync(string lineUserId)
        {
            // Show message: "อีเมลนี้เคยลงทะเบียนในระบบแล้ว"
            var message = "อีเมลนี้เคยลงทะเบียนในระบบแล้ว";
            await SendPushMessageAsync(lineUserId, message);
        }
        
        // Method to show registration success message
        private async Task ShowRegistrationSuccessMessageAsync(string lineUserId)
        {
            // Show message:
            // "ลงทะเบียนเรียบร้อย คุณสามารถใช้ฟังก์ชันการ Check-in, Check-out และอื่นๆ ได้ด้วยการคลิกที่เลือกจากเมนู หรือส่งข้อความ Check-in, Check-out เพื่อดำเนินการต่อ"
            var message = "ลงทะเบียนเรียบร้อย คุณสามารถใช้ฟังก์ชันการ Check-in, Check-out และอื่นๆ ได้ด้วยการคลิกที่เลือกจากเมนู หรือส่งข้อความ Check-in, Check-out เพื่อดำเนินการต่อ";
            await SendPushMessageAsync(lineUserId, message);
        }
        
        // Method to show invalid email message
        private async Task ShowInvalidEmailMessageAsync(string lineUserId)
        {
            // Show message about invalid email format
            var message = "รูปแบบอีเมลไม่ถูกต้อง กรุณาตรวจสอบอีกครั้ง";
            await SendPushMessageAsync(lineUserId, message);
        }
        
        // Method to show registration failed message
        private async Task ShowRegistrationFailedMessageAsync(string lineUserId)
        {
            // Show message about registration failure
            var message = "ไม่สามารถลงทะเบียนได้ กรุณาลองใหม่อีกครั้ง";
            await SendPushMessageAsync(lineUserId, message);
        }
        
        // Helper method to validate email format
        private bool IsValidEmail(string email)
        {
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
        
        // Helper method to send push messages
        private async Task SendPushMessageAsync(string toUserId, string text)
        {
            try
            {
                _logger.LogInformation("Attempting to send push message to user {UserId} with text: {Text}", toUserId, text);
                
                // Get any chatbot with a valid access token
                var chatbot = await _context.Chatbots
                    .Where(c => !string.IsNullOrEmpty(c.LineChannelAccessToken))
                    .FirstOrDefaultAsync();
                    
                if (chatbot == null)
                {
                    _logger.LogWarning("No chatbot with valid access token found for sending push message");
                    return;
                }
                
                _logger.LogInformation("Found chatbot with ID {ChatbotId} for sending push message", chatbot.Id);
                
                // Create the push message
                var pushMessage = new LinePushMessage
                {
                    To = toUserId,
                    Messages = new List<LineMessage>
                    {
                        new LineTextMessage { Text = text }
                    }
                };
                
                _logger.LogInformation("Created push message for user {UserId}", toUserId);
                
                // Send the push message
                var result = await _lineMessenger.SendPushMessage(chatbot, pushMessage);
                
                _logger.LogInformation("Push message sent to user {UserId} with status {Status}", toUserId, result.Status);
                
                if (!string.IsNullOrEmpty(result.Error))
                {
                    _logger.LogError("Error sending push message to user {UserId}: {Error}", toUserId, result.Error);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending push message to user {UserId}", toUserId);
            }
        }
        
        public async Task<LineReplyStatus> ProcessLineAsync(LineEvent evt, int chatbotId, string message, string userId,
            string replyToken, CancellationToken cancellationToken = default)
        {
            // Check if message is a registration command
            if (IsRegistrationCommand(message))
            {
                // Handle registration command by showing email input message
                await HandleRegistrationMenuClickAsync(userId);
                return new LineReplyStatus { Status = 200 };
            }
            
            // Check if message is an email address for registration
            if (IsValidEmail(message))
            {
                // Process the email registration
                await ProcessEmailInputAsync(userId, message);
                return new LineReplyStatus { Status = 200 };
            }
            
            // Return 404 for unrecognized messages
            return new LineReplyStatus { Status = 404 };
        }
        
        public async Task<LineReplyStatus> ProcessLineImageAsync(LineEvent evt, int chatbotId, string messageId, string userId,
            string replyToken, string accessToken, CancellationToken cancellationToken = default)
        {
            // This processor doesn't handle images, return 404
            return new LineReplyStatus { Status = 404 };
        }
        
        private bool IsRegistrationCommand(string message)
        {
            var registrationCommands = new[] { "ลงทะเบียน", "register" };
            return registrationCommands.Contains(message.ToLowerInvariant().Trim());
        }
    }
}