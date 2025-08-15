using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ChatbotApi.Application.Common.Interfaces;
using ChatbotApi.Application.Common.Models;
using ChatbotApi.Domain.Entities;

namespace ChatbotApi.Infrastructure.Line;

public class LineMessagingApi : ILineMessenger
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LineMessagingApi> _logger;

    public LineMessagingApi(IHttpClientFactory httpClientFactory, ILogger<LineMessagingApi> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }


    public async Task<LineSendResponse> SendMessage(Chatbot chatbot, LineReplyMessage message,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (message.Messages.Count == 0)
            {
                return new LineSendResponse() { Status = 400, Error = "No message to send" };
            }

            if (string.IsNullOrEmpty(chatbot.LineChannelAccessToken))
            {
                _logger.LogWarning("LineChannelAccessToken is null or empty for chatbot {ChatbotId}", chatbot.Id);
                return new LineSendResponse() { Status = 400, Error = "LineChannelAccessToken is missing" };
            }

            var httpClient = _httpClientFactory.CreateClient();
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", chatbot.LineChannelAccessToken);

            var json = JsonSerializer.Serialize(message, LineMessageConverter.Options);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            // Add timeout to prevent hanging requests
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(30)); // 30 second timeout
            
            var result = await httpClient.PostAsync("https://api.line.me/v2/bot/message/reply", content,
                cts.Token);
            if (result.IsSuccessStatusCode)
            {
                var response = await result.Content.ReadAsStringAsync(cts.Token);
                var sentResponse = JsonSerializer.Deserialize<SentResponse>(response)!;

                var sentMessages = new List<SentMessageData>();
                for (int i = 0; i < sentResponse.SentMessages.Count; i++)
                {
                    var sentMessageData = new SentMessageData()
                    {
                        MessageId = sentResponse.SentMessages[i].Id,
                        QuoteToken = sentResponse.SentMessages[i].QuoteToken,
                    };

                    if (message.Messages[i] is LineTextMessage textMessage)
                    {
                        sentMessageData.AllText = textMessage.Text;
                    }
                    else if (message.Messages[i] is LineTextMessageV2 textMessageV2)
                    {
                        sentMessageData.AllText = textMessageV2.Text;
                    }
                    sentMessages.Add(sentMessageData);
                }

                return new LineSendResponse() { Status = 200, SentMessages = sentMessages };
            }
            else
            {
                try
                {
                    var validate = await httpClient.PostAsync("https://api.line.me/v2/bot/message/validate/reply", content,
                        cts.Token);
                    if (validate.IsSuccessStatusCode)
                    {
                        var validateResult = await validate.Content.ReadAsStringAsync(cts.Token);
                        _logger.LogError("Validate result: {Result}", validateResult);
                    }
                }
                catch (Exception validateEx)
                {
                    _logger.LogError(validateEx, "Error validating LINE message for chatbot {ChatbotId}", chatbot.Id);
                }

                var error = await result.Content.ReadAsStringAsync(cts.Token);
                _logger.LogError("Error from LINE API for chatbot {ChatbotId}: {Error}", chatbot.Id, error);
                return new LineSendResponse() { Status = (int)result.StatusCode, Error = error };
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogError("Timeout sending message to LINE for chatbot {ChatbotId}", chatbot.Id);
            return new LineSendResponse() { Status = 500, Error = "Timeout sending message to LINE" };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending message to LINE for chatbot {ChatbotId}", chatbot.Id);
            return new LineSendResponse() { Status = 500, Error = ex.Message };
        }
    }

    public async Task<LineSendResponse> SendRawMessage(Chatbot chatbot, string json,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrEmpty(json))
            {
                return new LineSendResponse() { Status = 400, Error = "No message to send" };
            }

            if (string.IsNullOrEmpty(chatbot.LineChannelAccessToken))
            {
                _logger.LogWarning("LineChannelAccessToken is null or empty for chatbot {ChatbotId}", chatbot.Id);
                return new LineSendResponse() { Status = 400, Error = "LineChannelAccessToken is missing" };
            }

            var httpClient = _httpClientFactory.CreateClient();
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", chatbot.LineChannelAccessToken);

            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            // Add timeout to prevent hanging requests
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(30)); // 30 second timeout
            
            var result = await httpClient.PostAsync("https://api.line.me/v2/bot/message/reply", content,
                cts.Token);
            if (result.IsSuccessStatusCode)
            {
                return new LineSendResponse() { Status = 200 };
            }
            else
            {
                try
                {
                    var validate = await httpClient.PostAsync("https://api.line.me/v2/bot/message/validate/reply", content,
                        cts.Token);
                    if (validate.IsSuccessStatusCode)
                    {
                        var validateResult = await validate.Content.ReadAsStringAsync(cts.Token);
                        _logger.LogError("Validate result: {Result}", validateResult);
                    }
                }
                catch (Exception validateEx)
                {
                    _logger.LogError(validateEx, "Error validating LINE raw message for chatbot {ChatbotId}", chatbot.Id);
                }

                var error = await result.Content.ReadAsStringAsync(cts.Token);
                _logger.LogError("Error from LINE API for chatbot {ChatbotId}: {Error}", chatbot.Id, error);
                return new LineSendResponse() { Status = (int)result.StatusCode, Error = error };
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogError("Timeout sending raw message to LINE for chatbot {ChatbotId}", chatbot.Id);
            return new LineSendResponse() { Status = 500, Error = "Timeout sending raw message to LINE" };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending raw message to LINE for chatbot {ChatbotId}", chatbot.Id);
            return new LineSendResponse() { Status = 500, Error = ex.Message };
        }
    }

    public async Task<LineSendResponse> SendPushMessage(Chatbot chatbot, LinePushMessage message,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (message.Messages.Count == 0)
            {
                return new LineSendResponse() { Status = 400, Error = "No message to send" };
            }

            if (string.IsNullOrEmpty(chatbot.LineChannelAccessToken))
            {
                _logger.LogWarning("LineChannelAccessToken is null or empty for chatbot {ChatbotId}", chatbot.Id);
                return new LineSendResponse() { Status = 400, Error = "LineChannelAccessToken is missing" };
            }

            var httpClient = _httpClientFactory.CreateClient();
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", chatbot.LineChannelAccessToken);

            var json = JsonSerializer.Serialize(message, LineMessageConverter.Options);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            // Add timeout to prevent hanging requests
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(30)); // 30 second timeout
            
            var result = await httpClient.PostAsync("https://api.line.me/v2/bot/message/push", content,
                cts.Token);
            if (result.IsSuccessStatusCode)
            {
                var response = await result.Content.ReadAsStringAsync(cts.Token);
                var sentResponse = JsonSerializer.Deserialize<SentResponse>(response)!;

                var sentMessages = new List<SentMessageData>();
                for (int i = 0; i < sentResponse.SentMessages.Count; i++)
                {
                    var sentMessageData = new SentMessageData()
                    {
                        MessageId = sentResponse.SentMessages[i].Id,
                        QuoteToken = sentResponse.SentMessages[i].QuoteToken,
                    };

                    if (message.Messages[i] is LineTextMessage textMessage)
                    {
                        sentMessageData.AllText = textMessage.Text;
                    }
                    else if (message.Messages[i] is LineTextMessageV2 textMessageV2)
                    {
                        sentMessageData.AllText = textMessageV2.Text;
                    }
                    sentMessages.Add(sentMessageData);
                }

                return new LineSendResponse() { Status = 200, SentMessages = sentMessages };
            }
            else
            {
                try
                {
                    var validate = await httpClient.PostAsync("https://api.line.me/v2/bot/message/validate/push", content,
                        cts.Token);
                    if (validate.IsSuccessStatusCode)
                    {
                        var validateResult = await validate.Content.ReadAsStringAsync(cts.Token);
                        _logger.LogError("Validate result: {Result}", validateResult);
                    }
                }
                catch (Exception validateEx)
                {
                    _logger.LogError(validateEx, "Error validating LINE push message for chatbot {ChatbotId}", chatbot.Id);
                }

                var error = await result.Content.ReadAsStringAsync(cts.Token);
                _logger.LogError("Error from LINE API for chatbot {ChatbotId}: {Error}", chatbot.Id, error);
                return new LineSendResponse() { Status = (int)result.StatusCode, Error = error };
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogError("Timeout sending push message to LINE for chatbot {ChatbotId}", chatbot.Id);
            return new LineSendResponse() { Status = 500, Error = "Timeout sending push message to LINE" };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending push message to LINE for chatbot {ChatbotId}", chatbot.Id);
            return new LineSendResponse() { Status = 500, Error = ex.Message };
        }
    }
}
