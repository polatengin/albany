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

