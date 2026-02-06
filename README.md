# Cutover Monitor

Azure Function App for monitoring Logic App cutovers during Integration Account migrations.

## Features

- **Timer-based monitoring** - Checks every 5 minutes for issues
- **Failure detection** - Detects failed runs and failover triggers
- **SMS alerts** - Via Twilio when issues detected
- **Auto-cutback** - Optional automatic rollback on failure
- **Scheduled cutovers** - Set start/end times
- **Alert deduplication** - Won't spam SMS
- **Full audit log** - All actions tracked in Table Storage

## API Endpoints

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/api/status` | GET | Status of all cutovers |
| `/api/cutover/{name}/start` | POST | Start cutover |
| `/api/cutover/{name}/stop` | POST | Stop cutover |
| `/api/cutover/{name}` | PUT | Create/update config |
| `/api/audit` | GET | Recent audit logs |
| `/api/test/sms` | POST | Test SMS |

## Configuration

Set these in `local.settings.json` or Azure App Settings:

- `AzureSubscriptionId` - Azure subscription ID
- `AzureResourceGroup` - Resource group with Logic Apps
- `TableStorageConnection` - Azure Table Storage connection string
- `TwilioAccountSid` - Twilio account SID
- `TwilioAuthToken` - Twilio auth token
- `TwilioMessagingServiceSid` - Twilio messaging service SID
- `AlertPhoneNumber` - Phone number for SMS alerts

## Local Development

1. Copy `local.settings.template.json` to `local.settings.json`
2. Fill in your configuration values
3. Run: `func start`

## Deployment

Deploy to Azure Functions using VS Code, Visual Studio, or Azure CLI.
