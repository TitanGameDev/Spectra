using System.Collections.Concurrent;

namespace Spectra.Api.Services;

// Prevents two concurrent collection runs for the same customer from racing
// each other — a manual "Collect data" click and a scheduled
// CustomerSyncBackgroundService run, or two manual clicks in a row. A
// standalone singleton rather than living inside CustomerCollectionService
// itself, since that service is registered Scoped (it depends on the scoped
// SpectraDbContext) but this lock table must be shared across every scope.
public class CollectionLockRegistry
{
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _locks = new();

    public SemaphoreSlim GetLock(int customerId) => _locks.GetOrAdd(customerId, _ => new SemaphoreSlim(1, 1));
}
