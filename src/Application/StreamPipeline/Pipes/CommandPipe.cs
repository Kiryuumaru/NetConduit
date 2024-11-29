using Application.Common;
using Application.StreamPipeline.Abstraction;
using Application.StreamPipeline.Common;
using Application.StreamPipeline.Models;
using CliWrap;
using Domain.StreamPipeline.Exceptions;
using Domain.StreamPipeline.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using TransactionHelpers;

namespace Application.StreamPipeline.Pipes;

public class CommandPipe<TCommand, TResponse>(ILogger<CommandPipe<TCommand, TResponse>> logger, MessagingPipe<CommandPipePayload<TCommand, TResponse>, CommandPipePayload<TCommand, TResponse>> messagingPipe) : BasePipe
{
    public class CommandCallback
    {
        private readonly Action<TResponse> _responseCallback;

        public TCommand Command { get; }

        internal CommandCallback(TCommand command, Action<TResponse> responseCallback)
        {
            _responseCallback = responseCallback;
            Command = command;
        }

        public void Respond(TResponse response)
        {
            _responseCallback(response);
        }
    }

    private readonly ILogger<CommandPipe<TCommand, TResponse>> _logger = logger;
    private readonly MessagingPipe<CommandPipePayload<TCommand, TResponse>, CommandPipePayload<TCommand, TResponse>> _messagingPipe = messagingPipe;

    private readonly Dictionary<Guid, Action<TResponse>> _commandActionMap = [];
    private readonly ReaderWriterLockSlim _rwl = new();

    private Func<CommandCallback, Task>? _onCommandCallback = null;

    protected override Task Execute(TranceiverStream tranceiverStream, CancellationToken stoppingToken)
    {
        _messagingPipe.OnMessage(OnMessageCallback);
        return _messagingPipe.Start(tranceiverStream, stoppingToken);
    }

    public void SetPipeName(string name)
    {
        _messagingPipe.SetPipeName(name);
    }

    public void SetJsonSerializerOptions(JsonSerializerOptions? jsonSerializerOptions)
    {
        _messagingPipe.SetJsonSerializerOptions(jsonSerializerOptions);
    }

    public void OnCommand(Func<CommandCallback, Task> onCommandCallback)
    {
        _onCommandCallback = onCommandCallback;
    }

    public void OnCommand(Action<CommandCallback> onCommandCallback)
    {
        _onCommandCallback = commandPayload =>
        {
            onCommandCallback(commandPayload);
            return Task.CompletedTask;
        };
    }

    public Task<Result<TResponse>> Send(TCommand command, CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            using var _ = _logger.BeginScopeMap(nameof(CommandPipe<TCommand, TResponse>), nameof(Send));

            Result<TResponse> result = new();

            Guid commandGuid = Guid.Empty;

            var commandGate = new GateKeeper();

            try
            {
                _rwl.EnterWriteLock();

                commandGuid = _messagingPipe.Send(new()
                {
                    Command = command,
                    Response = default,
                });

                _commandActionMap[commandGuid] = response =>
                {
                    result.WithValue(response);
                    commandGate.SetOpen();
                };
            }
            catch (Exception ex)
            {
                if (IsDisposedOrDisposing)
                {
                    return result;
                }
                _logger.LogError("CommandPipe {CommandPipeName} Send Error: {Error}", _messagingPipe.Name, ex.Message);
            }
            finally
            {
                _rwl.ExitWriteLock();
            }

            if (!await commandGate.WaitForOpen(cancellationToken) && commandGuid != Guid.Empty)
            {
                try
                {
                    _rwl.EnterWriteLock();

                    _commandActionMap.Remove(commandGuid);
                }
                catch { }
                finally
                {
                    _rwl.ExitWriteLock();
                }
            }

            return result;
        });
    }

    private async void OnMessageCallback(MessagingPipePayload<CommandPipePayload<TCommand, TResponse>> messagingPipePayload)
    {
        try
        {
            if (messagingPipePayload.Message.Response != null)
            {
                try
                {
                    _rwl.EnterReadLock();

                    if (!_commandActionMap.TryGetValue(messagingPipePayload.MessageGuid, out var messageCallback))
                    {
                        throw new Exception($"Command guid {messagingPipePayload.MessageGuid} does not exists");
                    }

                    messageCallback.Invoke(messagingPipePayload.Message.Response);
                }
                finally
                {
                    _rwl.ExitReadLock();
                }
            }
            else if (messagingPipePayload.Message.Command != null)
            {
                var commandCallback = new CommandCallback(messagingPipePayload.Message.Command,
                    callbackResponse => _messagingPipe.Send(messagingPipePayload.MessageGuid, new()
                    {
                        Command = messagingPipePayload.Message.Command,
                        Response = callbackResponse
                    }));
                var commandResponseTask = _onCommandCallback?.Invoke(commandCallback);
                if (commandResponseTask != null)
                {
                    await commandResponseTask;
                }
            }
        }
        catch (Exception ex)
        {
            if (IsDisposedOrDisposing)
            {
                return;
            }
            _logger.LogError("CommandPipe {CommandPipeName} OnMessageCallback Error: {Error}", _messagingPipe.Name, ex.Message);
        }
    }
}
