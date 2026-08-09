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

// Closed until OrphanedCheckpointReaper's startup sweep has finished
// discovery-and-enqueue - see that class's own remarks, and the gate check
// on POST /v1/jobs below, for why a job store that will happily accept
// anything still needs one.
builder.Services.AddSingleton<JobIntakeGate>();

if (OperatingSystem.IsWindows())
{
    builder.Services.AddSingleton<IVirtualDiskManager, CimVirtualDiskManager>();
    builder.Services.AddSingleton<IClusterService, MsClusterService>();
    builder.Services.AddSingleton<IHyperVHostClient, CimHyperVHostClient>();
    builder.Services.AddSingleton<IDiskCopier, WindowsDiskCopier>();
}
else
{
    builder.Services.AddSingleton<IVirtualDiskManager, UnsupportedVirtualDiskManager>();
    builder.Services.AddSingleton<IClusterService, UnsupportedClusterService>();
    builder.Services.AddSingleton<IHyperVHostClient, UnsupportedHyperVHostClient>();
    builder.Services.AddSingleton<IDiskCopier, UnsupportedDiskCopier>();
}

#if DEBUG
// Converts issue #14's D10 invariant - every operation that resolves a VM and
// issues a call against it holds vm:<nodeId> for the entire duration of those
// calls - into a thrown InvalidOperationException the moment it is violated,
// rather than a race a real host might only occasionally lose. Wraps
// whichever IHyperVHostClient the branch above registered instead of asking
// again which branch that was, so this cannot drift from it. Not in Release:
// the cost there is a pointless allocation and comparison on the hot path of
// every VM mutation, for a check whose only job is turning "we remembered to
// reason about this" into "the test suite fails if someone forgets" during
// development.
var hostClientDescriptor = builder.Services.Single(d => d.ServiceType == typeof(IHyperVHostClient));
builder.Services.Remove(hostClientDescriptor);
builder.Services.AddSingleton<IHyperVHostClient>(services => new VmTargetAssertingHyperVHostClient(
    (IHyperVHostClient)ActivatorUtilities.CreateInstance(services, hostClientDescriptor.ImplementationType!)));
#endif

// Shared by VhdxService's restore-from-snapshot copy and SnapshotService's own
// copy: one cap on concurrent bulk copies against the CSV, not one per caller.
// See SnapshotCopySlots for why two separate semaphores would double it.
builder.Services.AddSingleton<SnapshotCopySlots>();

// Shared by AttachService's attach/detach and SnapshotService's checkpoint
// take, classify, find and destroy: one cap per Hyper-V host, not one per
// caller. See HostOperationSlots for why two separate caps would double it -
// issue #14's D4.
builder.Services.AddSingleton<HostOperationSlots>();

builder.Services.AddSingleton<IVhdxService, VhdxService>();
builder.Services.AddSingleton<IAttachService, AttachService>();

// Depends on IJobStore, which is registered above and depends on nothing: the
// snapshot service starts its own long-running copy job rather than doing the
// copy inline. The dependency runs one way only - JobDispatcher sits above both
// - so there is no cycle for the container to resolve.
builder.Services.AddSingleton<ISnapshotService, SnapshotService>();

// Depends on ISnapshotService (registered just above) for ResumeCopy and
// ReapOrphan, and on JobIntakeGate to open once its startup sweep is done -
// no dependency runs the other way, so there is no cycle for the container
// to resolve. It deliberately does not also take IJobStore directly:
// everything it needs to enqueue is already reachable through
// ISnapshotService, which is the one thing that actually knows how to build
// a copy or a merge job, so a second, narrower path to the same store would
// only be one more thing to keep in sync with it.
builder.Services.AddHostedService<OrphanedCheckpointReaper>();

// Independent of OrphanedCheckpointReaper and JobIntakeGate entirely - see
// SnapshotStorageWarningService's own remarks for why it is kept as its own
// hosted service rather than folded into the reaper that owns the gate.
builder.Services.AddHostedService<SnapshotStorageWarningService>();

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
        options => options.AllowedHosts = new[] { agentOptions.Tls.HostName });
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
    JobIntakeGate gate,
    IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> jsonOptions) =>
{
    // Closed until OrphanedCheckpointReaper's startup sweep has finished
    // discovery-and-enqueue for every orphan it found (see that class's own
    // remarks) - a window that has to close before this handler enqueues
    // anything, or an RPC-driven job could claim vm:<id> ahead of a recovery
    // job the sweep has not enqueued yet, reopening the exact race issue
    // #14's second comment describes. 503 rather than blocking the request:
    // csi-driver/internal/driver/jobs.go's enqueueFailed maps any non-2xx
    // response here to codes.Unavailable, which every CSI sidecar already
    // retries with backoff - so a brief window of these is a handful of
    // retries, not a fault, and Kestrel itself keeps answering the
    // connection rather than this handler hanging until the gate opens.
    if (!gate.IsOpen)
    {
        return Results.Json(new { error = "the agent is still recovering orphaned checkpoints; retry shortly" },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    if (string.IsNullOrWhiteSpace(request.OperationType))
    {
        return Results.BadRequest(new { error = "operationType is required" });
    }

    if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
    {
        return Results.BadRequest(new { error = "idempotencyKey is required" });
    }

    // No target is read from the request: the dispatcher derives what this job
    // must not interleave with from the payload it is already decoding. See
    // EnqueueJobRequest for why that is not the controller's to say.
    JobDispatcher.ResolvedJob resolved;
    try
    {
        resolved = dispatcher.Resolve(request.OperationType, request.Payload, jsonOptions.Value.SerializerOptions);
    }
    catch (InvalidJobRequestException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    var job = jobStore.GetOrCreate(request.IdempotencyKey, request.OperationType, resolved.Targets, resolved.Run);
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
