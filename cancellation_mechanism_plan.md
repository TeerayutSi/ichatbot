# Cancellation Mechanism Implementation Plan

## Overview
This document outlines the plan for implementing a cancellation mechanism that will cancel previous operations when a new menu is selected in the LINE chatbot system.

## Current State Analysis

### Implemented Cancellation Handlers
1. **LLamaPassportProcessor** - Implements `IProcessorCancellationHandler` properly
   - Cancels passport result and state from cache

2. **WorkingTimeProcessor** - Has cancellation logic but doesn't implement the interface
   - Cancels working time session and registration flow from cache

### Missing Cancellation Handlers
Several processors maintain session state but don't implement cancellation:
- TrackFileProcessor
- CheckCheatOnlineProcessor
- EmailRegistrationProcessor
- RichMenuProcessor
- And potentially others

## Implementation Plan

### Phase 1: Infrastructure Enhancement

1. **Modify LineWebhookCommand to Support Cancellation**
   - Add logic to detect menu selections (postback events)
   - Call cancellation handlers before processing new menu selections
   - Ensure cancellation is called for all processors that implement the interface

2. **Update Processors to Implement IProcessorCancellationHandler**
   - For processors that maintain session state, implement the interface
   - Identify what session data needs to be cleared for each processor

### Phase 2: Processor Updates

For each processor that maintains session state:

1. **WorkingTimeProcessor**
   - Implement `IProcessorCancellationHandler`
   - Move existing `CancelPreviousOperations` method to the interface method
   - Ensure all session data is properly cleared

2. **TrackFileProcessor**
   - Implement `IProcessorCancellationHandler`
   - Identify and clear any session data (pending uploads, etc.)

3. **Other Processors**
   - Review each processor for session state
   - Implement cancellation where needed

### Phase 3: Integration and Testing

1. **Integration Testing**
   - Test menu selection cancellation flow
   - Verify session data is properly cleared
   - Ensure no side effects on other functionality

2. **Edge Case Handling**
   - Handle cancellation failures gracefully
   - Log cancellation activities for debugging
   - Ensure cancellation doesn't block new operations

## Technical Details

### Detection of Menu Selections
Menu selections come as postback events with specific data patterns:
- Rich menu postbacks typically have data like "menu_action"
- Custom menu postbacks may have other patterns
- Need to identify all possible menu selection patterns

### Cancellation Call Sequence
When a menu selection is detected:
1. Identify the user ID from the event
2. Get all registered processors that implement `IProcessorCancellationHandler`
3. Call `CancelOperationsAsync` on each processor
4. Continue with normal menu processing

### Error Handling
- Cancellation failures should not block the main operation
- Log errors but continue processing
- Consider adding retry logic for critical cancellations

## Implementation Steps

### Step 1: Modify LineWebhookCommand
```csharp
// In ProcessPostbackEvent method
if (IsMenuSelection(messageText))
{
    // Cancel previous operations for all processors
    await CancelPreviousOperationsForUser(userId, cancellationToken);
}

// New helper method
private async Task CancelPreviousOperationsForUser(string userId, CancellationToken cancellationToken)
{
    foreach (var processor in _messageProcessors)
    {
        if (processor is IProcessorCancellationHandler cancellationHandler)
        {
            try
            {
                await cancellationHandler.CancelOperationsAsync(userId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cancelling operations for processor {ProcessorName} and user {UserId}", 
                    processor.Name, userId);
            }
        }
    }
}

// Helper to detect menu selections
private bool IsMenuSelection(string messageText)
{
    // Identify patterns that indicate menu selections
    return messageText.StartsWith("menu_") || 
           messageText.StartsWith("office_selected_") ||
           messageText.StartsWith("current_location_selected_") ||
           // Add other patterns as needed
           false;
}
```

### Step 2: Update Processors
Each processor that maintains session state should implement `IProcessorCancellationHandler`:

```csharp
public class TrackFileProcessor : ILineMessageProcessor, IProcessorCancellationHandler
{
    // Existing implementation...
    
    public async Task CancelOperationsAsync(string userId, CancellationToken cancellationToken = default)
    {
        try
        {
            // Remove any pending file uploads or session data
            await _cache.RemoveAsync($"pending_upload:{userId}", cancellationToken);
            // Add other session data cleanup as needed
            
            _logger.LogInformation("Cancelled TrackFile operations for user {UserId}", userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cancelling TrackFile operations for user {UserId}", userId);
        }
    }
}
```

## Benefits of This Approach

1. **Modularity**: Each processor manages its own session data cleanup
2. **Extensibility**: New processors can easily implement cancellation
3. **Reliability**: Cancellation failures don't block new operations
4. **Maintainability**: Clear separation of concerns

## Risks and Mitigations

1. **Performance Impact**: Multiple cancellation calls could slow down menu selection
   - Mitigation: Run cancellations in parallel where possible
   - Mitigation: Optimize cancellation methods to be lightweight

2. **Incomplete Cancellation**: Some processors might miss session data
   - Mitigation: Comprehensive review of each processor
   - Mitigation: Add logging to track what gets cancelled

3. **Race Conditions**: New operation might start before cancellation completes
   - Mitigation: Ensure cancellation is fast and completes before new operation starts
   - Mitigation: Consider adding explicit synchronization if needed