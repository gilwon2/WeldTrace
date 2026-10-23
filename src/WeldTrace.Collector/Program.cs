using WeldTrace.Collector;
using WeldTrace.Data;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<CollectorOptions>(builder.Configuration.GetSection("Collector"));
builder.Services.AddSingleton(_ =>
{
    var connectionString = builder.Configuration.GetConnectionString("WeldTrace")
                           ?? throw new InvalidOperationException("ConnectionStrings:WeldTrace 설정이 없습니다.");
    return new IngestService(connectionString);
});
builder.Services.AddSingleton<InboxProcessor>();
builder.Services.AddHostedService<CollectorWorker>();

builder.Build().Run();
