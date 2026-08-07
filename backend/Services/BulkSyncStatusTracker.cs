namespace Spectra.Api.Services;

public record BulkSyncStatus(bool IsRunning, int Completed, int Total, int? CurrentCustomerId, string? CurrentCustomerName);

// Tracks whether CustomerCollectionService.CollectAllAsync is currently
// running (triggered manually via POST /api/customers/sync-all, or by
// CustomerSyncBackgroundService's scheduled timer) and which customer it's
// on, so Settings can show "Syncing 3 of 12: Acme Corp…" instead of a bare
// spinner for however long a full sync takes. Registered Singleton for the
// same reason as CollectionLockRegistry/CollectionProgressTracker — has to
// be the same instance across the request that starts a sync and the
// separate requests polling its status.
public class BulkSyncStatusTracker
{
    private readonly object gate = new();
    private bool isRunning;
    private int completed;
    private int total;
    private int? currentCustomerId;
    private string? currentCustomerName;

    // False (without starting anything) if a sync is already in progress —
    // CollectAllAsync uses this to make a second concurrent call a no-op
    // rather than running two overlapping full-tenant sweeps.
    public bool TryStart(int totalCustomers)
    {
        lock (gate)
        {
            if (isRunning)
            {
                return false;
            }
            isRunning = true;
            completed = 0;
            total = totalCustomers;
            currentCustomerId = null;
            currentCustomerName = null;
            return true;
        }
    }

    public void ReportCustomer(int customerId, string customerName, int completedSoFar)
    {
        lock (gate)
        {
            currentCustomerId = customerId;
            currentCustomerName = customerName;
            completed = completedSoFar;
        }
    }

    public void Finish()
    {
        lock (gate)
        {
            isRunning = false;
            currentCustomerId = null;
            currentCustomerName = null;
        }
    }

    public BulkSyncStatus GetStatus()
    {
        lock (gate)
        {
            return new BulkSyncStatus(isRunning, completed, total, currentCustomerId, currentCustomerName);
        }
    }
}
