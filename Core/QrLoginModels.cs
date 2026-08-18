using System;

namespace SeewoAutoLogin
{
    public enum QrLoginState
    {
        Idle,
        CreatingQrCode,
        WaitingForScan,
        WaitingForConfirmation,
        Completing,
        Succeeded,
        Expired,
        Cancelled,
        Denied,
        NetworkError,
        ProtocolError
    }

    public sealed class QrLoginSession
    {
        public byte[] ImageBytes { get; init; } = Array.Empty<byte>();
        public string SessionKey { get; init; } = "";
        public DateTimeOffset ExpiresAt { get; init; }
    }

    public sealed class QrLoginStatus
    {
        public QrLoginState State { get; init; }
        public string TemporaryToken { get; init; } = "";
        public string ErrorMessage { get; init; } = "";
    }

    public sealed class QrLoginOutcome
    {
        public string Token { get; init; } = "";
        public SeewoUserInfo UserInfo { get; init; }
    }

    public sealed class QrLoginStateChangedEventArgs : EventArgs
    {
        public QrLoginStateChangedEventArgs(QrLoginState state, QrLoginSession session = null, string message = "")
        {
            State = state;
            Session = session;
            Message = message;
        }

        public QrLoginState State { get; }
        public QrLoginSession Session { get; }
        public string Message { get; }
    }
}
