namespace Wino.Core.Domain.Models.Accounts;

/// <param name="IncludeCredentials">
/// Puts the passwords of IMAP, POP3 and DAV accounts in the snapshot. File backups only: the Wino
/// Account export ignores it so passwords never reach the service, even encrypted.
/// </param>
public sealed record WinoAccountSyncSelection(
    bool IncludePreferences = true,
    bool IncludeAccounts = true,
    bool IncludeCredentials = false);
