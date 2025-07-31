# WorkingTimeProcessor

## Overview
This processor implements a check-in/check-out system for Line Official Account (Line OA) that allows staff to record their working time with location verification and selfie confirmation.

## Features
1. **Check-in/Check-out Commands**: Users can initiate the process with "เช็คอิน", "check-in", "check in" for check-in or "เช็คเอาต์", "check-out", "check out" for check-out.
2. **Location Verification**: Requests user's current location and verifies they are near a government office.
3. **Agency Selection**: Shows nearby government offices within 500m radius for user to select.
4. **Selfie Confirmation**: Requests a selfie photo to verify the user's identity.
5. **HR System Integration**: Submits all collected data to an HR system via API.

## Implementation Details

### Flow
1. User sends "เช็คอิน" or "เช็คเอาต์" command
2. Bot requests user's location
3. Bot finds nearby government offices using Google Places API
4. Bot shows a FLEX carousel with nearby agencies for selection
5. User selects an agency
6. Bot requests a selfie photo
7. Bot submits all data to HR system

### Data Collected
- Agency name (user selection)
- Location (latitude/longitude from Line location message)
- Current timestamp (server time)
- Type of action (Check-In/Check-Out)
- Photo (from user's upload)
- Line User ID

### Configuration
The processor requires the following configuration in appsettings.json:

```json
{
  "WorkingTime": {
    "GoogleApiKey": "AIzaSyCl2kx23dQsVMQ1sloTsOmZ2fUQ043LHlg",
    "HrSystemApiUrl": "https://your-hr-system.com/api/working-time"
  }
}
```

### Google Places API
Uses the Google Places API with the key: `AIzaSyCl2kx23dQsVMQ1sloTsOmZ2fUQ043LHlg` to find nearby government offices within a 500m radius.

## Session Management
User sessions are stored in IDistributedCache with a 30-minute expiration time. Each session tracks the current step in the process:
- WaitingForLocation
- WaitingForOfficeSelection
- WaitingForPhoto

## Dependencies
- IApplicationDbContext
- IHttpClientFactory
- ILogger<WorkingTimeProcessor>
- IDistributedCache
- IConfiguration
- ISystemService

## Usage
To enable this processor, add "WorkingTime" to the chatbot's plugins in the database.