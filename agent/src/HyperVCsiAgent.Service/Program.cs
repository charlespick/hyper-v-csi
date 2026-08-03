using HyperVCsiAgent.Core.Jobs;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IJobStore, InMemoryJobStore>();

var app = builder.Build();

app.MapGet("/healthz", () => Results.Ok());

// Enqueues a job and returns immediately; the HTTP listener never blocks on a
// multi-minute Hyper-V operation. Real request/response DTOs and dispatch to
// the CSI operation handlers land once those handlers exist.
app.MapPost("/v1/jobs", () => Results.StatusCode(StatusCodes.Status501NotImplemented));

app.MapGet("/v1/jobs/{id}", (string id, IJobStore jobStore) =>
{
    var job = jobStore.Get(id);
    return job is null ? Results.NotFound() : Results.Ok(job);
});

app.Run();
