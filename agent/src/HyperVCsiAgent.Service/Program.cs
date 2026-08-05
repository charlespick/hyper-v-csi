using HyperVCsiAgent.Core;
using HyperVCsiAgent.Core.Cluster;
using HyperVCsiAgent.Core.Configuration;
using HyperVCsiAgent.Core.HostControl;
using HyperVCsiAgent.Core.Jobs;
using HyperVCsiAgent.Core.Security;
using HyperVCsiAgent.Core.Storage;
using HyperVCsiAgent.Service.Cluster;
using HyperVCsiAgent.Service.HostControl;
using HyperVCsiAgent.Service.Security;
using HyperVCsiAgent.Service.Storage;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// The agent runs as a Failover Cluster Generic Service resource, so it has to
// answer the SCM's start control or the cluster gives up on it (error 1053)
// and never brings the role online. This also moves the content root off the
// SCM's working directory - C:\Windows\System32 - and sends logs to the event
// log, which is where an operator looks when a clustered role won't start.
// It is a no-op when the process isn't running as a service, so dotnet run is
// unaffected.
builder.Services.AddWindowsService(options => options.ServiceName = "hyperv-csi-agent");

// Config comes from a file on the CSV named by --config, not from
// appsettings-next-to-the-exe or per-host environment variables: the clustered
// role's command line has to resolve identically on whichever host starts the
// process. Added last so it wins over the built-in sources.
var configPath = builder.Configuration["config"];
if (!string.IsNullOrWhiteSpace(configPath))
{
    builder.Configuration.AddJsonFile(Path.GetFullPath(configPath), optional: false, reloadOnChange: false);
}

// Bound once and shared: Kestrel has to be configured before the container
// exists, so binding a second copy for it would let the startup guards below
// vouch for a listener they don't describe.
var agentOptions = builder.Configuration
    .GetSection(AgentOptions.SectionName)
    .Get<AgentOptions>() ?? new AgentOptions();

builder.Services.ConfigureHttpJsonOptions(options => AgentJson.Apply(options.SerializerOptions));
builder.Services.AddSingleton(Options.Create(agentOptions));
builder.Services.AddSingleton<IJobStore, InMemoryJobStore>();

if (OperatingSystem.IsWindows())
{
    builder.Services.AddSingleton<IVirtualDiskManager, CimVirtualDiskManager>();
    builder.Services.AddSingleton<IClusterService, MsClusterService>();
    builder.Services.AddSingleton<IHyperVHostClient, CimHyperVHostClient>();
}
else
{
    builder.Services.AddSingleton<IVirtualDiskManager, UnsupportedVirtualDiskManager>();
    builder.Services.AddSingleton<IClusterService, UnsupportedClusterService>();
    builder.Services.AddSingleton<IHyperVHostClient, UnsupportedHyperVHostClient>();
}

builder.Services.AddSingleton<IVhdxService, VhdxService>();
builder.Services.AddSingleton<IAttachService, AttachService>();
builder.Services.AddSingleton<JobDispatcher>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<StoreCertificateProvider>();
builder.Services.AddSingleton<IServerCertificateProvider>(
    services => services.GetRequiredService<StoreCertificateProvider>());

builder.ConfigureHttps(agentOptions);

// appsettings.json's AllowedHosts stays "*" as the safe fallback for
// Development / before TLS is configured. Once the clustered role's DNS name
// is known from config, narrow Kestrel's host-header validation to that one
// name instead of leaving it wide open.
if (agentOptions.Tls.IsConfigured)
{
    builder.Services.Configure<Microsoft.AspNetCore.HostFiltering.HostFilteringOptions>(
        options => options.AllowedHosts = new[] { agentOptions.Tls.SubjectName });
}

var app = builder.Build();

// Fail at startup rather than on the first CreateVolume: a missing
// CsvVolumesRoot is a deployment mistake, and the cluster failing to bring the
// role online is a far louder signal than volumes that silently never provision.
agentOptions.Validate();

// Running without TLS or without client authentication is a development
// convenience, never a deployment state. Anything that can reach an unsecured
// agent can create and delete volumes on the CSV, so refuse to start rather
// than let a misconfigured deployment come up quietly serving plaintext.
ProductionSecurityGate.Enforce(app.Environment.IsDevelopment(), agentOptions, app.Logger);

// Read the certificate store now rather than at the first handshake. Otherwise
// a mistyped store or a missing certificate produces a role the cluster happily
// reports Online whose every connection fails - the loudest possible symptom in
// the quietest possible place.
if (agentOptions.Tls.IsConfigured)
{
    app.Services.GetRequiredService<StoreCertificateProvider>().Warmup();
}

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

/// <summary>
/// Named so the test host can boot this exact application. The wire format the
/// Go client depends on is a property of the configured host, not of
/// AgentJson alone, so it has to be asserted against the real thing.
/// </summary>
public partial class Program;
