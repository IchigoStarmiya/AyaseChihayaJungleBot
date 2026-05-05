using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Hosting.Services;
using NetCord.Hosting.Services.ApplicationCommands;
using VoiceTimerBot.BackgroundServices;
using VoiceTimerBot.Interfaces;
using VoiceTimerBot.Services;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddSentry(options =>
{
    options.Dsn = builder.Configuration["Dsn"];
    options.MinimumEventLevel = LogLevel.Error;
    options.Environment = builder.Environment.EnvironmentName;
    options.AutoSessionTracking = true;
    options.Debug = builder.Environment.IsDevelopment();
});

builder.Services.Configure<VoiceTimerSettings>(
    builder.Configuration.GetSection("VoiceTimer"));

builder.Services.AddSingleton<IVoiceTimerService, VoiceTimerService>();

builder.Services.AddHostedService<SentryHeartbeatService>();

builder.Services
       .AddDiscordGateway(options =>
        {
            options.Intents = GatewayIntents.GuildVoiceStates | GatewayIntents.GuildMessages | GatewayIntents.MessageContent;
        })
       .AddApplicationCommands()
       .AddGatewayHandlers(typeof(Program).Assembly);

var host = builder.Build();

host.AddModules(typeof(Program).Assembly);

await host.RunAsync();
