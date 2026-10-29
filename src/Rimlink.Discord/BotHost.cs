using System.Net.Http.Headers;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Rimlink.Discord.Api;
using Rimlink.Discord.Chat;
using Rimlink.Discord.Commands;
using Rimlink.Discord.Feed;
using Rimlink.Discord.Gateway;
using Rimlink.Discord.Options;
using Rimlink.Discord.Text;

namespace Rimlink.Discord;

/// <summary>Builds the bot: configuration (<c>appsettings.json</c>, environment <c>RimlinkBot__…</c>), services and the two workers.</summary>
public static class BotHost
{
    public static IConfiguration Configuration(string[] args) => new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .AddEnvironmentVariables()
        .AddCommandLine(args)
        .Build();

    /// <summary>Writes every configuration problem to <paramref name="output"/>; 1 when the bot cannot start, else 0.</summary>
    public static int Check(IConfiguration configuration, TextWriter output)
    {
        var errors = BotOptions.Validate(configuration.GetSection(BotOptions.Section).Get<BotOptions>() ?? new BotOptions());
        foreach (string error in errors) output.WriteLine(error);
        return errors.Count == 0 ? 0 : 1;
    }

    public static IHost Create(string[] args, IConfiguration? overrides = null)
    {
        var builder = Host.CreateApplicationBuilder(args);
        if (overrides is not null) builder.Configuration.AddConfiguration(overrides);
        var section = builder.Configuration.GetSection(BotOptions.Section);
        var options = section.Get<BotOptions>() ?? new BotOptions();
        builder.Services.Configure<BotOptions>(section);

        var s = builder.Services;
        void Server(HttpClient client, TimeSpan timeout)
        {
            client.BaseAddress = new Uri(options.Server.Url.TrimEnd('/') + "/api/admin/");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.Server.ApiToken);
            client.Timeout = timeout;
        }
        s.AddHttpClient("api", c => Server(c, TimeSpan.FromSeconds(10))).ConfigurePrimaryHttpMessageHandler(() => ServerHandler.Create(options.Server));
        s.AddHttpClient("feed", c => Server(c, Timeout.InfiniteTimeSpan)).ConfigurePrimaryHttpMessageHandler(() => ServerHandler.Create(options.Server));

        s.AddSingleton(TimeProvider.System);
        s.AddSingleton(sp => new RimlinkApi(sp.GetRequiredService<IHttpClientFactory>().CreateClient("api")));
        s.AddSingleton(new FeedReaderOptions());
        s.AddSingleton(sp => new FeedReader(() => sp.GetRequiredService<IHttpClientFactory>().CreateClient("feed"),
            sp.GetRequiredService<FeedReaderOptions>(), sp.GetRequiredService<ILogger<FeedReader>>()));
        s.AddSingleton(new Strings(options.Discord.Language is "ru" ? "ru" : "en"));
        s.AddSingleton<Formatter>();
        var c = options.Discord.Channels;
        s.AddSingleton(new ChannelMap(c.Server, c.Raids, c.Market, c.Moderation, c.Chat));
        s.AddSingleton(sp => new OutboundQueue(sp.GetRequiredService<IDiscordOut>(), sp.GetRequiredService<ILogger<OutboundQueue>>()));
        s.AddSingleton<ChatBridge>();
        s.AddSingleton(sp => new FeedRouter(sp.GetRequiredService<ChannelMap>(), sp.GetRequiredService<Formatter>(), sp.GetRequiredService<OutboundQueue>(),
            sp.GetRequiredService<Strings>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ChatBridge>().FromGame));
        s.AddSingleton(new Permissions(options.Discord));
        s.AddSingleton<PlayerResolver>();
        s.AddSingleton<Confirmations>();
        s.AddSingleton<CommandHandlers>();

        s.AddSingleton(new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.MessageContent,
            AlwaysDownloadUsers = false,
        }));
        s.AddSingleton(sp => new InteractionService(sp.GetRequiredService<DiscordSocketClient>(), new InteractionServiceConfig
        {
            // Command descriptions in other languages: Strings/commands.<locale>.json; the English ones are the attributes.
            LocalizationManager = new JsonLocalizationManager(Path.Combine(AppContext.BaseDirectory, "Strings"), "commands"),
        }));
        s.AddSingleton<IDiscordOut, DiscordOut>();
        s.AddHostedService<DiscordHost>();
        s.AddHostedService<FeedWorker>();
        return builder.Build();
    }
}
