using Application.Common;
using Application.StreamPipeline.Abstraction;
using Application.StreamPipeline.Common;
using Application.StreamPipeline.Models;
using Domain.StreamPipeline.Enums;
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
    private readonly ILogger<CommandPipe<TCommand, TResponse>> _logger = logger;
    private readonly MessagingPipe<CommandPipePayload<TCommand, TResponse>, CommandPipePayload<TCommand, TResponse>> _messagingPipe = messagingPipe;

    private readonly Dictionary<Guid, Action<TResponse>> _commandActionMap = [];
    private readonly ReaderWriterLockSlim _rwl = new();

    private Func<TCommand, Task<TResponse>>? _onCommandCallback = null;

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

    public void OnCommand(Func<TCommand, Task<TResponse>> onCommandCallback)
    {
        _onCommandCallback = onCommandCallback;
    }

    public void OnCommand(Func<TCommand, TResponse> onCommandCallback)
    {
        _onCommandCallback = commandPayload =>
        {
            var response = onCommandCallback(commandPayload);
            return Task.FromResult(response);
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

                commandGuid = _messagingPipe.Send(new CommandPipePayload()
                {
                    PayloadType = CommandPipePayloadType.Command,
                    RawPayload = JsonSerializer.Serialize(command, _messagingPipe.JsonSerializerOptions)
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

            if (!await commandGate.WaitForOpen(cancellationToken) &&
                commandGuid != Guid.Empty)
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
            switch (messagingPipePayload.Message.PayloadType)
            {
                case CommandPipePayloadType.Command:

                    if (JsonSerializer.Deserialize<TCommand>(messagingPipePayload.Message.RawPayload, _messagingPipe.JsonSerializerOptions) is not TCommand command)
                    {
                        throw new InvalidCommandPipePayloadException(nameof(TCommand));
                    }

                    TResponse? commandResponse = default;
                    var commandResponseTask = _onCommandCallback?.Invoke(command);
                    if (commandResponseTask != null)
                    {
                        commandResponse = await commandResponseTask;
                    }

                    _messagingPipe.Send(messagingPipePayload.MessageGuid, new CommandPipePayload()
                    {
                        PayloadType = CommandPipePayloadType.Response,
                        RawPayload = JsonSerializer.Serialize(commandResponse, _messagingPipe.JsonSerializerOptions)
                    });

                    break;
                case CommandPipePayloadType.Response:

                    if (JsonSerializer.Deserialize<TResponse>(messagingPipePayload.Message.RawPayload, _messagingPipe.JsonSerializerOptions) is not TResponse receivedResponse)
                    {
                        throw new InvalidCommandPipePayloadException(nameof(TResponse));
                    }

                    try
                    {
                        _rwl.EnterWriteLock();

                        if (!_commandActionMap.TryGetValue(messagingPipePayload.MessageGuid, out var messageCallback))
                        {
                            throw new Exception($"Command guid {messagingPipePayload.MessageGuid} does not exists");
                        }

                        messageCallback.Invoke(receivedResponse);
                    }
                    finally
                    {
                        _rwl.ExitWriteLock();
                    }

                    break;
                default:

                    throw new NotImplementedException($"{messagingPipePayload.Message.PayloadType}");
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
