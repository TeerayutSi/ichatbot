using System.Text.Json;
using ChatbotApi.Application.Common.Exceptions;
using ChatbotApi.Application.Common.Interfaces;
using ChatbotApi.Application.Webhook.Commands.FacebookWebhookCommand;
using ChatbotApi.Application.Webhook.Commands.LineWebhookCommand;
using ChatbotApi.Application.Webhook.Commands.OpenAIWebhookCommand;
using ChatbotApi.Application.Webhook.Queries.GetFacebookSubscribeQuery;
using ChatbotApi.Domain.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace ChatbotApi.Web.Endpoints;

public class WebhookEndpoint : EndpointGroupBase
{
    public override void Map(WebApplication app)
    {
        app.MapGroup("/webhook")
            .MapGet(FacebookGet, "/facebook/{chatbotId:int}")
            .MapPost(FacebookPost, "/facebook/{chatbotId:int}")
            .MapPost(Line, "/line/{chatbotId:int}")
            .MapPost(OpenAI, "/openai/{chatbotId:int}/chat/completions")
            ;
    }

    private async Task<IResult> OpenAI(ISender sender, int chatbotId, HttpContext httpContext, IApplicationDbContext context)
    {
        using var reader = new StreamReader(httpContext.Request.Body);
        var requestBody = await reader.ReadToEndAsync();

        await context.IncomingRequests.AddAsync(new IncomingRequest()
        {
            Raw = requestBody
        });
        await context.SaveChangesAsync(CancellationToken.None);

        // Deserialize the request body into LineWebhookCommand object
        var command = JsonSerializer.Deserialize<OpenAIWebhookCommand>(requestBody);

        if (command == null)
        {
            return Results.BadRequest("Not openai webhook");
        }

        command.ChatbotId = chatbotId;

        if (httpContext.Request.Headers["Authorization"] == StringValues.Empty)
        {
            return Results.Unauthorized();
        }

        var authorization = httpContext.Request.Headers["Authorization"];
        var bearerToken = authorization.ToString().Replace("Bearer ", "");
        command.ApiKey = bearerToken;
        try
        {
            var result = await sender.Send(command);
            if (result.Choices != null && result.Choices.Count > 0)
            {
                return Results.Ok(result);
            }

        }
        catch (ChatCompletionException e)
        {
            return Results.BadRequest(e.Message);
        }

        return Results.BadRequest();
    }
    private async Task<IResult> FacebookGet(ISender sender, HttpContext context, int chatbotId)
    {
        var mode = context.Request.Query["hub.mode"];
        var token = context.Request.Query["hub.verify_token"];
        var challenge = context.Request.Query["hub.challenge"];

        var result = await sender.Send(new GetFacebookSubscribeQuery()
        {
            Mode = mode.ToString(),
            VerifyToken = token.ToString(),
            Challenge = challenge.ToString(),
            ChatbotId = chatbotId
        });

        return result;
    }

    private async Task<IResult> FacebookPost(ISender sender, int chatbotId, HttpContext httpContext, IApplicationDbContext context)
    {
        using var reader = new StreamReader(httpContext.Request.Body);
        var requestBody = await reader.ReadToEndAsync();

        await context.IncomingRequests.AddAsync(new IncomingRequest()
        {
            Raw = requestBody
        });
        await context.SaveChangesAsync(CancellationToken.None);

        var command = JsonSerializer.Deserialize<FacebookWebhookCommand>(requestBody);

        if (command == null)
        {
            return Results.BadRequest("Invalid Facebook webhook payload.");
        }

        command.ChatbotId = chatbotId;
        var result = await sender.Send(command);
        if (result)
        {
            return Results.Ok();
        }

        return Results.BadRequest();
    }


    private async Task<IResult> Line(ISender sender, int chatBotId, HttpContext httpContext, IApplicationDbContext context)
    {
        try
        {
            // Log the start of webhook processing
            Console.WriteLine($"Starting LINE webhook processing for chatbot {chatBotId} at {DateTime.UtcNow}");
            
            using var reader = new StreamReader(httpContext.Request.Body);
            var requestBody = await reader.ReadToEndAsync();
    
            // Log the incoming request
            Console.WriteLine($"Received LINE webhook for chatbot {chatBotId}. Request body length: {requestBody.Length}");
    
            // Always save the incoming request for debugging purposes
            try
            {
                await context.IncomingRequests.AddAsync(new IncomingRequest()
                {
                    Raw = requestBody
                });
                await context.SaveChangesAsync(CancellationToken.None);
                Console.WriteLine($"Saved incoming request for chatbot {chatBotId} to database");
            }
            catch (Exception dbEx)
            {
                // Log database error but don't fail the webhook
                Console.WriteLine($"Error saving incoming request for chatbot {chatBotId}: {dbEx}");
            }
    
            // Deserialize the request body into LineWebhookCommand object
            LineWebhookCommand? command = null;
            try
            {
                command = JsonSerializer.Deserialize<LineWebhookCommand>(requestBody);
                Console.WriteLine($"Deserialized LINE webhook for chatbot {chatBotId}. Events count: {command?.Events?.Count ?? 0}");
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"Error deserializing LINE webhook for chatbot {chatBotId}: {ex}");
                Console.WriteLine($"Request body: {requestBody}");
                // Still return 200 OK to acknowledge receipt of the webhook
                return Results.Ok();
            }
    
            if (command == null || command.Destination == null)
            {
                Console.WriteLine($"Invalid LINE webhook for chatbot {chatBotId}: command or destination is null");
                Console.WriteLine($"Request body: {requestBody}");
                return Results.Ok(); // Still acknowledge receipt
            }
    
            command.ChatbotId = chatBotId;
            
            // Process the webhook command
            try
            {
                Console.WriteLine($"Sending command to MediatR for chatbot {chatBotId}");
                var result = await sender.Send(command);
                Console.WriteLine($"Command processed for chatbot {chatBotId}. Result: {result}");
            }
            catch (Exception sendEx)
            {
                // Log the exception but still return 200 OK
                Console.WriteLine($"Error processing LINE webhook command for chatbot {chatBotId}: {sendEx}");
                Console.WriteLine($"Command: {System.Text.Json.JsonSerializer.Serialize(command)}");
            }
            
            // Log successful completion
            Console.WriteLine($"Completed LINE webhook processing for chatbot {chatBotId}");
            
            // Always return 200 OK to acknowledge receipt by LINE platform
            return Results.Ok();
        }
        catch (Exception ex)
        {
            // Log the exception for debugging purposes
            Console.WriteLine($"Unexpected error processing LINE webhook for chatbot {chatBotId}: {ex}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            
            // Always return 200 OK to LINE platform to acknowledge receipt
            // Even if there's an error processing the message, we should acknowledge receipt
            return Results.Ok();
        }
    }
}
