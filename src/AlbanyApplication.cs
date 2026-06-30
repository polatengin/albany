static class AlbanyApplication
{
  private static readonly ConcurrentDictionary<string, TaskCompletionSource<object?>> PlayOperations = new();
  private static readonly ConcurrentDictionary<string, TaskCompletionSource<string?>> RecognitionOperations = new();

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
      builder.Configuration["SPEECH_LOCALE"] ?? "en-US");

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

        if (eventType == "Microsoft.Communication.PlayStarted")
        {
          logger.LogInformation("Text-to-speech event payload: {Payload}", eventElement.GetRawText());
          continue;
        }

        if (eventType == "Microsoft.Communication.PlayCompleted")
        {
          CompletePlayOperation(eventElement);
          logger.LogInformation("Text-to-speech completed: {Payload}", eventElement.GetRawText());
          continue;
        }

        if (eventType == "Microsoft.Communication.PlayFailed")
        {
          FailPlayOperation(eventElement, "Text-to-speech failed.");
          logger.LogWarning("Text-to-speech failed: {Payload}", eventElement.GetRawText());
          continue;
        }

        if (eventType == "Microsoft.Communication.RecognizeCompleted")
        {
          var recognizedSpeech = GetRecognizedSpeech(eventElement);
          CompleteRecognitionOperation(eventElement, recognizedSpeech);
          logger.LogInformation("Speech recognition completed: {RecognizedSpeech}", recognizedSpeech ?? "(nothing recognized)");
          if (string.IsNullOrWhiteSpace(recognizedSpeech))
          {
            logger.LogInformation("Speech recognition completed without text. Payload: {Payload}", eventElement.GetRawText());
          }
          continue;
        }

        if (eventType?.Contains("Transcription", StringComparison.OrdinalIgnoreCase) == true)
        {
          logger.LogInformation("Transcription event: {Payload}", eventElement.GetRawText());
          continue;
        }

        if (eventType is "Microsoft.Communication.RecognizeFailed" or "Microsoft.Communication.RecognizeCanceled")
        {
          FailRecognitionOperation(eventElement, "Speech recognition failed or was canceled.");
          logger.LogWarning("Speech recognition event: {Payload}", eventElement.GetRawText());
        }
      }

      return Results.Ok();
    });

    return app;
  }

  internal static async Task WaitForPlayCompletedAsync(string operationContext, Func<Task> startPlay)
  {
    var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
    if (!PlayOperations.TryAdd(operationContext, completion))
    {
      throw new InvalidOperationException($"A play operation already exists for operation context '{operationContext}'.");
    }

    try
    {
      await startPlay();
      using var timeout = CreateOperationTimeout(operationContext, PlayOperations, completion);
      await completion.Task;
    }
    finally
    {
      PlayOperations.TryRemove(operationContext, out _);
    }
  }

  internal static async Task<string?> WaitForRecognizedSpeechAsync(string operationContext, Func<Task> startRecognition)
  {
    var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
    if (!RecognitionOperations.TryAdd(operationContext, completion))
    {
      throw new InvalidOperationException($"A recognition operation already exists for operation context '{operationContext}'.");
    }

    try
    {
      await startRecognition();
      using var timeout = CreateOperationTimeout(operationContext, RecognitionOperations, completion);
      return await completion.Task;
    }
    finally
    {
      RecognitionOperations.TryRemove(operationContext, out _);
    }
  }

  private static CancellationTokenSource CreateOperationTimeout<T>(
    string operationContext,
    ConcurrentDictionary<string, TaskCompletionSource<T>> operations,
    TaskCompletionSource<T> completion)
  {
    var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    timeout.Token.Register(() =>
    {
      if (operations.TryRemove(operationContext, out _))
      {
        completion.TrySetException(new TimeoutException($"Timed out waiting for operation context '{operationContext}'."));
      }
    });

    return timeout;
  }

  private static void CompletePlayOperation(JsonElement eventElement)
  {
    if (TryGetOperationContext(eventElement, out var operationContext) &&
      PlayOperations.TryRemove(operationContext, out var completion))
    {
      completion.TrySetResult(null);
    }
  }

  private static void FailPlayOperation(JsonElement eventElement, string message)
  {
    if (TryGetOperationContext(eventElement, out var operationContext) &&
      PlayOperations.TryRemove(operationContext, out var completion))
    {
      completion.TrySetException(new InvalidOperationException(message));
    }
  }

  private static void CompleteRecognitionOperation(JsonElement eventElement, string? recognizedSpeech)
  {
    if (TryGetOperationContext(eventElement, out var operationContext) &&
      RecognitionOperations.TryRemove(operationContext, out var completion))
    {
      completion.TrySetResult(recognizedSpeech);
    }
  }

  private static void FailRecognitionOperation(JsonElement eventElement, string message)
  {
    if (TryGetOperationContext(eventElement, out var operationContext) &&
      RecognitionOperations.TryRemove(operationContext, out var completion))
    {
      completion.TrySetException(new InvalidOperationException(message));
    }
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

  private static bool TryGetOperationContext(JsonElement eventElement, out string operationContext)
  {
    operationContext = string.Empty;

    if (!eventElement.TryGetProperty("data", out var data) ||
      !data.TryGetProperty("operationContext", out var operationContextElement))
    {
      return false;
    }

    operationContext = operationContextElement.GetString() ?? string.Empty;
    return !string.IsNullOrWhiteSpace(operationContext);
  }

  private static string? GetRecognizedSpeech(JsonElement eventElement)
  {
    try
    {
      var recognizeCompleted = RecognizeCompleted.Deserialize(eventElement.GetRawText());
      if (recognizeCompleted.RecognizeResult is SpeechResult speechResult &&
        !string.IsNullOrWhiteSpace(speechResult.Speech))
      {
        return speechResult.Speech;
      }
    }
    catch (JsonException)
    {
    }

    if (!eventElement.TryGetProperty("data", out var data))
    {
      return null;
    }

    return FindStringProperty(data, "speech");
  }

  private static string? FindStringProperty(JsonElement element, string propertyName)
  {
    if (element.ValueKind == JsonValueKind.Object)
    {
      foreach (var property in element.EnumerateObject())
      {
        if (property.NameEquals(propertyName) && property.Value.ValueKind == JsonValueKind.String)
        {
          return property.Value.GetString();
        }

        var nestedValue = FindStringProperty(property.Value, propertyName);
        if (!string.IsNullOrWhiteSpace(nestedValue))
        {
          return nestedValue;
        }
      }
    }

    if (element.ValueKind == JsonValueKind.Array)
    {
      foreach (var item in element.EnumerateArray())
      {
        var nestedValue = FindStringProperty(item, propertyName);
        if (!string.IsNullOrWhiteSpace(nestedValue))
        {
          return nestedValue;
        }
      }
    }

    return null;
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

    var operationContext = NewOperationContext("greeting");
    var playOptions = new PlayToAllOptions(new TextSource(text, speechOptions.VoiceName))
    {
      OperationContext = operationContext
    };
    var callMedia = callAutomationClient
      .GetCallConnection(callConnectionId)
      .GetCallMedia();

    await AlbanyApplication.WaitForPlayCompletedAsync(
      operationContext,
      async () => await callMedia.PlayToAllAsync(playOptions));

    logger.LogInformation("Completed greeting playback on call {CallConnectionId} with voice {VoiceName}.", callConnectionId, speechOptions.VoiceName);
  }

  public async Task<string?> ListenToOtherSide()
  {
    if (!speechOptions.HasCognitiveServicesEndpoint)
    {
      logger.LogWarning("Skipping listening because COGNITIVE_SERVICES_ENDPOINT is not configured.");
      return null;
    }

    var callConnection = callAutomationClient.GetCallConnection(callConnectionId);
    var participants = (await callConnection.GetParticipantsAsync()).Value;
    var targetParticipant = participants.FirstOrDefault()?.Identifier;
    if (targetParticipant is null)
    {
      logger.LogWarning("Skipping listening because the call has no active participant to recognize.");
      return null;
    }

    var operationContext = NewOperationContext("listen");
    var recognizeOptions = new CallMediaRecognizeSpeechOptions(targetParticipant)
    {
      OperationContext = operationContext,
      SpeechLanguage = speechOptions.SpeechLocale,
      InitialSilenceTimeout = TimeSpan.FromSeconds(20),
      EndSilenceTimeout = TimeSpan.FromSeconds(2)
    };
    var callMedia = callAutomationClient
      .GetCallConnection(callConnectionId)
      .GetCallMedia();

    logger.LogInformation("Listening for speech on call {CallConnectionId}.", callConnectionId);

    string? recognizedSpeech;
    try
    {
      recognizedSpeech = await AlbanyApplication.WaitForRecognizedSpeechAsync(
        operationContext,
        async () => await callMedia.StartRecognizingAsync(recognizeOptions));
    }
    catch (Azure.RequestFailedException exception)
    {
      logger.LogWarning(exception, "Could not start speech recognition on call {CallConnectionId}.", callConnectionId);
      return null;
    }
    catch (InvalidOperationException exception)
    {
      logger.LogWarning(exception, "Speech recognition did not complete on call {CallConnectionId}.", callConnectionId);
      return null;
    }
    catch (TimeoutException exception)
    {
      logger.LogWarning(exception, "Speech recognition timed out on call {CallConnectionId}.", callConnectionId);
      return null;
    }

    logger.LogInformation("Recognized speech on call {CallConnectionId}: {RecognizedSpeech}", callConnectionId, recognizedSpeech ?? "(nothing recognized)");
    return recognizedSpeech;
  }

  private static string NewOperationContext(string name) => $"{name}-{Guid.NewGuid():N}";
}

sealed record SpeechOptions(string? CognitiveServicesEndpoint, string VoiceName, string SpeechLocale)
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
