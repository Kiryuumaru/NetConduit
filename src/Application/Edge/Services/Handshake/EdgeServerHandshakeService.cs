using Application.Common;
using Application.Edge.Common;
using Application.Edge.Interfaces;
using Application.Edge.Workers;
using Application.StreamPipeline.Common;
using Application.StreamPipeline.Interfaces;
using Application.StreamPipeline.Services;
using DisposableHelpers.Attributes;
using Domain.Edge.Dtos;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Application.Edge.Services.Handshake;

[Disposable]
internal partial class EdgeServerHandshakeService(ILogger<EdgeServerHandshakeService> logger, IEdgeLocalStoreService edgeLocalStoreService)
    : ISecureStreamFactory
{
    private readonly ILogger<EdgeServerHandshakeService> _logger = logger;
    private readonly IEdgeLocalStoreService _edgeLocalStoreService = edgeLocalStoreService;

    private CancellationTokenSource? _cts;

    public GateKeeper AcceptGate { get; } = new();

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

                handshakeCommand.OnCommand(callback =>
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
                                EncryptedAcceptedEdgeKey = null
                            });
                        }
                    }
                    if (callback.Command.EncryptedEdgeToken != null &&
                        callback.Command.EncryptedEdgeToken.Length != 0 &&
                        callback.Command.EncryptedHandshakeToken != null &&
                        callback.Command.EncryptedHandshakeToken.Length != 0)
                    {
                        if (ServerRsa == null || ClientRsa == null)
                        {
                            return;
                        }
                        GetEdgeWithKeyDto? edgeEntity = null;
                        try
                        {
                            string decryptedHandshakeToken = SecureDataHelpers.DecryptString(callback.Command.EncryptedHandshakeToken, ServerRsa);
                            if (edgeServerEntity.Token == decryptedHandshakeToken)
                            {
                                edgeEntity = EdgeEntityHelpers.Decode(SecureDataHelpers.DecryptString(callback.Command.EncryptedEdgeToken, ServerRsa));
                            }
                        }
                        catch { }
                        if (edgeEntity != null)
                        {
                            callback.Respond(new HandshakeResponseDto()
                            {
                                PublicKey = [],
                                EncryptedAcceptedEdgeKey = SecureDataHelpers.Encrypt(edgeEntity.Key, ClientRsa)
                            });
                            AcceptGate.SetOpen();
                        }
                    }
                });

                if (await AcceptGate.WaitForOpen(_cts.Token.WithTimeout(EdgeDefaults.HandshakeTimeout)))
                {
                    _logger.LogInformation("Handshake {ClientAddress} accepted", iPAddress);
                }
                else
                {
                    _logger.LogInformation("Handshake {ClientAddress} declined: Expired", iPAddress);
                    _cts.Cancel();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Handshake {ClientAddress} declined: {ErrorMessage}", iPAddress, ex.Message);
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
