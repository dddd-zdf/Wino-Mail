using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// An API synchronizer that keeps a live connection to its provider and asks for a mail
/// synchronization as soon as the mailbox changes, ahead of the scheduled synchronization.
/// </summary>
public interface IMailChangeWatcherSynchronizer
{
    Task StartChangeWatcherAsync();

    /// <summary>
    /// Stops the watcher. Used when the account starts needing attention and on teardown.
    /// </summary>
    Task StopChangeWatcherAsync();
}
