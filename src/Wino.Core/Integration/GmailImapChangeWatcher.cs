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
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Integration;

/// <summary>
/// Holds one IMAP IDLE connection to Gmail as a change signal. It downloads nothing: every change it
/// hears about asks the synchronizer for a History API delta sync, which stays the only data path.
/// The scheduled synchronization remains the fallback when IMAP is unavailable for the account.
/// </summary>
internal sealed class GmailImapChangeWatcher
{
    private const string GmailImapHost = "imap.gmail.com";
    private const int GmailImapPort = 993;

    // Gmail ends IDLE commands after about 30 minutes. Renewing earlier also exposes dead sockets.
    private static readonly TimeSpan IdleRenewInterval = TimeSpan.FromMinutes(9);

    // Gmail reports one arrival as several untagged responses. The first one waits briefly so
    // the History API already lists the change; later ones within the window share one sync.
    private static readonly TimeSpan FirstChangeDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ChangeThrottleWindow = TimeSpan.FromSeconds(15);

    private static readonly Random ReconnectJitter = new();

    private readonly MailAccount _account;
    private readonly IGmailAuthenticator _authenticator;
    private readonly Action _changeDetected;
    private readonly ILogger _logger = Log.ForContext<GmailImapChangeWatcher>();

    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _idleDoneLock = new();
    private readonly object _changeLock = new();

    private CancellationTokenSource _loopCancellationTokenSource;
    private CancellationTokenSource _idleDoneCancellationTokenSource;
    private Task _loopTask;
    private Task _changeNotificationTask;
    private volatile bool _stopping;
    private bool _changePending;
    private DateTime _lastChangeRequestUtc = DateTime.MinValue;

    public GmailImapChangeWatcher(MailAccount account, IGmailAuthenticator authenticator, Action changeDetected)
    {
        _account = account ?? throw new ArgumentNullException(nameof(account));
        _authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
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
                lock (_changeLock) _changePending = false;
            }
        }
        finally { _lifecycle.Release(); }
    }

    /// <summary>
    /// Asks for a synchronization within the throttle window. The synchronizer also calls this when
    /// it deferred a requested sync because another one was still pending.
    /// </summary>
    public void RequestSynchronization()
    {
        lock (_changeLock)
        {
            if (_stopping || _loopCancellationTokenSource == null) return;
            _changePending = true;
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
                await client.ConnectAsync(GmailImapHost, GmailImapPort, SecureSocketOptions.SslOnConnect, cancellationToken).ConfigureAwait(false);

                var token = retriedAuthentication
                    ? await _authenticator.RefreshTokenInformationAsync(_account, cancellationToken: cancellationToken).ConfigureAwait(false)
                    : await _authenticator.GetTokenInformationAsync(_account, cancellationToken: cancellationToken).ConfigureAwait(false);

                await client.AuthenticateAsync(new SaslMechanismOAuth2(_account.Address, token.AccessToken), cancellationToken).ConfigureAwait(false);
                retriedAuthentication = false;

                if (!client.Capabilities.HasFlag(ImapCapabilities.Idle))
                {
                    _logger.Information("Gmail IMAP did not offer IDLE for {AccountName}. Updates use scheduled synchronization.", _account.Name);
                    return;
                }

                // All Mail sees new mail that filters skip the inbox with and mail sent from other devices.
                folder = GetWatchedFolder(client);
                await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);

                folder.CountChanged += FolderChanged;
                folder.MessageFlagsChanged += FolderChanged;
                folder.MessageExpunged += FolderChanged;
                folder.MessagesVanished += FolderChanged;

                // Reconcile changes that arrived while this listener was disconnected.
                if (reconnectAttempt > 0) RequestSynchronization();
                _logger.Debug("Started Gmail IDLE watcher on {FolderName} for {AccountName}.", folder.FullName, _account.Name);

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
                // fresh one; a second rejection means IMAP is off for this account (for example by a
                // Workspace admin), which does not affect the API synchronization.
                if (!retriedAuthentication)
                {
                    retriedAuthentication = true;
                    continue;
                }

                _logger.Information(ex, "Gmail IMAP sign-in was rejected for {AccountName}. Updates use scheduled synchronization.", _account.Name);
                return;
            }
            catch (ImapCommandException ex)
            {
                _logger.Information(ex, "Gmail refused the IDLE watcher for {AccountName}. Updates use scheduled synchronization.", _account.Name);
                return;
            }
            catch (Exception ex) when (ex is IOException or ImapProtocolException or System.Net.Sockets.SocketException or OperationCanceledException)
            {
                _logger.Debug(ex, "Gmail IDLE watcher lost its connection for {AccountName}.", _account.Name);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Gmail IDLE watcher failed for {AccountName}.", _account.Name);
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
            _logger.Debug("Reconnecting Gmail IDLE watcher for {AccountName} in {Delay}.", _account.Name, reconnectDelay);

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

    private static IMailFolder GetWatchedFolder(ImapClient client)
    {
        // All Mail is missing when the user hid it from IMAP in Gmail settings.
        if ((client.Capabilities & (ImapCapabilities.SpecialUse | ImapCapabilities.XList)) != 0)
        {
            try
            {
                var allMail = client.GetFolder(SpecialFolder.All);
                if (allMail != null) return allMail;
            }
            catch (NotSupportedException)
            {
            }
        }

        return client.Inbox;
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

    private void FolderChanged(object sender, EventArgs e) => RequestSynchronization();

    private async Task FlushChangesAsync()
    {
        try
        {
            while (true)
            {
                TimeSpan delay;
                CancellationToken token;
                lock (_changeLock)
                {
                    if (!_changePending || _stopping || _loopCancellationTokenSource == null) return;
                    token = _loopCancellationTokenSource.Token;
                    var throttleDelay = ChangeThrottleWindow - (DateTime.UtcNow - _lastChangeRequestUtc);
                    delay = throttleDelay > FirstChangeDelay ? throttleDelay : FirstChangeDelay;
                }

                await Task.Delay(delay, token).ConfigureAwait(false);

                lock (_changeLock)
                {
                    if (_stopping) return;
                    _changePending = false;
                    _lastChangeRequestUtc = DateTime.UtcNow;
                }

                _changeDetected();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.Warning(ex, "Failed to request Gmail IDLE synchronization for {AccountName}.", _account.Name); }
        finally
        {
            lock (_changeLock)
            {
                _changeNotificationTask = null;
                if (_changePending && !_stopping && _loopCancellationTokenSource != null)
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
