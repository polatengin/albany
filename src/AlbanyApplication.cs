static class AlbanyApplication
{
  public static WebApplication Create(string[] args)
  {
    var builder = WebApplication.CreateBuilder(args);

    var connectionString = builder.Configuration["ACS_CONNECTION_STRING"];
    if (string.IsNullOrWhiteSpace(connectionString))
    {
      throw new InvalidOperationException("ACS_CONNECTION_STRING is required. Run 'make provision' first.");
    }

    var speechOptions = new SpeechOptions(
      builder.Configuration["COGNITIVE_SERVICES_ENDPOINT"],
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

    return app;
  }

  public static WebApplication AnswerIncomingCalls(this WebApplication app, Func<CallLine, Task> handleCall)
  {
    ArgumentNullException.ThrowIfNull(handleCall);

    app.MapGet("/", (SpeechOptions speechOptions) => Results.Ok(new
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

    app.MapPost("/api/calls/callbacks", async (
      HttpRequest request,
      CallAutomationClient callAutomationClient,
      SpeechOptions speechOptions,
      ILoggerFactory loggerFactory) =>
    {
      var logger = loggerFactory.CreateLogger("CallAutomationCallbacks");
      using var document = await ReadJsonBodyAsync(request);

      foreach (var eventElement in EnumerateEvents(document.RootElement))
      {
        var eventType = GetEventType(eventElement);
        logger.LogInformation("Received call automation event {EventType}.", eventType ?? "unknown");

        if (eventType == "Microsoft.Communication.CallConnected")
        {
          if (!TryGetCallConnectionId(eventElement, out var callConnectionId))
          {
            logger.LogWarning("CallConnected event did not include callConnectionId.");
            continue;
          }

          await handleCall(new CallLine(callAutomationClient, callConnectionId, speechOptions, logger));

          continue;
        }

        if (eventType is "Microsoft.Communication.PlayCompleted" or "Microsoft.Communication.PlayStarted")
        {
          logger.LogInformation("Text-to-speech event payload: {Payload}", eventElement.GetRawText());
          continue;
        }

        if (eventType == "Microsoft.Communication.PlayFailed")
        {
          logger.LogWarning("Text-to-speech failed: {Payload}", eventElement.GetRawText());
          continue;
        }

        if (eventType == "Microsoft.Communication.RecognizeCompleted")
        {
          logger.LogInformation("Speech recognition completed: {Payload}", eventElement.GetRawText());
          continue;
        }

        if (eventType?.Contains("Transcription", StringComparison.OrdinalIgnoreCase) == true)
        {
          logger.LogInformation("Transcription event: {Payload}", eventElement.GetRawText());
          continue;
        }

        if (eventType is "Microsoft.Communication.RecognizeFailed" or "Microsoft.Communication.RecognizeCanceled")
        {
          logger.LogWarning("Speech recognition event: {Payload}", eventElement.GetRawText());
        }
      }

      return Results.Ok();
    });

    return app;
  }

  private static async Task<JsonDocument> ReadJsonBodyAsync(HttpRequest request)
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

  private static IEnumerable<JsonElement> EnumerateEvents(JsonElement rootElement)
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

  private static string? GetEventType(JsonElement eventElement)
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

  private static bool TryGetIncomingCallContext(JsonElement eventElement, out string incomingCallContext)
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

  private static bool TryGetCallConnectionId(JsonElement eventElement, out string callConnectionId)
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

  private static Uri BuildCallbackUri(HttpRequest request, IConfiguration configuration)
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
}

sealed class CallLine
{
  private readonly CallAutomationClient callAutomationClient;
  private readonly string callConnectionId;
  private readonly SpeechOptions speechOptions;
  private readonly ILogger logger;

  public CallLine(CallAutomationClient callAutomationClient, string callConnectionId, SpeechOptions speechOptions, ILogger logger)
  {
    this.callAutomationClient = callAutomationClient;
    this.callConnectionId = callConnectionId;
    this.speechOptions = speechOptions;
    this.logger = logger;
  }

  public async Task SendGreetingMessage(string text)
  {
    if (!speechOptions.HasCognitiveServicesEndpoint)
    {
      logger.LogWarning("Skipping greeting because COGNITIVE_SERVICES_ENDPOINT is not configured.");
      return;
    }

    var playSource = new TextSource(text, speechOptions.VoiceName);
    var callMedia = callAutomationClient
      .GetCallConnection(callConnectionId)
      .GetCallMedia();

    await callMedia.PlayToAllAsync(playSource);

    logger.LogInformation("Started greeting playback on call {CallConnectionId} with voice {VoiceName}.", callConnectionId, speechOptions.VoiceName);
  }

  public async Task ListenToOtherSide()
  {
    if (!speechOptions.HasCognitiveServicesEndpoint)
    {
      logger.LogWarning("Skipping listening because COGNITIVE_SERVICES_ENDPOINT is not configured.");
      return;
    }

    if (!speechOptions.EnableTranscription)
    {
      logger.LogInformation("Skipping listening because ENABLE_TRANSCRIPTION is not true.");
      return;
    }

    var callMedia = callAutomationClient
      .GetCallConnection(callConnectionId)
      .GetCallMedia();

    await callMedia.StartTranscriptionAsync(new StartTranscriptionOptions
    {
      Locale = speechOptions.SpeechLocale,
      OperationContext = "default-transcription"
    });

    logger.LogInformation("Started speech transcription on call {CallConnectionId} with locale {SpeechLocale}.", callConnectionId, speechOptions.SpeechLocale);
  }
}

sealed record SpeechOptions(string? CognitiveServicesEndpoint, string VoiceName, string SpeechLocale, bool EnableTranscription)
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
