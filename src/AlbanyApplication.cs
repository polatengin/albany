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
