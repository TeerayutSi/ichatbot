# Rich Menu Implementation for LINE OA Bot

This document describes the implementation of Rich Menu functionality for the LINE OA Bot.

## Overview

The Rich Menu implementation creates a custom menu for LINE users when they interact with the bot. The menu has the following features:

1. Two rows layout:
   - Row 1: Registration button (full width) - sends "ลงทะเบียน" message
   - Row 2: Two buttons (50% width each):
     - Check-in button - sends "check-in" message
     - Check-out button - sends "check-out" message

2. Background image from "D:\oabg.png"

3. Automatically linked to all users when they start chatting with the bot

## Implementation Details

### Components

1. **Rich Menu Models** - Located in `src/Application/Common/Models/LineRichMenu.cs`
   - `LineRichMenu` - Main Rich Menu object
   - `RichMenuSize` - Size of the Rich Menu (2500x1686 pixels)
   - `RichMenuArea` - Area definition with bounds and action
   - `RichMenuBounds` - Position and size of an area
   - `RichMenuAction` - Action to perform when area is tapped

2. **Configuration** - Added to `src/Web/appsettings.json`
   ```json
   "LineRichMenu": {
     "BackgroundImagePath": "D:\\oabg.png",
     "Name": "WorkingTimeMenu",
     "ChatBarText": "เมนูการทำงาน",
     "Selected": true
   }
   ```

3. **Background Service** - `src/Infrastructure/BackgroundServices/RichMenuBackgroundServiceV3.cs`
   - Runs once at application startup (after a 5-second delay to ensure application is initialized)
   - Creates Rich Menu for all chatbots with LINE channel access tokens
   - Uploads background image
   - Links Rich Menu as default for all users
   - Includes improved error handling and logging
   - Better validation of access tokens and image files

4. **Dependency Injection** - Registered in `src/Infrastructure/DependencyInjection.cs`
   ```csharp
   services.AddHostedService<RichMenuBackgroundServiceV3>();
   ```

## How It Works

1. When the application starts, the `RichMenuBackgroundService` runs once
2. It retrieves all chatbots that have LINE channel access tokens from the database
3. For each chatbot:
   - Creates a Rich Menu with the specified layout using the LINE Messaging API
   - Uploads the background image from the configured path
   - Sets the created Rich Menu as the default for all users

## Testing

To test the Rich Menu implementation:

1. Ensure the background image exists at the configured path (`D:\oabg.png`)
2. Start the application
3. Check the application logs for messages from `RichMenuBackgroundService`
4. Look for log entries like:
   - "Starting Rich Menu creation process"
   - "Creating Rich Menu for chatbot {ChatbotId}"
   - "Successfully created and linked Rich Menu {RichMenuId} for chatbot {ChatbotId}"
   - "Rich Menu creation process completed"

5. Open the LINE OA and start a conversation with the bot
6. The Rich Menu should appear automatically

## Required Packages

The implementation uses the standard .NET HTTP client and does not require additional packages.

## Configuration

The following configuration values can be adjusted in `appsettings.json`:

- `LineRichMenu:BackgroundImagePath` - Path to the background image file
- `LineRichMenu:Name` - Name of the Rich Menu (for identification)
- `LineRichMenu:ChatBarText` - Text displayed in the chat bar when the Rich Menu is active
- `LineRichMenu:Selected` - Whether the Rich Menu should be selected by default

## Troubleshooting

If the Rich Menu does not appear:

1. Check that the background image exists at the configured path
2. Verify that the chatbot has a valid LINE channel access token in the database
3. Check the application logs for any error messages from `RichMenuBackgroundService`
4. Ensure the LINE channel has the proper permissions for Rich Menu API access

## API Endpoints Used

The implementation uses the following LINE Messaging API endpoints:

1. `POST /v2/bot/richmenu` - Create Rich Menu
2. `POST /v2/bot/richmenu/{richMenuId}/content` - Upload Rich Menu image
3. `POST /v2/bot/user/all/richmenu/{richMenuId}` - Link Rich Menu to all users

## Test Utility

A test utility class `TestRichMenuCreation.cs` is included in the `src/Infrastructure/BackgroundServices` directory.
This class can be used to manually trigger Rich Menu creation for testing purposes. To use it:

1. Register it as a hosted service in `DependencyInjection.cs` by uncommenting the following line:
   ```csharp
   // services.AddHostedService<TestRichMenuCreation>();
   ```

2. Set the environment variable `RUN_RICHMENU_TEST=true` when starting the application

3. Check the application logs for test execution messages

Note: The test utility works with `RichMenuBackgroundServiceV3` which includes improved error handling and logging.

## Manual Tester Utility

A manual tester utility `ManualRichMenuTester.cs` is included in the `src/Infrastructure/BackgroundServices` directory.
This class can be used to manually create Rich Menu for testing purposes without running the full application.

To use it, you would need to create a simple console application that:
1. Configures the necessary services (IConfiguration, ILogger)
2. Creates an instance of ManualRichMenuTester
3. Calls the TestRichMenuCreationAsync method with a valid LINE channel access token

This utility is particularly useful for troubleshooting Rich Menu creation issues without having to restart the entire application.

A sample console application `Program.cs` is also provided in the same directory to demonstrate how to use the ManualRichMenuTester.