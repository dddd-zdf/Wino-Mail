using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Supplies an Exchange Online IMAP token for the instant new mail watcher of an Outlook account.
/// IMAP is a separate resource from Graph, so the token is acquired on its own.
///
/// The watcher is only a change signal: an account that has not consented to IMAP gets no
/// token, and mail keeps arriving through the scheduled Graph synchronization.
/// </summary>
public interface IOutlookImapTokenProvider
{
    /// <summary>
    /// Returns a token without showing UI, or null when the account has not granted IMAP access.
    /// </summary>
    Task<string> GetImapTokenAsync(MailAccount account, bool forceRefresh = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Shows the Microsoft consent prompt for IMAP access once.
    /// </summary>
    Task EnsureImapConsentAsync(MailAccount account, CancellationToken cancellationToken = default);
}
