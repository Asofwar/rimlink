using Rimlink.Discord;

int check = BotHost.Check(BotHost.Configuration(args), Console.Error);
if (check != 0) return check;

await BotHost.Create(args).RunAsync();
return 0;
