using System.Text.Json;
using Azure.Communication.CallAutomation;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration["ACS_CONNECTION_STRING"];
if (string.IsNullOrWhiteSpace(connectionString))
{
  throw new InvalidOperationException("ACS_CONNECTION_STRING is required. Run 'make provision' first.");
}

var speechOptions = new SpeechOptions(
  builder.Configuration["COGNITIVE_SERVICES_ENDPOINT"],
  builder.Configuration["TTS_GREETING_TEXT"] ?? "Hello from Albany. Your call is connected.",
  builder.Configuration["TTS_VOICE_NAME"] ?? "en-US-JennyNeural",
  builder.Configuration["SPEECH_LOCALE"] ?? "en-US",
  bool.TryParse(builder.Configuration["ENABLE_TRANSCRIPTION"], out var enableTranscription) && enableTranscription);

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
  options.ForwardedHeaders = ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedProto;
});
builder.Services.AddSingleton(new CallAutomationClient(connectionString));
builder.Services.AddSingleton(speechOptions);

var app = builder.Build();

app.UseForwardedHeaders();

app.MapGet("/", () => Results.Ok(new
{
  service = "albany-call-listener",
  incomingCallWebhook = "/api/incoming-call",
  callAutomationCallbacks = "/api/calls/callbacks",
  speechEnabled = speechOptions.HasCognitiveServicesEndpoint
}));

app.MapPost("/api/incoming-call", async (
  HttpRequest request,
  IConfiguration configuration,
  SpeechOptions speechOptions,
  CallAutomationClient callAutomationClient,
  ILoggerFactory loggerFactory) =>
{
  var logger = loggerFactory.CreateLogger("IncomingCalls");
  using var document = await ReadJsonBodyAsync(request);

  var answeredCalls = 0;

  foreach (var eventElement in EnumerateEvents(document.RootElement))
  {
    var eventType = GetEventType(eventElement);
    if (eventType == "Microsoft.EventGrid.SubscriptionValidationEvent")
    {
      var validationCode = eventElement.GetProperty("data").GetProperty("validationCode").GetString();
      logger.LogInformation("Validated Event Grid subscription.");
      return Results.Ok(new { validationResponse = validationCode });
    }

    if (eventType != "Microsoft.Communication.IncomingCall")
    {
      logger.LogInformation("Ignored Event Grid event {EventType}.", eventType ?? "unknown");
      continue;
    }

    if (!TryGetIncomingCallContext(eventElement, out var incomingCallContext))
    {
      logger.LogWarning("Incoming call event did not include incomingCallContext.");
      continue;
    }

    var callbackUri = BuildCallbackUri(request, configuration);
    var answerCallOptions = new AnswerCallOptions(incomingCallContext, callbackUri);
    if (speechOptions.TryGetCognitiveServicesEndpoint(out var cognitiveServicesEndpoint))
    {
      answerCallOptions.CallIntelligenceOptions = new CallIntelligenceOptions
      {
        CognitiveServicesEndpoint = cognitiveServicesEndpoint
      };
    }
    else
    {
      logger.LogWarning("COGNITIVE_SERVICES_ENDPOINT is not set. The call will be answered, but text-to-speech and speech-to-text actions will be skipped.");
    }

    await callAutomationClient.AnswerCallAsync(answerCallOptions);

    answeredCalls++;
    logger.LogInformation("Answered incoming call. Callback URI: {CallbackUri}", callbackUri);
  }

  return Results.Ok(new { answeredCalls });
});

app.MapPost("/api/calls/callbacks", async (HttpRequest request, ILoggerFactory loggerFactory) =>
{
  var logger = loggerFactory.CreateLogger("CallAutomationCallbacks");
  using var document = await ReadJsonBodyAsync(request);

  foreach (var eventElement in EnumerateEvents(document.RootElement))
  {
    logger.LogInformation("Received call automation event {EventType}.", GetEventType(eventElement) ?? "unknown");
  }

  return Results.Ok();
});

app.Run();

static async Task<JsonDocument> ReadJsonBodyAsync(HttpRequest request)
{
  try
  {
    return await JsonDocument.ParseAsync(request.Body);
  }
  catch (JsonException exception)
  {
    throw new BadHttpRequestException("Request body must be valid JSON.", exception);
  }
}

static IEnumerable<JsonElement> EnumerateEvents(JsonElement rootElement)
{
  if (rootElement.ValueKind == JsonValueKind.Array)
  {
    foreach (var eventElement in rootElement.EnumerateArray())
    {
      yield return eventElement;
    }

    yield break;
  }

  yield return rootElement;
}

static string? GetEventType(JsonElement eventElement)
{
  if (eventElement.TryGetProperty("eventType", out var eventType))
  {
    return eventType.GetString();
  }

  if (eventElement.TryGetProperty("type", out var cloudEventType))
  {
    return cloudEventType.GetString();
  }

  return null;
}

static bool TryGetIncomingCallContext(JsonElement eventElement, out string incomingCallContext)
{
  incomingCallContext = string.Empty;

  if (!eventElement.TryGetProperty("data", out var data) ||
    !data.TryGetProperty("incomingCallContext", out var incomingCallContextElement))
  {
    return false;
  }

  incomingCallContext = incomingCallContextElement.GetString() ?? string.Empty;
  return !string.IsNullOrWhiteSpace(incomingCallContext);
}

static bool TryGetCallConnectionId(JsonElement eventElement, out string callConnectionId)
{
  callConnectionId = string.Empty;

  if (!eventElement.TryGetProperty("data", out var data) ||
    !data.TryGetProperty("callConnectionId", out var callConnectionIdElement))
  {
    return false;
  }

  callConnectionId = callConnectionIdElement.GetString() ?? string.Empty;
  return !string.IsNullOrWhiteSpace(callConnectionId);
}

static Uri BuildCallbackUri(HttpRequest request, IConfiguration configuration)
{
  var configuredBaseUrl = configuration["CALLBACK_BASE_URL"];
  if (!string.IsNullOrWhiteSpace(configuredBaseUrl))
  {
    return new Uri(new Uri(configuredBaseUrl.TrimEnd('/') + "/"), "api/calls/callbacks");
  }

  var scheme = request.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? request.Scheme;
  var host = request.Headers["X-Forwarded-Host"].FirstOrDefault() ?? request.Host.Value;

  return new Uri($"{scheme}://{host}/api/calls/callbacks");
}

sealed record SpeechOptions(string? CognitiveServicesEndpoint, string GreetingText, string VoiceName, string SpeechLocale, bool EnableTranscription)
{
  public bool HasCognitiveServicesEndpoint => !string.IsNullOrWhiteSpace(CognitiveServicesEndpoint);

  public bool TryGetCognitiveServicesEndpoint(out Uri cognitiveServicesEndpoint)
  {
    if (!string.IsNullOrWhiteSpace(CognitiveServicesEndpoint) &&
      Uri.TryCreate(CognitiveServicesEndpoint, UriKind.Absolute, out cognitiveServicesEndpoint!))
    {
      return true;
    }

    cognitiveServicesEndpoint = null!;
    return false;
  }
}
