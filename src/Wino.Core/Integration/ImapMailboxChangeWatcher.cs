using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Serilog;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Extensions;

namespace Wino.Core.Integration;

/// <summary>
/// How an API account's mailbox is reached over IMAP. The token is an OAuth access token for
/// XOAUTH2; a null token means the account has not granted IMAP access, and the watcher stays off.
/// </summary>
internal sealed record ImapChangeWatcherConnection(
    string ProviderName,
    string Host,
    Func<string> GetUserName,
    Func<bool, CancellationToken, Task<string>> GetAccessTokenAsync);

/// <summary>
/// Holds one IMAP IDLE connection to the Inbox of an API account (Gmail, Outlook) as a change
/// signal. It downloads nothing: every change it hears about asks the synchronizer for its
/// regular API sync, which stays the only data path. The scheduled synchronization remains the
/// fallback when IMAP is unavailable for the account.
/// </summary>
internal sealed class ImapMailboxChangeWatcher
{
    private const int ImapPort = 993;

    // Gmail ends IDLE commands after about 30 minutes. Renewing earlier also exposes dead sockets.
    private static readonly TimeSpan IdleRenewInterval = TimeSpan.FromMinutes(9);

    // Servers report one arrival as several untagged responses; they share one sync. Requests
    // stay this far apart so a burst of responses does not start a sync per response.
    private static readonly TimeSpan FirstChangeDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MinimumRequestSpacing = TimeSpan.FromSeconds(3);

    // IMAP can report a new message before the provider API lists it. A wake-up sync that finds
    // nothing asks again after these delays, which keeps verification codes quick.
    private static readonly TimeSpan[] FollowUpDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)];
    private static readonly TimeSpan PendingCheckInterval = TimeSpan.FromMilliseconds(500);

    private static readonly Random ReconnectJitter = new();

    private readonly MailAccount _account;
    private readonly ImapChangeWatcherConnection _connection;
    private readonly Action _changeDetected;
    private readonly ILogger _logger = Log.ForContext<ImapMailboxChangeWatcher>();

    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _idleDoneLock = new();
    private readonly object _changeLock = new();

    private CancellationTokenSource _loopCancellationTokenSource;
    private CancellationTokenSource _idleDoneCancellationTokenSource;
    private Task _loopTask;
    private Task _changeNotificationTask;
    private volatile bool _stopping;
    private DateTime? _nextRequestDueUtc;
    private DateTime _lastChangeRequestUtc = DateTime.MinValue;
    private int _followUpAttempts;

    public ImapMailboxChangeWatcher(MailAccount account, ImapChangeWatcherConnection connection, Action changeDetected)
    {
        _account = account ?? throw new ArgumentNullException(nameof(account));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _changeDetected = changeDetected ?? throw new ArgumentNullException(nameof(changeDetected));
    }

    public async Task StartAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_loopTask is { IsCompleted: false }) return;
            _loopCancellationTokenSource?.Dispose();
            _stopping = false;
            _loopCancellationTokenSource = new CancellationTokenSource();
            var token = _loopCancellationTokenSource.Token;
            _loopTask = Task.Run(() => RunLoopAsync(token));
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            _stopping = true;
            lock (_idleDoneLock)
            {
                // MailKit requires DONE before canceling the command token on TLS streams.
                if (_idleDoneCancellationTokenSource != null)
                {
                    _idleDoneCancellationTokenSource.Cancel();
                    _loopCancellationTokenSource?.CancelAfter(TimeSpan.FromSeconds(5));
                }
                else _loopCancellationTokenSource?.Cancel();
            }
            try
            {
                if (_loopTask != null) await _loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            finally
            {
                _loopCancellationTokenSource?.Cancel();
                Task notifications;
                lock (_changeLock) notifications = _changeNotificationTask;
                if (notifications != null) await notifications.ConfigureAwait(false);
                _loopCancellationTokenSource?.Dispose();
                _loopCancellationTokenSource = null;
                _loopTask = null;
                lock (_changeLock)
                {
                    _nextRequestDueUtc = null;
                    _followUpAttempts = 0;
                }
            }
        }
        finally { _lifecycle.Release(); }
    }

    /// <summary>
    /// Asks for a synchronization shortly. The synchronizer also calls this when it deferred a
    /// requested sync because another one was still pending.
    /// </summary>
    public void RequestSynchronization() => ScheduleRequest(FirstChangeDelay, isFollowUp: false);

    /// <summary>
    /// Called by the synchronizer when a wake-up sync found no history yet. Checks again a few
    /// times with growing delays, until the next mailbox change starts a new round.
    /// </summary>
    public void RequestFollowUp() => ScheduleRequest(TimeSpan.Zero, isFollowUp: true);

    private void ScheduleRequest(TimeSpan delay, bool isFollowUp)
    {
        lock (_changeLock)
        {
            if (_stopping || _loopCancellationTokenSource == null) return;

            if (isFollowUp)
            {
                if (_followUpAttempts >= FollowUpDelays.Length) return;
                delay = FollowUpDelays[_followUpAttempts++];
            }
            else
            {
                _followUpAttempts = 0;
            }

            var now = DateTime.UtcNow;
            var due = now + delay;
            var earliest = _lastChangeRequestUtc + MinimumRequestSpacing;
            if (due < earliest) due = earliest;
            if (_nextRequestDueUtc == null || due < _nextRequestDueUtc) _nextRequestDueUtc = due;

            _changeNotificationTask ??= Task.Run(FlushChangesAsync);
        }
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        int reconnectAttempt = 0;
        bool retriedAuthentication = false;

        while (!cancellationToken.IsCancellationRequested && !_stopping)
        {
            // Resumed by SynchronizerFactory once the attention is cleared.
            if (_account.IsNetworkAccessBlocked() || !_account.IsMailAccessGranted)
                return;

            IMailFolder folder = null;
            bool shouldReconnect = true;

            using var client = new WinoImapClient();

            try
            {
                var accessToken = await _connection.GetAccessTokenAsync(retriedAuthentication, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrEmpty(accessToken))
                {
                    _logger.Information("{Provider} IMAP access is not granted for {AccountName}. Updates use scheduled synchronization.", _connection.ProviderName, _account.Name);
                    return;
                }

                await client.ConnectAsync(_connection.Host, ImapPort, SecureSocketOptions.SslOnConnect, cancellationToken).ConfigureAwait(false);
                await client.AuthenticateAsync(new SaslMechanismOAuth2(_connection.GetUserName(), accessToken), cancellationToken).ConfigureAwait(false);
                retriedAuthentication = false;

                if (!client.Capabilities.HasFlag(ImapCapabilities.Idle))
                {
                    _logger.Information("{Provider} IMAP did not offer IDLE for {AccountName}. Updates use scheduled synchronization.", _connection.ProviderName, _account.Name);
                    return;
                }

                // Verification codes land in the Inbox, and Gmail signals Inbox changes sooner than
                // All Mail changes. Mail that skips the Inbox waits for the scheduled synchronization.
                folder = client.Inbox;
                await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);

                folder.CountChanged += FolderChanged;
                folder.MessageFlagsChanged += FolderChanged;
                folder.MessageExpunged += FolderChanged;
                folder.MessagesVanished += FolderChanged;

                // Reconcile changes that arrived while this listener was disconnected.
                if (reconnectAttempt > 0) RequestSynchronization();
                _logger.Debug("Started {Provider} IDLE watcher on {FolderName} for {AccountName}.", _connection.ProviderName, folder.FullName, _account.Name);

                while (!cancellationToken.IsCancellationRequested && !_stopping && client.IsConnected)
                {
                    using var idleDoneTokenSource = new CancellationTokenSource(IdleRenewInterval);
                    var started = DateTime.UtcNow;
                    lock (_idleDoneLock)
                    {
                        if (_stopping) break;
                        _idleDoneCancellationTokenSource = idleDoneTokenSource;
                    }
                    try
                    {
                        await client.IdleAsync(idleDoneTokenSource.Token, cancellationToken).ConfigureAwait(false);
                        if (DateTime.UtcNow - started >= TimeSpan.FromMinutes(1)) reconnectAttempt = 0;
                    }
                    finally
                    {
                        lock (_idleDoneLock) _idleDoneCancellationTokenSource = null;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is AuthenticationAttentionException or AccountAttentionRequiredException)
            {
                // The API synchronization owns sign-in problems and shows them to the user.
                return;
            }
            catch (Exception ex) when (ex is MailKit.Security.AuthenticationException or SaslException)
            {
                // An access token can expire between the cache check and the login. Retry once with a
                // fresh one; a second rejection means IMAP is off for this account (for example by an
                // organization admin), which does not affect the API synchronization.
                if (!retriedAuthentication)
                {
                    retriedAuthentication = true;
                    continue;
                }

                _logger.Information(ex, "{Provider} IMAP sign-in was rejected for {AccountName}. Updates use scheduled synchronization.", _connection.ProviderName, _account.Name);
                return;
            }
            catch (ImapCommandException ex)
            {
                _logger.Information(ex, "{Provider} refused the IDLE watcher for {AccountName}. Updates use scheduled synchronization.", _connection.ProviderName, _account.Name);
                return;
            }
            catch (Exception ex) when (ex is IOException or ImapProtocolException or System.Net.Sockets.SocketException or OperationCanceledException)
            {
                _logger.Debug(ex, "{Provider} IDLE watcher lost its connection for {AccountName}.", _connection.ProviderName, _account.Name);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "{Provider} IDLE watcher failed for {AccountName}.", _connection.ProviderName, _account.Name);
            }
            finally
            {
                if (folder != null)
                {
                    folder.CountChanged -= FolderChanged;
                    folder.MessageFlagsChanged -= FolderChanged;
                    folder.MessageExpunged -= FolderChanged;
                    folder.MessagesVanished -= FolderChanged;
                }

                if (cancellationToken.IsCancellationRequested || _stopping) shouldReconnect = false;
                await DisconnectQuietlyAsync(client).ConfigureAwait(false);
            }

            if (!shouldReconnect) break;

            reconnectAttempt++;
            var reconnectDelay = GetReconnectDelay(reconnectAttempt);
            _logger.Debug("Reconnecting {Provider} IDLE watcher for {AccountName} in {Delay}.", _connection.ProviderName, _account.Name, reconnectDelay);

            try
            {
                await Task.Delay(reconnectDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static async Task DisconnectQuietlyAsync(ImapClient client)
    {
        if (!client.IsConnected) return;

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.DisconnectAsync(true, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The socket is discarded either way.
        }
    }

    private void FolderChanged(object sender, EventArgs e)
    {
        _logger.Debug("{Provider} IDLE reported {ChangeKind} for {AccountName}.", _connection.ProviderName, e.GetType().Name, _account.Name);
        RequestSynchronization();
    }

    private async Task FlushChangesAsync()
    {
        try
        {
            while (true)
            {
                TimeSpan remaining;
                CancellationToken token;
                lock (_changeLock)
                {
                    if (_nextRequestDueUtc == null || _stopping || _loopCancellationTokenSource == null) return;
                    token = _loopCancellationTokenSource.Token;
                    remaining = _nextRequestDueUtc.Value - DateTime.UtcNow;

                    if (remaining <= TimeSpan.Zero)
                    {
                        _nextRequestDueUtc = null;
                        _lastChangeRequestUtc = DateTime.UtcNow;
                    }
                }

                if (remaining > TimeSpan.Zero)
                {
                    // Short waits notice when a newer request moved the due time earlier.
                    await Task.Delay(remaining < PendingCheckInterval ? remaining : PendingCheckInterval, token).ConfigureAwait(false);
                    continue;
                }

                _changeDetected();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.Warning(ex, "Failed to request {Provider} IDLE synchronization for {AccountName}.", _connection.ProviderName, _account.Name); }
        finally
        {
            lock (_changeLock)
            {
                _changeNotificationTask = null;
                if (_nextRequestDueUtc != null && !_stopping && _loopCancellationTokenSource != null)
                    _changeNotificationTask = Task.Run(FlushChangesAsync);
            }
        }
    }

    private static TimeSpan GetReconnectDelay(int attempt)
    {
        var backoffSeconds = Math.Min(300, Math.Pow(2, Math.Min(attempt, 9)));
        int jitterMs;

        lock (ReconnectJitter)
        {
            jitterMs = ReconnectJitter.Next(250, 1250);
        }

        return TimeSpan.FromSeconds(backoffSeconds) + TimeSpan.FromMilliseconds(jitterMs);
    }
}
