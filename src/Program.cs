using System.Text.Json;
using Azure.Communication.CallAutomation;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration["ACS_CONNECTION_STRING"];
if (string.IsNullOrWhiteSpace(connectionString))
{
  throw new InvalidOperationException("ACS_CONNECTION_STRING is required. Run 'make provision' first.");
}

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
  options.ForwardedHeaders = ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedProto;
});
builder.Services.AddSingleton(new CallAutomationClient(connectionString));

var app = builder.Build();

app.UseForwardedHeaders();


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
