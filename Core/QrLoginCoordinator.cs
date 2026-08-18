using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SeewoAutoLogin
{
    public sealed class QrLoginCoordinator : IDisposable
    {
        private readonly SeewoQrLoginClient _client;
        private readonly SemaphoreSlim _sessionLock = new SemaphoreSlim(1, 1);
        private CancellationTokenSource _sessionCancellation;
        private long _sessionVersion;

        public QrLoginCoordinator(SeewoQrLoginClient client)
        {
            _client = client;
        }

        public QrLoginState State { get; private set; } = QrLoginState.Idle;
        public event EventHandler<QrLoginStateChangedEventArgs> StateChanged;
        public event Action<string> LogMessage;

        public async Task<QrLoginOutcome> StartAsync(CancellationToken cancellationToken)
        {
            await _sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Cancel();
                var version = Interlocked.Increment(ref _sessionVersion);
                Log($"启动扫码会话 #{version}");
                _sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                return await RunSessionAsync(version, _sessionCancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                _sessionLock.Release();
            }
        }

        public void Cancel()
        {
            var cancellation = Interlocked.Exchange(ref _sessionCancellation, null);
            if (cancellation == null) return;
            Log("取消当前扫码会话");
            cancellation.Cancel();
            cancellation.Dispose();
        }

        private async Task<QrLoginOutcome> RunSessionAsync(long version, CancellationToken cancellationToken)
        {
            try
            {
                Publish(version, QrLoginState.CreatingQrCode);
                var session = await _client.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
                Publish(version, QrLoginState.WaitingForScan, session);

                var scanned = false;
                while (DateTimeOffset.UtcNow < session.ExpiresAt)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var status = await _client.PollAsync(session, scanned, cancellationToken).ConfigureAwait(false);
                    if (status.State == QrLoginState.WaitingForScan)
                    {
                        Publish(version, status.State, session);
                    }
                    else if (status.State == QrLoginState.WaitingForConfirmation)
                    {
                        scanned = true;
                        Publish(version, status.State, session);
                    }
                    else if (status.State == QrLoginState.Completing)
                    {
                        Publish(version, status.State, session);
                        var outcome = await _client.CompleteAsync(status.TemporaryToken, cancellationToken).ConfigureAwait(false);
                        Publish(version, QrLoginState.Succeeded, session);
                        return outcome;
                    }
                    else
                    {
                        Publish(version, status.State, session, status.ErrorMessage);
                        return null;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                }

                Publish(version, QrLoginState.Expired, session);
                return null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Log($"扫码会话 #{version} 已取消");
                Publish(version, QrLoginState.Cancelled);
                return null;
            }
            catch (HttpRequestException ex)
            {
                Log($"扫码会话 #{version} 网络错误: {ex.GetType().Name}; status={(ex.StatusCode.HasValue ? ((int)ex.StatusCode.Value).ToString() : "<none>")}");
                Publish(version, QrLoginState.NetworkError);
                return null;
            }
            catch (IOException ex)
            {
                Log($"扫码会话 #{version} 协议错误: {ex.GetType().Name}; {SafeMessage(ex.Message)}");
                Publish(version, QrLoginState.ProtocolError, message: ex.Message);
                return null;
            }
            catch (Exception ex)
            {
                Log($"扫码会话 #{version} 未处理错误: {ex.GetType().Name}; {SafeMessage(ex.Message)}");
                Publish(version, QrLoginState.ProtocolError);
                return null;
            }
        }

        private void Publish(long version, QrLoginState state, QrLoginSession session = null, string message = "")
        {
            if (version != Volatile.Read(ref _sessionVersion)) return;
            var previous = State;
            State = state;
            if (previous != state)
                Log($"扫码会话 #{version} 状态: {previous} -> {state}; image-present={session?.ImageBytes?.Length > 0}; diagnostic-present={!string.IsNullOrWhiteSpace(message)}");
            StateChanged?.Invoke(this, new QrLoginStateChangedEventArgs(state, session, message));
        }

        private static string SafeMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return "<empty>";
            var normalized = message.Replace('\r', ' ').Replace('\n', ' ');
            return normalized.Length <= 240 ? normalized : normalized.Substring(0, 240);
        }

        private void Log(string message) => LogMessage?.Invoke("[QR Coordinator] " + message);

        public void Dispose()
        {
            Cancel();
            _sessionLock.Dispose();
        }
    }
}
