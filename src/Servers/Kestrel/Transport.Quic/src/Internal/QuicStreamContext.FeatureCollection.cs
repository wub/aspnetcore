// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Quic.Internal;

internal sealed partial class QuicStreamContext :
    IPersistentStateFeature,
    IStreamDirectionFeature,
    IProtocolErrorCodeFeature,
    IStreamIdFeature,
    IStreamAbortFeature,
    IConnectionCompleteFeature
{
    private IDictionary<object, object?>? _persistentState;
    private long? _error;
    private Stack<KeyValuePair<Func<object, Task>, object>>? _onCompleted;

    public bool CanRead { get; private set; }
    public bool CanWrite { get; private set; }

    public long Error
    {
        get => _error ?? -1;
        set => _error = value;
    }

    public long StreamId { get; private set; }

    IDictionary<object, object?> IPersistentStateFeature.State
    {
        get
        {
            // Lazily allocate persistent state
            return _persistentState ?? (_persistentState = new ConnectionItems());
        }
    }

    public void AbortRead(long errorCode, ConnectionAbortedException abortReason)
    {
        lock (_shutdownLock)
        {
            if (_stream != null)
            {
                if (_stream.CanRead)
                {
                    _shutdownReadReason = abortReason;
                    QuicLog.StreamAbortRead(_log, this, errorCode, abortReason.Message);
                    _stream.AbortRead(errorCode);
                }
                else
                {
                    throw new InvalidOperationException("Unable to abort reading from a stream that doesn't support reading.");
                }
            }
        }
    }

    public void AbortWrite(long errorCode, ConnectionAbortedException abortReason)
    {
        lock (_shutdownLock)
        {
            if (_stream != null)
            {
                if (_stream.CanWrite)
                {
                    _shutdownWriteReason = abortReason;
                    QuicLog.StreamAbortWrite(_log, this, errorCode, abortReason.Message);
                    _stream.AbortWrite(errorCode);
                }
                else
                {
                    throw new InvalidOperationException("Unable to abort writing to a stream that doesn't support writing.");
                }
            }
        }
    }

    public void OnCompleted(Func<object, Task> callback, object state)
    {
        if (_streamClosed)
        {
            throw new InvalidOperationException("The stream is already complete.");
        }

        if (_onCompleted == null)
        {
            _onCompleted = new Stack<KeyValuePair<Func<object, Task>, object>>();
        }
        _onCompleted.Push(new KeyValuePair<Func<object, Task>, object>(callback, state));
    }

    private Task CompleteAsyncMayAwait(Stack<KeyValuePair<Func<object, Task>, object>> onCompleted)
    {
        while (onCompleted.TryPop(out var entry))
        {
            try
            {
                var task = entry.Key.Invoke(entry.Value);
                if (!task.IsCompletedSuccessfully)
                {
                    return CompleteAsyncAwaited(task, onCompleted);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "An error occurred running an IConnectionCompleteFeature.OnCompleted callback.");
            }
        }

        return Task.CompletedTask;
    }

    private async Task CompleteAsyncAwaited(Task currentTask, Stack<KeyValuePair<Func<object, Task>, object>> onCompleted)
    {
        try
        {
            await currentTask;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "An error occurred running an IConnectionCompleteFeature.OnCompleted callback.");
        }

        while (onCompleted.TryPop(out var entry))
        {
            try
            {
                await entry.Key.Invoke(entry.Value);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "An error occurred running an IConnectionCompleteFeature.OnCompleted callback.");
            }
        }
    }

    private void InitializeFeatures()
    {
        _currentIPersistentStateFeature = this;
        _currentIStreamDirectionFeature = this;
        _currentIProtocolErrorCodeFeature = this;
        _currentIStreamIdFeature = this;
        _currentIStreamAbortFeature = this;
        _currentIConnectionCompleteFeature = this;
    }
}
