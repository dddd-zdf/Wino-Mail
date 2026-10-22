namespace Wino.Core.Domain.Models.Accounts;

public sealed class WinoAccountSyncImportResult
{
    public bool IncludedPreferences { get; init; }
    public bool IncludedAccounts { get; init; }
    public bool HadRemotePreferences { get; init; }
    public int AppliedPreferenceCount { get; init; }
    public int FailedPreferenceCount { get; init; }
    public int ImportedMailboxCount { get; init; }
    public int SkippedDuplicateMailboxCount { get; init; }
    public int RemoteMailboxCount { get; init; }

    /// <summary>
    /// Number of imported mailboxes that came back with their passwords and need no sign-in.
    /// </summary>
    public int RestoredCredentialCount { get; init; }

    /// <summary>
    /// Number of accounts whose settings, signatures or folder layout were updated from the payload.
    /// </summary>
    public int AppliedAccountDataCount { get; init; }

    /// <summary>
    /// Number of folders whose navigation layout was applied directly. Folders that do not exist yet
    /// are parked and applied when the synchronizer creates them, so they are not counted here.
    /// </summary>
    public int AppliedFolderConfigurationCount { get; init; }

    /// <summary>
    /// Number of app data items (templates, rules, shortcuts, categories, aliases, merged inboxes)
    /// created or updated from the snapshot.
    /// </summary>
    public int AppliedAppDataCount { get; init; }

    /// <summary>
    /// Theme and layout carried by the snapshot. Applying it touches the window, so the caller
    /// applies it on the UI thread through <see cref="Interfaces.IWinoAccountDataSyncService.ApplyAppearance"/>.
    /// </summary>
    public SyncSnapshotAppearance? Appearance { get; init; }

    /// <summary>
    /// Some imported mailbox came back without a password and has to be signed in again.
    /// </summary>
    public bool NeedsSignIn => ImportedMailboxCount > RestoredCredentialCount;

    public bool HasAnyRemoteData => HadRemotePreferences || RemoteMailboxCount > 0 || AppliedAccountDataCount > 0 || AppliedAppDataCount > 0 || Appearance != null;
}
