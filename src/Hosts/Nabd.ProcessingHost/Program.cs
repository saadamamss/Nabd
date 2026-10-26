using Nabd.ProcessingHost;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<ProcessingWorker>();

var host = builder.Build();
host.Run();
