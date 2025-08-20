# Cancellation Coordination Plan

## Overview
This document outlines the detailed plan for coordinating cancellation across multiple processors when a new menu is selected in the LINE chatbot system.

## Cancellation Coordination Strategy

### 1. Detection of Menu Selection Events
Menu selections in the LINE chatbot system are typically postback events with specific data patterns. We need to identify these patterns to trigger cancellation:

```csharp
// In LineWebhookCommandHandler
private bool IsMenuSelection(string postbackData)
{
    // Common menu selection patterns
    return postbackData.StartsWith("menu_") ||
           postbackData.StartsWith("office_selected_") ||
           postbackData.StartsWith("current_location_selected_") ||
           postbackData.StartsWith("confirm_email_registration_") ||
           // Add other known menu patterns
           IsRichMenuSelection(postbackData);
}

private bool IsRichMenuSelection(string postbackData)
{
    // Rich menu selections might have specific patterns
    // This could be based on configuration or known prefixes
    return postbackData.Contains("richmenu") ||
           postbackData.Contains("main_menu") ||
           postbackData.Contains("submenu");
}
```

### 2. Cancellation Coordination Mechanism
When a menu selection is detected, we need to coordinate cancellation across all processors that implement `IProcessorCancellationHandler`:

```csharp
// In LineWebhookCommandHandler
private async Task CancelPreviousOperationsForUser(string userId, CancellationToken cancellationToken)
{
    _logger.LogInformation("Cancelling previous operations for user {UserId}", userId);
    
    // Track cancellation results for logging and monitoring
    var cancellationResults = new List<(string processorName, bool success, Exception? exception)>();
    
    // Run cancellations in parallel for better performance
    var cancellationTasks = new List<Task>();
    
    foreach (var processor in _messageProcessors)
    {
        if (processor is IProcessorCancellationHandler cancellationHandler)
        {
            var task = Task.Run(async () =>
            {
                try
                {
                    await cancellationHandler.CancelOperationsAsync(userId, cancellationToken);
                    cancellationResults.Add((processor.Name, true, null));
                    _logger.LogDebug("Successfully cancelled operations for processor {ProcessorName} and user {UserId}", 
                        processor.Name, userId);
                }
                catch (Exception ex)
                {
                    cancellationResults.Add((processor.Name, false, ex));
                    _logger.LogError(ex, "Error cancelling operations for processor {ProcessorName} and user {UserId}", 
                        processor.Name, userId);
                }
            }, cancellationToken);
            
            cancellationTasks.Add(task);
        }
    }
    
    // Wait for all cancellations to complete
    if (cancellationTasks.Any())
    {
        try
        {
            await Task.WhenAll(cancellationTasks);
            _logger.LogInformation("Completed cancellation for user {UserId} across {ProcessorCount} processors", 
                userId, cancellationTasks.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error waiting for cancellation tasks to complete for user {UserId}", userId);
        }
    }
    else
    {
        _logger.LogDebug("No processors with cancellation handlers found for user {UserId}", userId);
    }
    
    // Log summary of cancellation results
    LogCancellationSummary(userId, cancellationResults);
}
```

### 3. Integration with Postback Processing
The cancellation coordination needs to be integrated into the postback processing flow:

```csharp
// Modified ProcessPostbackEvent method in LineWebhookCommandHandler
private async Task<LineReplyStatus?> ProcessPostbackEvent(Event evt, Chatbot chatbot, List<string> plugins,
    string userId, string replyToken, CancellationToken cancellationToken)
{
    string messageText = evt.Postback?.Data ?? string.Empty;
    
    // Check if this is a menu selection that should trigger cancellation
    if (IsMenuSelection(messageText))
    {
        _logger.LogInformation("Menu selection detected for user {UserId}, triggering cancellation", userId);
        await CancelPreviousOperationsForUser(userId, cancellationToken);
    }
    
    LineReplyStatus? toReturn = null;
    if (chatbot.LineChannelAccessToken != null)
    {
        foreach (ILineMessageProcessor process in _messageProcessors)
        {
            if (!plugins.Contains(process.Name))
            {
                continue;
            }

            LineReplyStatus result =
                await process.ProcessLineAsync(evt, chatbot.Id, messageText, userId, replyToken,
                    cancellationToken);
            if (result.Status is 200 or 201)
            {
                toReturn = result;
                break;
            }
        }
    }

    return toReturn;
}
```

## Processor-Specific Cancellation Implementation

### 1. LLamaPassportProcessor (Already Implemented)
```csharp
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
```

### 2. WorkingTimeProcessor (Needs Interface Implementation)
```csharp
// Add interface implementation to WorkingTimeProcessor
public async Task CancelOperationsAsync(string userId, CancellationToken cancellationToken = default)
{
    try
    {
        // Cancel working time session if exists
        await _cache.RemoveAsync($"workingtime_session:{userId}", cancellationToken);
        
        // Cancel registration flow if exists (from EmailRegistrationProcessor)
        await _cache.RemoveAsync($"registration_flow_{userId}", cancellationToken);
        
        _logger.LogInformation("Cancelled WorkingTime operations for user {UserId}", userId);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Error cancelling WorkingTime operations for user {UserId}", userId);
    }
}
```

### 3. TrackFileProcessor (New Implementation)
```csharp
// Add interface implementation to TrackFileProcessor
public async Task CancelOperationsAsync(string userId, CancellationToken cancellationToken = default)
{
    try
    {
        // Remove any pending file uploads
        await _cache.RemoveAsync($"pending_upload:{userId}", cancellationToken);
        
        // Add any other session data that needs to be cleared
        // TODO: Identify other session data in TrackFileProcessor
        
        _logger.LogInformation("Cancelled TrackFile operations for user {UserId}", userId);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Error cancelling TrackFile operations for user {UserId}", userId);
    }
}
```

### 4. Other Processors
Similar implementations would be needed for:
- CheckCheatOnlineProcessor
- EmailRegistrationProcessor
- RichMenuProcessor
- And any other processors that maintain session state

## Error Handling and Resilience

### 1. Cancellation Failure Handling
Cancellation failures should not block the main operation:

```csharp
private async Task CancelPreviousOperationsForUser(string userId, CancellationToken cancellationToken)
{
    // ... existing code ...
    
    // Even if some cancellations fail, we continue with the main operation
    // The failures are logged but don't prevent the new menu selection from being processed
}
```

### 2. Timeout Handling
To prevent cancellations from taking too long:

```csharp
private async Task CancelPreviousOperationsForUser(string userId, CancellationToken cancellationToken)
{
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    cts.CancelAfter(TimeSpan.FromSeconds(5)); // 5 second timeout for cancellations
    
    try
    {
        // ... existing cancellation code with cts.Token ...
    }
    catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
    {
        _logger.LogWarning("Cancellation operations timed out for user {UserId}", userId);
    }
}
```

## Monitoring and Logging

### 1. Cancellation Metrics
Track cancellation success rates and performance:

```csharp
private void LogCancellationSummary(string userId, List<(string processorName, bool success, Exception? exception)> results)
{
    var successfulCancellations = results.Count(r => r.success);
    var failedCancellations = results.Count(r => !r.success);
    
    _logger.LogInformation("Cancellation summary for user {UserId}: {Successful} successful, {Failed} failed", 
        userId, successfulCancellations, failedCancellations);
    
    if (failedCancellations > 0)
    {
        var failedProcessors = results.Where(r => !r.success).Select(r => r.processorName);
        _logger.LogWarning("Failed cancellations for user {UserId} in processors: {FailedProcessors}", 
            userId, string.Join(", ", failedProcessors));
    }
}
```

### 2. Performance Monitoring
Monitor how long cancellations take:

```csharp
private async Task CancelPreviousOperationsForUser(string userId, CancellationToken cancellationToken)
{
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    
    try
    {
        // ... existing cancellation code ...
    }
    finally
    {
        stopwatch.Stop();
        _logger.LogInformation("Cancellation operations for user {UserId} completed in {ElapsedMilliseconds}ms", 
            userId, stopwatch.ElapsedMilliseconds);
    }
}
```

## Testing Strategy

### 1. Unit Tests
- Test `IsMenuSelection` method with various postback data patterns
- Test `CancelPreviousOperationsForUser` with mock processors
- Test error handling in cancellation methods

### 2. Integration Tests
- Test end-to-end menu selection flow with cancellation
- Test cancellation with multiple processors
- Test cancellation timeout scenarios

### 3. Manual Testing
- Verify that previous operations are properly cancelled when selecting new menus
- Verify that new operations work correctly after cancellation
- Test edge cases like rapid menu selections

## Rollout Plan

### 1. Phase 1: Core Implementation
- Implement cancellation coordination in `LineWebhookCommand`
- Update `WorkingTimeProcessor` to implement `IProcessorCancellationHandler`
- Add basic logging and monitoring

### 2. Phase 2: Processor Updates
- Update all processors that maintain session state to implement cancellation
- Add comprehensive error handling and timeout management

### 3. Phase 3: Testing and Monitoring
- Deploy to test environment
- Monitor cancellation success rates and performance
- Address any issues discovered during testing

### 4. Phase 4: Production Deployment
- Deploy to production with monitoring enabled
- Monitor for any unexpected issues
- Gather feedback and make improvements as needed