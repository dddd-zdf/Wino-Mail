using Wino.Core.Domain.Models.Accounts;

namespace Wino.Messaging.UI;

/// <summary>
/// The welcome window imported accounts and hands over to the main window. The restored
/// theme travels with it and is applied only after the main window is active: applying it
/// on the welcome window queues work on a window that is about to close.
/// <see cref="NeedsSignIn"/> is false when every imported mailbox came back with its password.
/// </summary>
public record WelcomeImportCompletedMessage(int ImportedMailboxCount, SyncSnapshotAppearance? Appearance = null, bool NeedsSignIn = true) : UIMessageBase<WelcomeImportCompletedMessage>;
