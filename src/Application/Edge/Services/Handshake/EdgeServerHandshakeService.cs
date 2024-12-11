using Application.Common.Extensions;
using Application.Common.Features;
using Application.Edge.Extensions;
using Application.Edge.Interfaces;
using Application.Edge.Services.HiveStore;
using Application.StreamPipeline.Common;
using Application.StreamPipeline.Features;
using Application.StreamPipeline.Interfaces;
using Application.StreamPipeline.Services;
using DisposableHelpers.Attributes;
using Domain.Edge.Dtos;
using Domain.Edge.Enums;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Security.Cryptography;

namespace Application.Edge.Services.Handshake;

[Disposable]
internal partial class EdgeServerHandshakeService(ILogger<EdgeServerHandshakeService> logger, IEdgeHiveStoreService edgeHiveStoreService, IEdgeLocalStoreService edgeLocalStoreService)
    : ISecureStreamFactory
{
    private readonly ILogger<EdgeServerHandshakeService> _logger = logger;
    private readonly IEdgeHiveStoreService _edgeHiveStoreService = edgeHiveStoreService;
    private readonly IEdgeLocalStoreService _edgeLocalStoreService = edgeLocalStoreService;

    private CancellationTokenSource? _cts;

    public GateKeeper AcceptGate { get; } = new();

    public ValueKeeper<Guid> EdgeClientHiveIdKeeper { get; } = new();

    public RSA? ClientRsa { get; private set; } = null;

    public RSA? ServerRsa { get; private set; } = null;

    public void Begin(GateKeeper dependent, IPAddress iPAddress, StreamPipelineService streamPipelineService, CancellationToken stoppingToken)
    {
        if (_cts != null)
        {
            throw new InvalidOperationException($"{nameof(EdgeServerHandshakeService)} already started");
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(CancelWhenDisposing(), stoppingToken);

        _cts.Token.Register(Dispose);

        var handshakeCommand = streamPipelineService.SetCommandPipe<HandshakeAttemptDto, HandshakeResponseDto>(EdgeDefaults.HandshakeChannel, $"handshake_channel");

        Task.Run(async () =>
        {
            using var _ = _logger.BeginScopeMap(nameof(EdgeServerHandshakeService), nameof(Begin), new()
            {
                ["ClientAddress"] = iPAddress
            });

            try
            {
                await dependent.WaitForOpen(_cts.Token);
                if (_cts.IsCancellationRequested)
                {
                    return;
                }

                _logger.LogInformation("Attempting handshake from {ClientAddress}...", iPAddress);

                var edgeServerEntity = (await _edgeLocalStoreService.Get(_cts.Token)).GetValueOrThrow();

                handshakeCommand.OnCommand(async callback =>
                {
                    if (callback.Command == null)
                    {
                        return;
                    }
                    if (callback.Command.PublicKey != null &&
                        callback.Command.PublicKey.Length != 0)
                    {
                        if (ServerRsa != null || ClientRsa != null)
                        {
                            return;
                        }

                        ServerRsa = RSA.Create(EdgeDefaults.EdgeHandshakeRSABitsLength);

                        bool hasRsaLoaded = false;
                        try
                        {
                            ClientRsa = RSA.Create();
                            ClientRsa.ImportRSAPublicKey(callback.Command.PublicKey, out var clientRsaBytesRead);
                            hasRsaLoaded = true;
                        }
                        catch { }
                        
                        if (hasRsaLoaded)
                        {
                            callback.Respond(new HandshakeResponseDto()
                            {
                                PublicKey = ServerRsa.ExportRSAPublicKey(),
                                EncryptedAcceptedEdgeToken = null
                            });
                        }
                    }
                    if (callback.Command.EncryptedEdgeEntity != null &&
                        callback.Command.EncryptedEdgeEntity.Length != 0 &&
                        callback.Command.EncryptedHandshakeToken != null &&
                        callback.Command.EncryptedHandshakeToken.Length != 0)
                    {
                        if (ServerRsa == null || ClientRsa == null)
                        {
                            return;
                        }
                        GetEdgeWithTokenDto? edgeEntity = null;
                        try
                        {
                            string? decryptedHandshakeToken = SecureDataHelpers.Decrypt<string>(callback.Command.EncryptedHandshakeToken, ServerRsa);
                            if (edgeServerEntity.Token == decryptedHandshakeToken)
                            {
                                edgeEntity = SecureDataHelpers.Decrypt<GetEdgeWithTokenDto>(callback.Command.EncryptedEdgeEntity, ServerRsa);
                            }
                        }
                        catch { }
                        if (edgeEntity != null)
                        {
                            var addEdgeDto = new AddEdgeDto()
                            {
                                EdgeType = EdgeType.Client,
                                Name = edgeEntity.Name
                            };
                            bool hasCreated = false;
                            if ((await _edgeHiveStoreService.GetOrCreate(edgeEntity.Id.ToString(), () =>
                            {
                                hasCreated = true;
                                return addEdgeDto;
                            })).SuccessAndHasValue(out var edgeClientEntity))
                            {
                                callback.Respond(new HandshakeResponseDto()
                                {
                                    PublicKey = [],
                                    EncryptedAcceptedEdgeToken = SecureDataHelpers.Encrypt(edgeEntity.Token, ClientRsa)
                                });
                                EdgeClientHiveIdKeeper.SetValue(edgeClientEntity.Id);
                                AcceptGate.SetOpen();
                                if (hasCreated)
                                {
                                    _logger.LogInformation("New edge client {ClientAddress}:{EdgeClientId} added", iPAddress, edgeClientEntity.Id);
                                }
                            }
                        }
                    }
                });

                if (await AcceptGate.WaitForOpen(_cts.Token.WithTimeout(EdgeDefaults.HandshakeTimeout)))
                {
                    _logger.LogInformation("Handshake {ClientAddress} accepted", iPAddress);
                }
                else
                {
                    throw new OperationCanceledException();
                }
            }
            catch (Exception ex)
            {
                if (ex is ObjectDisposedException ||
                    ex is OperationCanceledException)
                {
                    _logger.LogInformation("Handshake {ClientAddress} declined: Expired", iPAddress);
                }
                else
                {
                    _logger.LogError("Handshake {ClientAddress} declined: {ErrorMessage}", iPAddress, ex.Message);
                }
                _cts.Cancel();
            }
            finally
            {
                AcceptGate.SetOpen();
            }

        }, stoppingToken);
    }

    public TranceiverStream CreateSecureTranceiverStream(int capacity)
    {
        if (_cts == null || ClientRsa == null || ServerRsa == null)
        {
            throw new Exception("Handshake incomplete");
        }

        var tranceiverStream = new TranceiverStream(new BlockingMemoryStream(capacity), new BlockingMemoryStream(capacity));

        _cts.Token.Register(tranceiverStream.Dispose);

        return tranceiverStream;
    }

    protected void Dispose(bool disposing)
    {
        if (disposing)
        {
            AcceptGate.SetOpen();
            ClientRsa?.Dispose();
            ServerRsa?.Dispose();
            _cts?.Dispose();
        }
    }
}
