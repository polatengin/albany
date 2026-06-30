var app = AlbanyApplication.Create(args);

app.AnswerIncomingCalls(IncomingCall);

app.Run();

static async Task IncomingCall(CallLine line)
{
  await line.SendGreetingMessage("Hello from Albany. Your call is connected.");

  var whatTheySaid = await line.ListenToOtherSide();

  Console.WriteLine($"Other side said: {whatTheySaid ?? "(nothing recognized)"}");
}
