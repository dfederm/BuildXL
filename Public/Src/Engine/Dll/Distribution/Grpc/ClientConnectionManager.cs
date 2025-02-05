// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Distribution.Grpc;
using BuildXL.Engine.Tracing;
using BuildXL.Utilities;
using BuildXL.Utilities.Configuration;
using BuildXL.Utilities.Instrumentation.Common;
using BuildXL.Utilities.Tasks;
using Grpc.Core;

namespace BuildXL.Engine.Distribution.Grpc
{
    /// <nodoc/>
    internal sealed class ClientConnectionManager
    {
        public class ConnectionTimeoutEventArgs : EventArgs
        {
            public string Details { get; init; }

            public ConnectionTimeoutEventArgs(string details) => Details = details;
        }

        /// <summary>
        /// Default channel options for clients/servers to send/receive unlimited messages.
        /// </summary>
        private static readonly ChannelOption[] s_defaultChannelOptions = new ChannelOption[] { new ChannelOption(ChannelOptions.MaxSendMessageLength, -1), new ChannelOption(ChannelOptions.MaxReceiveMessageLength, -1) };

        public static readonly IEnumerable<ChannelOption> ClientChannelOptions = GetClientChannelOptions();
        public static readonly IEnumerable<ChannelOption> ServerChannelOptions = GetServerChannelOptions();

        internal readonly Channel Channel;
        private readonly LoggingContext m_loggingContext;
        private readonly string m_buildId;
        private readonly Task m_monitorConnectionTask;
        public event EventHandler<ConnectionTimeoutEventArgs> OnConnectionTimeOutAsync;
        private volatile bool m_isShutdownInitiated;
        private volatile bool m_isExitCalledForServer;

        private string GenerateLog(string traceId, string status, uint numTry, string description)
        {
            // example: [SELF -> MW1AAP45DD9145A::89] e709c667-ef88-464c-8557-232b02463976 Call#1. Description 
            // example: [SELF -> MW1AAP45DD9145A::89] e709c667-ef88-464c-8557-232b02463976 Sent#1. Duration: Milliseconds 
            // example: [SELF -> MW1AAP45DD9145A::89] e709c667-ef88-464c-8557-232b02463976 Fail#1. Duration: Milliseconds. Failure: 
            return string.Format("[SELF -> {0}] {1} {2}#{3}. {4}", Channel.Target, traceId, status, numTry, description);
        }

        private string GenerateFailLog(string traceId, uint numTry, long duration, string failureMessage)
        {
            return GenerateLog(traceId.ToString(), "Fail", numTry, $"Duration: {duration}ms. Failure: {failureMessage}. ChannelState: {Channel.State}");
        }

        public ClientConnectionManager(LoggingContext loggingContext, string ipAddress, int port, string buildId)
        {
            m_buildId = buildId;
            m_loggingContext = loggingContext;
            Channel = new Channel(
                    ipAddress,
                    port,
                    ChannelCredentials.Insecure,
                    ClientChannelOptions);
            m_monitorConnectionTask = MonitorConnectionAsync();
        }

        public static IEnumerable<ChannelOption> GetClientChannelOptions()
        {
            List<ChannelOption> channelOptions = new List<ChannelOption>();
            channelOptions.AddRange(s_defaultChannelOptions);
            if (EngineEnvironmentSettings.GrpcKeepAliveEnabled)
            {
                channelOptions.Add(new ChannelOption(ExtendedChannelOptions.KeepAlivePermitWithoutCalls, 1)); // enable sending pings
                channelOptions.Add(new ChannelOption(ExtendedChannelOptions.KeepAliveTimeMs, 300000)); // 5m-frequent pings
                channelOptions.Add(new ChannelOption(ExtendedChannelOptions.KeepAliveTimeoutMs, 60000)); // wait for 1m to receive ack for the ping before closing connection.
                channelOptions.Add(new ChannelOption(ExtendedChannelOptions.MaxPingsWithoutData, 0)); // no limit for pings with no header/data
                channelOptions.Add(new ChannelOption(ExtendedChannelOptions.MinSentPingIntervalWithoutDataMs, 300000)); // 5m-frequent pings with no header/data
            }

            return channelOptions;
        }

        public static IEnumerable<ChannelOption> GetServerChannelOptions()
        {
            List<ChannelOption> channelOptions = new List<ChannelOption>();
            channelOptions.AddRange(s_defaultChannelOptions);
            if (EngineEnvironmentSettings.GrpcKeepAliveEnabled)
            {
                // Pings are sent from client to server, and we do not want server to send pings to client due to the overhead concerns.
                // We just need to make server accept the pings.
                channelOptions.Add(new ChannelOption(ExtendedChannelOptions.KeepAlivePermitWithoutCalls, 1)); // enable receiving pings with no data
                channelOptions.Add(new ChannelOption(ExtendedChannelOptions.MinRecvPingIntervalWithoutDataMs, 300000)); // expecting 5m-frequent pings with no header/data
            }

            return channelOptions;
        }

        public async Task MonitorConnectionAsync()
        {
            await Task.Yield();

            ChannelState state = ChannelState.Idle;
            var transientFailureTimer = new Stopwatch();
            while (state != ChannelState.Shutdown)
            {
                try
                {
                    await Channel.TryWaitForStateChangedAsync(state);
                }
                catch (ObjectDisposedException)
                {
                    // The channel has been already shutdown and handle was disposed
                    // (https://github.com/grpc/grpc/blob/master/src/csharp/Grpc.Core/Channel.cs#L160)
                    // We shouldn't fail or leave this unobserved, instead we just stop monitoring
                    Logger.Log.GrpcTrace(m_loggingContext, $"[{Channel.Target}] Channel state: {state} -> Disposed. Assuming shutdown was requested");
                    break;
                }

                Logger.Log.GrpcTrace(m_loggingContext, $"[{Channel.Target}] Channel state: {state} -> {Channel.State}");

                state = Channel.State;

                // Check if we're stuck in transient failure
                // In this situation, the state will alternate between "Connecting" and "TransientFailure"
                if (state == ChannelState.TransientFailure)
                {
                    if (!transientFailureTimer.IsRunning)
                    {
                        transientFailureTimer.Start();
                    }

                    if (transientFailureTimer.Elapsed >= EngineEnvironmentSettings.DistributionConnectTimeout)
                    {
                        OnConnectionTimeOutAsync?.Invoke(this, new ConnectionTimeoutEventArgs($"Timed out trying to recover from the TRANSIENT_FAILURE channel state. Timeout: {EngineEnvironmentSettings.DistributionConnectTimeout.Value.TotalMinutes} minutes"));
                        break;
                    }
                }
                else if (state != ChannelState.Connecting)
                {
                    // We assume we are out of the "transient failure" situation
                    // if the state is no longer TransientFailure or Connecting
                    transientFailureTimer.Reset();
                }

                // If we requested 'exit' for the server, the channel can go to 'Idle' state.
                // We should not reconnect the channel again in that case.
                if (state == ChannelState.Idle && !m_isExitCalledForServer)
                {
                    bool isReconnected = await TryReconnectAsync();
                    if (!isReconnected)
                    {
                        OnConnectionTimeOutAsync?.Invoke(this, new ConnectionTimeoutEventArgs("Reconnection attempts failed"));
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Ready for exit.
        /// </summary>
        public void ReadyForExit()
        {
            // If this is an exit operation, it will make the server to exit on the other machine.
            // We need to be aware of this case as we do not want to reconnect to server. 
            m_isExitCalledForServer = true;
        }

        private async Task<bool> TryReconnectAsync()
        {
            int numRetries = 0;
            bool connectionSucceeded = false;

            while (numRetries < GrpcSettings.MaxRetry)
            {
                numRetries++;

                // Try connecting with timeout
                connectionSucceeded = await TryConnectChannelAsync(GrpcSettings.CallTimeout, nameof(TryReconnectAsync));
                if (connectionSucceeded)
                {
                    return true;
                }
                else if (IsNonRecoverableState(Channel.State))
                {
                    // If the end state is a non-recovarable state, there is no hope for the reconnection.
                    return false;
                }
            }

            // If the connection is not established after retries, return false.
            return false;
        }

        public async Task CloseAsync()
        {
            if (!m_isShutdownInitiated)
            {
                m_isShutdownInitiated = true;
                await Channel.ShutdownAsync();
            }

            await m_monitorConnectionTask;
        }

        public async Task<RpcCallResult<Unit>> CallAsync(
            Func<CallOptions, Task<RpcResponse>> func, 
            string operation,
            CancellationToken cancellationToken = default(CancellationToken),
            bool waitForConnection = false)
        {
            var watch = Stopwatch.StartNew();

            TimeSpan waitForConnectionDuration = TimeSpan.Zero;
            TimeSpan totalCallDuration = TimeSpan.Zero;

            if (waitForConnection)
            {
                bool connectionSucceeded = await TryConnectChannelAsync(GrpcSettings.WorkerAttachTimeout, operation, watch);
                waitForConnectionDuration = watch.Elapsed;

                if (!connectionSucceeded)
                {
                    return new RpcCallResult<Unit>(RpcCallResultState.Cancelled, attempts: 1, duration: TimeSpan.Zero, waitForConnectionDuration);
                }
            }

            Guid traceId = Guid.NewGuid();
            var headers = new Metadata();
            headers.Add(GrpcSettings.TraceIdKey, traceId.ToByteArray());
            headers.Add(GrpcSettings.BuildIdKey, m_buildId);
            headers.Add(GrpcSettings.SenderKey, DistributionHelpers.MachineName);

            RpcCallResultState state = RpcCallResultState.Succeeded;
            Failure failure = null;

            uint numTry = 0;
            while (numTry < GrpcSettings.MaxRetry)
            {
                numTry++;
                watch.Restart();

                try
                {
                    var callOptions = new CallOptions(
                        deadline: DateTime.UtcNow.Add(GrpcSettings.CallTimeout),
                        cancellationToken: cancellationToken,
                        headers: headers).WithWaitForReady();

                    Logger.Log.GrpcTrace(m_loggingContext, GenerateLog(traceId.ToString(), "Call", numTry, operation));
                    await func(callOptions);
                    Logger.Log.GrpcTrace(m_loggingContext, GenerateLog(traceId.ToString(), "Sent", numTry, $"Duration: {watch.ElapsedMilliseconds}ms"));

                    state = RpcCallResultState.Succeeded;
                    break;
                }
                catch (RpcException e)
                {
                    state = e.Status.StatusCode == StatusCode.Cancelled ? RpcCallResultState.Cancelled : RpcCallResultState.Failed;
                    failure = state == RpcCallResultState.Failed ? new RecoverableExceptionFailure(new BuildXLException(e.Message)) : null;
                    Logger.Log.GrpcTrace(m_loggingContext, GenerateFailLog(traceId.ToString(), numTry, watch.ElapsedMilliseconds, e.Message));

                    // If the call is cancelled or channel is shutdown, then do not retry the call.
                    if (state == RpcCallResultState.Cancelled || m_isShutdownInitiated)
                    {
                        break;
                    }
                }
                catch (ObjectDisposedException e)
                {
                    state = RpcCallResultState.Failed;
                    failure = new RecoverableExceptionFailure(new BuildXLException(e.Message));
                    Logger.Log.GrpcTrace(m_loggingContext, GenerateFailLog(traceId.ToString(), numTry, watch.ElapsedMilliseconds, e.Message));

                    // If stream is already disposed, we cannot retry call. 
                    break;
                }
                finally
                {
                    totalCallDuration += watch.Elapsed;
                }
            }

            if (state == RpcCallResultState.Succeeded)
            {
                return new RpcCallResult<Unit>(Unit.Void, attempts: numTry, duration: totalCallDuration, waitForConnectionDuration: waitForConnectionDuration);
            }

            return new RpcCallResult<Unit>(
                state,
                attempts: numTry,
                duration: totalCallDuration,
                waitForConnectionDuration: waitForConnectionDuration,
                lastFailure: failure);
        }


        private async Task<bool> TryConnectChannelAsync(TimeSpan timeout, string operation, Stopwatch watch = null)
        {
            watch = watch ?? Stopwatch.StartNew();

            try
            {
                Logger.Log.GrpcTrace(m_loggingContext, $"Attempt to connect to {Channel.Target}. ChannelState {Channel.State}. Operation {operation}");
                await Channel.ConnectAsync(DateTime.UtcNow.Add(timeout));
                Logger.Log.GrpcTrace(m_loggingContext, $"Connected to {Channel.Target}. ChannelState {Channel.State}. Duration {watch.ElapsedMilliseconds}ms");
            }
            catch (Exception e)
            {
#pragma warning disable EPC12 // Suspicious exception handling: only Message property is observed in exception block.
                Logger.Log.GrpcTrace(m_loggingContext, $"Failed to connect to {Channel.Target}. Duration {watch.ElapsedMilliseconds}ms. ChannelState {Channel.State}. Failure {e.Message}");
#pragma warning restore EPC12 // Suspicious exception handling: only Message property is observed in exception block.

                return false;
            }

            return true;
        }

        private static bool IsNonRecoverableState(ChannelState state)
        {
            switch (state)
            {
                case ChannelState.Idle:
                case ChannelState.Shutdown:
                    return true;
                default:
                    return false;
            }
        }
    }
}