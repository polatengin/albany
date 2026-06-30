# Albany

Albany is a small Azure Communication Services call automation sample. It answers an incoming phone call, plays a greeting, listens to the caller, and prints the recognized text from the other side of the call.

The app keeps the main call script intentionally small:

```csharp
var app = AlbanyApplication.Create(args);

app.AnswerIncomingCalls(IncomingCall);

app.Run();

static async Task IncomingCall(CallLine line)
{
  await line.SendGreetingMessage("Hello from Albany. Your call is connected.");

  var whatTheySaid = await line.ListenToOtherSide();

  Console.WriteLine($"Other side said: {whatTheySaid ?? "(nothing recognized)"}");
}
```

All infrastructure setup, webhook handling, Azure Communication Services calls, speech setup, callback correlation, and JSON event parsing live behind `AlbanyApplication`.

## Why This Matters

Voice applications are usually awkward to prototype because they need public webhooks, telephony resources, event callbacks, speech services, and local development plumbing all working at once. Albany packages those moving parts into a repeatable workflow so you can focus on call behavior instead of infrastructure glue.

This project is useful as a starting point for:

- Voice assistants that answer real phone calls.
- Call triage and intake workflows.
- Prototypes that combine telephony, text-to-speech, and speech recognition.
- Experiments with event-driven call automation in Azure.

## How It Works

Albany has two main pieces:

- `Makefile`: provisions Azure resources, starts a localtunnel public URL, configures Event Grid, and runs the app locally.
- `src/`: contains the ASP.NET Core app that receives ACS and Event Grid callbacks.

At runtime, the flow is:

1. A caller dials a phone number connected to the Azure Communication Services resource.
2. Azure Communication Services emits a `Microsoft.Communication.IncomingCall` Event Grid event.
3. Event Grid sends that event to `/api/incoming-call` on the local app through localtunnel.
4. The app answers the call and tells ACS to send call automation callbacks to `/api/calls/callbacks`.
5. When ACS sends `CallConnected`, the app invokes your `IncomingCall` function in `Program.cs`.
6. `SendGreetingMessage(...)` plays a text-to-speech greeting and waits for playback to complete.
7. `ListenToOtherSide()` starts speech recognition against the active caller participant and waits for `RecognizeCompleted`.
8. The recognized text is returned to `Program.cs` and printed to the console.

## Prerequisites

You need:

- An Azure subscription.
- Azure CLI authenticated with `az login`.
- GitHub CLI authenticated with `gh auth login` if you want reusable state saved to the repository variable `ALBANY_STATE`.
- Node.js and npm, used by `npx localtunnel`.
- .NET SDK compatible with the project target framework in `src/cli.csproj`.
- An Azure Communication Services phone number or call route that can receive incoming calls for the ACS resource.

The dev container for this repository already includes common tools such as `az`, `gh`, `node`, `npm`, and the .NET SDK.

## Setup

Sign in to Azure:

```bash
az login
```

Optionally sign in to GitHub CLI so provisioned resource names can be restored on another machine:

```bash
gh auth login
```

Provision the Azure resources and Event Grid subscription:

```bash
make provision
```

Run the local app and tunnel:

```bash
make run
```

When `make run` starts, it prints the public callback URL:

```text
Incoming-call webhook: https://albany-<PROJECT_SUFFIX>.loca.lt/api/incoming-call
```

Call the phone number associated with the ACS resource. You should hear the greeting, then the app will listen and print what it recognized:

```text
Other side said: hello, this is a test
```

## Provisioning Details

`make provision` creates or reuses:

- Azure resource group: `rg-albany-<PROJECT_SUFFIX>`.
- Azure Communication Services resource: `acs-albany-<PROJECT_SUFFIX>`.
- Azure AI Services account: `ai-albany-<PROJECT_SUFFIX>`.
- A managed identity on the ACS resource.
- A `Cognitive Services User` role assignment from ACS to the AI Services account.
- An Event Grid subscription for `Microsoft.Communication.IncomingCall` events.

The Makefile writes local runtime state to `.albany.env`. That file is ignored by Git because it contains sensitive values such as the ACS connection string.

If GitHub CLI is authenticated, `make provision` also saves the reusable non-secret resource identifiers to the repository variable `ALBANY_STATE`. On a different machine, if `.albany.env` is missing, `make provision` can restore those identifiers from `ALBANY_STATE` and then re-fetch sensitive values from Azure.

## Runtime Configuration

The app reads configuration from environment variables. The Makefile writes these to `.albany.env` after provisioning:

| Variable | Purpose |
| --- | --- |
| `ACS_CONNECTION_STRING` | Connection string used by `CallAutomationClient`. |
| `COGNITIVE_SERVICES_ENDPOINT` | Azure AI Services endpoint used for call intelligence, text-to-speech, and speech recognition. |
| `CALLBACK_BASE_URL` | Public base URL used by ACS callbacks, usually the localtunnel URL. |
| `TTS_VOICE_NAME` | Optional voice name for the greeting. Defaults to `en-US-JennyNeural`. |
| `SPEECH_LOCALE` | Optional recognition locale. Defaults to `en-US`. |

You normally do not edit `.albany.env` by hand. Run `make provision` to create it, then `make run` to use it.

## Important Files

- `Program.cs`: the call behavior you want to customize.
- `AlbanyApplication.cs`: ACS webhook routing, answering calls, playing prompts, speech recognition, callback correlation, and app setup.
- `cli.csproj`: project settings, package references, and global usings.
- `Makefile`: Azure provisioning, state restore/save, localtunnel startup, and app execution.

## Development Loop

Build the app:

```bash
dotnet build ./src/cli.csproj
```

Run the app with tunnel and Event Grid validation:

```bash
make run
```

Change call behavior by editing `Program.cs`. For example, you can add more prompts, branch on `whatTheySaid`, or call other services after recognition.

## Troubleshooting

### localtunnel returned a random subdomain

Albany asks localtunnel for a deterministic URL like `https://albany-<PROJECT_SUFFIX>.loca.lt`. If localtunnel returns a random URL, the requested subdomain is usually already occupied by a stale localtunnel process or by another client.

The Makefile tries to clean up stale localtunnel processes before starting a new one. If the problem persists, stop old Node localtunnel processes for this project and rerun `make run`.

### Speech recognition says `(nothing recognized)`

The app logs the raw `RecognizeCompleted` payload when ACS completes recognition without text. Look for:

```text
Speech recognition completed without text. Payload: ...
```

Common causes are speaking before listening starts, background noise, an incorrect `SPEECH_LOCALE`, or ACS returning an empty recognition result. Wait until after the greeting finishes and the terminal logs `Listening for speech on call ...`, then speak clearly.

### Event Grid says the subscription is not provisioned

Run:

```bash
make provision
```

Provisioning starts the local app temporarily so Event Grid can validate the webhook endpoint.

### The app says `ACS_CONNECTION_STRING is required`

Run:

```bash
make provision
```

That creates `.albany.env` with the connection string and other runtime values.

## Cleanup

To remove Azure resources created by this sample, delete the resource group:

```bash
az group delete --name "$RESOURCE_GROUP_NAME"
```

If you do not have `.albany.env` sourced, replace `$RESOURCE_GROUP_NAME` with the actual resource group name, such as `rg-albany-28137`.

## Customizing The Call

Keep `Program.cs` focused on the conversation flow. Put infrastructure and protocol details in `AlbanyApplication.cs`.

For example:

```csharp
static async Task IncomingCall(CallLine line)
{
  await line.SendGreetingMessage("Thanks for calling. How can I help?");

  var request = await line.ListenToOtherSide();

  Console.WriteLine($"Caller request: {request}");
}
```
