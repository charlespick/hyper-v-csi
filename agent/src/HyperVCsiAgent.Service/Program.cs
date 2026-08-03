using HyperVCsiAgent.Core;
using HyperVCsiAgent.Core.Configuration;
using HyperVCsiAgent.Core.Jobs;
using HyperVCsiAgent.Core.Storage;
using HyperVCsiAgent.Service.Storage;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Config comes from a file on the CSV named by --config, not from
// appsettings-next-to-the-exe or per-host environment variables: the clustered
// role's command line has to resolve identically on whichever host starts the
// process. Added last so it wins over the built-in sources.
var configPath = builder.Configuration["config"];
if (!string.IsNullOrWhiteSpace(configPath))
{
    builder.Configuration.AddJsonFile(Path.GetFullPath(configPath), optional: false, reloadOnChange: false);
}

builder.Services.ConfigureHttpJsonOptions(options => AgentJson.Apply(options.SerializerOptions));
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.SectionName));
builder.Services.AddSingleton<IJobStore, InMemoryJobStore>();

if (OperatingSystem.IsWindows())
{
    builder.Services.AddSingleton<IVirtualDiskManager, CimVirtualDiskManager>();
}
else
{
    builder.Services.AddSingleton<IVirtualDiskManager, UnsupportedVirtualDiskManager>();
}

builder.Services.AddSingleton<IVhdxService, VhdxService>();
builder.Services.AddSingleton<JobDispatcher>();

var app = builder.Build();

// Fail at startup rather than on the first CreateVolume: a missing
// CsvVolumesRoot is a deployment mistake, and the cluster failing to bring the
// role online is a far louder signal than volumes that silently never provision.
app.Services.GetRequiredService<IOptions<AgentOptions>>().Value.Validate();

app.MapGet("/healthz", () => Results.Ok());

// Enqueues a job and returns immediately; the HTTP listener never blocks on a
// multi-minute Hyper-V operation. A 400 means the request itself was unusable
// and no job exists; a job that runs and fails is a 202 followed by a Failed
// status on GET /v1/jobs/{id}.
app.MapPost("/v1/jobs", (
    EnqueueJobRequest request,
    IJobStore jobStore,
    JobDispatcher dispatcher,
    IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> jsonOptions) =>
{
    if (string.IsNullOrWhiteSpace(request.OperationType))
    {
        return Results.BadRequest(new { error = "operationType is required" });
    }

    if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
    {
        return Results.BadRequest(new { error = "idempotencyKey is required" });
    }

    if (string.IsNullOrWhiteSpace(request.Target))
    {
        return Results.BadRequest(new { error = "target is required" });
    }

    Func<Job, CancellationToken, Task> run;
    try
    {
        run = dispatcher.Resolve(request.OperationType, request.Payload, jsonOptions.Value.SerializerOptions);
    }
    catch (InvalidJobRequestException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    var job = jobStore.GetOrCreate(request.IdempotencyKey, request.OperationType, request.Target, run);
    return Results.Accepted($"/v1/jobs/{job.Id}", job);
});

app.MapGet("/v1/jobs/{id}", (string id, IJobStore jobStore) =>
{
    var job = jobStore.Get(id);
    return job is null ? Results.NotFound() : Results.Ok(job);
});

app.Run();
