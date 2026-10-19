using Microsoft.Extensions.Logging;
using Sharpcaster.Extensions;
using Sharpcaster.Messages.Heartbeat;
using System;
using System.Text.Json;
using System.Timers;

namespace Sharpcaster.Channels
{
    /// <summary>
    /// Heartbeat channel. Responds to ping messages with pong message
    /// </summary>
    public class HeartbeatChannel : ChromecastChannel, IDisposable
    {
        //private ILogger _logger = null;
        private readonly Timer _timer;
        private bool disposedValue;
        private bool _triedToPing;

        private static readonly Action<ILogger, Exception?> LogPongSent =
            LoggerMessage.Define(LogLevel.Debug, new EventId(1001, nameof(OnMessageReceived)), "Pong sent - Heartbeat Timer restarted.");

        private static readonly Action<ILogger, Exception?> LogHeartbeatTimerStarted =
            LoggerMessage.Define(LogLevel.Trace, new EventId(1002, nameof(StartTimeoutTimer)), "Started heartbeat timeout timer");

        private static readonly Action<ILogger, Exception?> LogHeartbeatTimerStopped =
            LoggerMessage.Define(LogLevel.Trace, new EventId(1003, nameof(StopTimeoutTimer)), "Stopped heartbeat timeout timer");

        private static readonly Action<ILogger, Exception?> LogHeartbeatTimeout =
            LoggerMessage.Define(LogLevel.Information, new EventId(1004, nameof(TimerElapsed)), "Heartbeat timeout");

        private static readonly Action<ILogger, Exception?> LogHeartbeatTimerRestarted =
            LoggerMessage.Define(LogLevel.Trace, new EventId(1005, nameof(StartTimeoutTimer)), "Restarted heartbeat timeout timer");

        private static readonly Action<ILogger, Exception?> LogPongReceived =
            LoggerMessage.Define(LogLevel.Debug, new EventId(1006, nameof(OnMessageReceived)), "Pong received - Heartbeat Timer restarted.");

        private static readonly Action<ILogger, Exception?> LogPingSent =
            LoggerMessage.Define(LogLevel.Debug, new EventId(1008, nameof(OnMessageReceived)), "Ping sent - Heartbeat Timer restarted.");

        /// <summary>
        /// Initializes a new instance of HeartbeatChannel class
        /// </summary>
        public HeartbeatChannel(ILogger? logger = null) : base("tp.heartbeat", logger)
        {
            _timer = new Timer(10000); // timeout is 10 seconds.
                                       // Normally Chromecast application only waits for 8 seconds before it tries to ping us.
                                       // So we give it a bit more time.
            _timer.Elapsed += TimerElapsed;
            _timer.AutoReset = false;
        }

        public event EventHandler? StatusChanged;


        /// <summary>
        /// Called when a message for this channel is received
        /// </summary>
        /// <param name="messagePayload">message payload to process</param>
        /// <param name="type">message type</param>
        // RPM: try/catch, for the same reason as TimerElapsed below. This is `async void` writing to
        // the socket, and answering a PING on a connection that closed between the read and the write
        // would otherwise crash the process.
        public override async void OnMessageReceived(string messagePayload, string type)
        {
            try
            {
                if (type == "PONG")
                {
                    _triedToPing = false;
                    _timer.Stop();
                    _timer.Start();
                    if (Logger != null) LogPongReceived(Logger, null);
                    return;
                }
                _timer.Stop();
                var pongMessage = new PongMessage();
                await SendAsync(JsonSerializer.Serialize(pongMessage, SharpcasteSerializationContext.Default.PongMessage)).ConfigureAwait(false);
                _timer.Start();
                if (Logger != null) LogPongSent(Logger, null);
            }
            catch (Exception)
            {
                SafeInvokeEvent(StatusChanged, this, EventArgs.Empty);
            }
        }

        public void StartTimeoutTimer()
        {
            _timer.Start();
            if (Logger != null) LogHeartbeatTimerStarted(Logger, null);
        }

        public void StopTimeoutTimer()
        {
            _timer.Stop();
            if (Logger != null) LogHeartbeatTimerStopped(Logger, null);
        }

        public void RestartTimeoutTimer()
        {
            _triedToPing = false;
            _timer.Stop();
            _timer.Start();
            if (Logger != null) LogHeartbeatTimerRestarted(Logger, null);
        }

        // RPM: wrapped in a try/catch. The catch is required.
        //
        // This is an `async void` timer handler that writes to the socket. When a speaker is gone
        // (unplugged, rebooted, taken over by another sender), that write throws
        // "IOException: Broken pipe" AFTER the first await, so it is raised on a thread pool thread
        // rather than stored on a task. Nothing in the host application can catch it there: it is not
        // an unobserved task exception, so TaskScheduler.UnobservedTaskException never sees it, and the
        // process crashes. It must be caught here.
        //
        // A failed ping means the same thing a heartbeat timeout means, so it is reported the same
        // way: raise StatusChanged, which the client answers by disconnecting.
        private async void TimerElapsed(object sender, ElapsedEventArgs e)
        {
            try
            {
                if (_triedToPing)
                {
                    if (Logger != null) LogHeartbeatTimeout(Logger, null);
                    SafeInvokeEvent(StatusChanged, this, e);
                    return;
                }
                var pingMessage = new PingMessage();
                await SendAsync(JsonSerializer.Serialize(pingMessage, SharpcasteSerializationContext.Default.PingMessage)).ConfigureAwait(false);
                _triedToPing = true;
                if (Logger != null) LogPingSent(Logger, null);
            }
            catch (Exception)
            {
                // The socket is gone. Same result as a timeout, only sooner.
                SafeInvokeEvent(StatusChanged, this, e);
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    _timer?.Dispose();
                }
                disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
