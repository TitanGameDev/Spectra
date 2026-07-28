using Microsoft.EntityFrameworkCore;
using Spectra.Api.Data;

namespace Spectra.Api.Services;

// Runs CustomerCollectionService for every customer on a timer, so security
// data stays fresh without an admin needing to click "Collect data" — the
// exact same collection logic manual collection uses, just on a schedule.
// Configured via Sync:IntervalHours (Sync__IntervalHours in .env); defaults
// to 6h if unset, 0 disables it entirely.
//
// Deliberately serial across customers, not parallel: EXO/SCC PowerShell
// app-only sessions are capped per app/tenant, and spinning up several pwsh
// processes at once for unrelated customers isn't worth the resource spike
// for a background job nobody is waiting on. A slow cycle just pushes the
// next one later (the delay is measured from the END of one cycle to the
// START of the next), so cycles never overlap.
public class CustomerSyncBackgroundService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<CustomerSyncBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalHours = configuration.GetValue<double?>("Sync:IntervalHours") ?? 6;
        if (intervalHours <= 0)
        {
            logger.LogInformation("Automatic customer sync is disabled (Sync:IntervalHours <= 0).");
            return;
        }

        var interval = TimeSpan.FromHours(intervalHours);
        logger.LogInformation("Automatic customer sync enabled, every {IntervalHours}h", intervalHours);

        // Give the app a minute to finish starting up before the first run.
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await SyncAllCustomersAsync(stoppingToken);

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SyncAllCustomersAsync(CancellationToken ct)
    {
        // A fresh scope per cycle (not per customer) — CustomerCollectionService
        // is Scoped because it depends on the scoped SpectraDbContext, and reusing
        // one DbContext across a sequential loop of SaveChanges calls is a normal,
        // supported pattern (no concurrent access, since nothing here runs in parallel).
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpectraDbContext>();
        var collectionService = scope.ServiceProvider.GetRequiredService<CustomerCollectionService>();

        List<Customer> customers;
        try
        {
            customers = await db.Customers.ToListAsync(ct);
        }
        catch (Exception ex)
        {
            // e.g. the active database was mid-cutover or briefly unreachable —
            // skip this cycle rather than crash the whole hosted service.
            logger.LogError(ex, "Automatic sync couldn't load the customer list this cycle");
            return;
        }

        logger.LogInformation("Automatic sync starting for {Count} customer(s)", customers.Count);
        foreach (var customer in customers)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            try
            {
                // CollectAsync saves internally — see CustomerCollectionService.
                await collectionService.CollectAsync(customer);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Automatic sync failed for customer {CustomerId}", customer.Id);
            }
        }
        logger.LogInformation("Automatic sync finished");
    }
}
