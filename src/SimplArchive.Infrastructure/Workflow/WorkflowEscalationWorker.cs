using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Infrastructure.Workflow;

// Periodically sweeps in-review workflow tasks for SLA reminders + overdue escalations (ADR "Workflow
// escalation / SLA reminders"), off the request path. Registered unconditionally in AddInfrastructure. The
// sweep is a no-op for tasks with no deadline or already handled, so a frequent tick is cheap.
public sealed class WorkflowEscalationWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WorkflowEscalationWorker> _logger;

    public WorkflowEscalationWorker(IServiceScopeFactory scopeFactory, ILogger<WorkflowEscalationWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} started (interval {Interval}).", nameof(WorkflowEscalationWorker), Interval);
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await SweepAsync(stoppingToken);
                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var escalation = scope.ServiceProvider.GetRequiredService<IWorkflowEscalationService>();
            var acted = await escalation.SweepAsync(cancellationToken);
            if (acted > 0)
            {
                _logger.LogInformation("Workflow escalation sweep notified on {Count} task(s).", acted);
            }
            else
            {
                _logger.LogDebug("Workflow escalation sweep found no tasks needing a reminder or escalation.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Workflow escalation sweep failed; will retry next interval.");
        }
    }
}
