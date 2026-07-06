using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using BrokerAi.Core.Data;
using BrokerAi.Core.Options;
using BrokerAi.Core.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices((context, services) =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        var config = context.Configuration;

        services.Configure<MetaOptions>(config.GetSection(MetaOptions.Section));
        services.Configure<AnthropicOptions>(config.GetSection(AnthropicOptions.Section));
        services.Configure<FacebookOptions>(config.GetSection(FacebookOptions.Section));
        services.Configure<AppOptions>(config.GetSection(AppOptions.Section));

        var sqlConnectionString = config.GetConnectionString("AppFmDb")
            ?? throw new InvalidOperationException("Missing ConnectionStrings:AppFmDb");
        var storageConnectionString = config["AzureWebJobsStorage"] ?? "UseDevelopmentStorage=true";

        services.AddDbContext<BrokerAiDbContext>(opt =>
            opt.UseSqlServer(sqlConnectionString, sql => sql.EnableRetryOnFailure()));
        services.AddDbContextFactory<BrokerAiDbContext>(opt =>
            opt.UseSqlServer(sqlConnectionString, sql => sql.EnableRetryOnFailure()));

        services.AddSingleton(new BlobServiceClient(storageConnectionString));
        services.AddSingleton(new QueueServiceClient(storageConnectionString));

        services.AddHttpClient<IWhatsAppSender, WhatsAppSender>();
        services.AddHttpClient<IFacebookService, FacebookService>();
        services.AddHttpClient<IMediaService, MediaService>();

        services.AddScoped<MessageRouter>();
        services.AddSingleton<IClaudeGateway, ClaudeService>();
    })
    .Build();

host.Run();
