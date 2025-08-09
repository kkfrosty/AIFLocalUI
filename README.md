# AI Foundry Local - .NET WPF UI

A Windows WPF application to manage and chat with AI Foundry Local models.

## Features

- **Model Management**: Lists available models using `foundry model list`
- **Service Control**: Start/stop models using `foundry model run {model}`
- **Chat Interface**: OpenAI-compatible chat once service is running
- **System Monitoring**: Real-time CPU, Memory, Disk, GPU utilization
- **Progress Tracking**: Shows model download and loading progress

## Prerequisites

- Windows 10/11
- .NET 9 SDK
- AI Foundry Local installed and accessible via `foundry` command
- PowerShell execution policy allowing script execution

## Quick Start

```powershell
# Clone and build
cd AIFLocalUI
dotnet build .\AiFoundryUI.sln

# Run the application
dotnet run --project .\src\AiFoundryUI\AiFoundryUI.csproj
```

## Configuration

Edit `src\AiFoundryUI\appsettings.json`:

```json
{
  "start_command_template": "foundry model run {model}",
  "api_base": "http://localhost:8000/v1",
  "health_url": "http://localhost:8000/health",
  "openai_compatible": true,
  "models": ["gpt-oss-20b", "llama3.1", "phi-3.5"]
}
```

## Usage

1. **Load Models**: Models are automatically loaded on app startup using `foundry model list`
2. **Select Model**: Choose a model from the dropdown (populated with model aliases)
3. **Start Service**: Click "Start Service" to run `foundry model run {selected-model}`
4. **Monitor Progress**: Watch the service logs for download/loading progress and service URL detection
5. **Chat**: Once loaded, the app auto-detects the service URL and you can chat with the model

## Service Output Example

```
foundry model run gpt-oss-20b
🟢 Service is Started on http://127.0.0.1:52356/, PID 3728!
Downloading gpt-oss-20b-cuda-gpu...
[####################################] 100.00 % [Time remaining: about 0s] 51.3 MB/s
🕐 Loading model...
🟢 Model gpt-oss-20b-cuda-gpu loaded successfully
```

The app will automatically extract the service URL (`http://127.0.0.1:52356/`) and update the API base for chat.

## Architecture

- **Models/Config.cs**: Configuration management and JSON serialization
- **Models/ChatMessage.cs**: Chat message structure
- **Services/AiFoundryLocalClient.cs**: AI Foundry Local CLI and REST API integration
- **Services/ProcessManager.cs**: PowerShell process execution and monitoring
- **Services/ChatClient.cs**: OpenAI-compatible chat client for inference
- **Services/SystemMonitor.cs**: Real-time system metrics using performance counters
- **MainWindow.xaml**: WPF UI layout with chat, logs, and metrics
- **MainWindow.xaml.cs**: UI event handling and application logic

## API Integration

The app uses both AI Foundry Local's CLI and REST API:

- **CLI**: `foundry model list` to get available models, `foundry model run {model}` to start services
- **REST API**: 
  - `GET /foundry/list` - Get detailed model information
  - `GET /openai/status` - Check service status
  - `GET /openai/loadedmodels` - Get currently loaded models
  - `POST /v1/chat/completions` - OpenAI-compatible chat endpoint

## Build and Deploy

```powershell
# Development build
dotnet build .\AiFoundryUI.sln

# Release build
dotnet build .\AiFoundryUI.sln -c Release

# Single file deployment
dotnet publish .\src\AiFoundryUI\AiFoundryUI.csproj -c Release -r win-x64 -p:PublishSingleFile=true --self-contained false
```

## Troubleshooting

- **Models not loading**: Ensure `foundry model list` works in PowerShell
- **Service won't start**: Check that the selected model exists and `foundry model run` works manually
- **GPU metrics showing 0%**: GPU performance counters may not be available on all systems
- **Chat not working**: Verify the service URL from logs and check `api_base` configuration
