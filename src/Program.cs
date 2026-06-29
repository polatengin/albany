var app = AlbanyApplication.Create(args);

app.AnswerIncomingCalls(IncomingCall);

app.Run();

static async Task IncomingCall(CallLine line)
{
  await line.SendGreetingMessage("Hello from Albany. Your call is connected.");
  await line.ListenToOtherSide();
}
