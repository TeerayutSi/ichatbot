# Background Services - Rich Menu Testing

This directory contains utilities for testing and troubleshooting Rich Menu creation for LINE OA Bot.

## Files

1. `RichMenuBackgroundServiceV3.cs` - The main background service that automatically creates Rich Menu at application startup
2. `TestRichMenuCreation.cs` - A test utility that can be registered as a hosted service for manual testing
3. `ManualRichMenuTester.cs` - A utility class that can be used to manually create Rich Menu without running the full application
4. `Program.cs` - A sample console application that demonstrates how to use the ManualRichMenuTester

## How to Test Rich Menu Creation

### Method 1: Using the TestRichMenuCreation service

1. Register the TestRichMenuCreation service in `DependencyInjection.cs` by uncommenting the following line:
   ```csharp
   // services.AddHostedService<TestRichMenuCreation>();
   ```

2. Set the environment variable `RUN_RICHMENU_TEST=true` when starting the application

3. Check the application logs for test execution messages

### Method 2: Using the ManualRichMenuTester (Recommended for troubleshooting)

1. Create a simple console application or use the provided `Program.cs`

2. Run the console application and provide your LINE channel access token when prompted

3. Check the console output for detailed logs about the Rich Menu creation process

## Troubleshooting

If the Rich Menu is not showing up in LINE:

1. Check that the background image exists at the configured path (`D:\oabg.png`)

2. Verify that the LINE channel access token is valid

3. Check the application logs for any error messages

4. Use the ManualRichMenuTester to get more detailed error information

5. Ensure the LINE channel has the proper permissions for Rich Menu API access