using Application.Common;
using Application.Edge.Common;
using Application.Edge.Interfaces;
using Application.Edge.Workers;
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
{
    private readonly ILogger<EdgeServerHandshakeService> _logger = logger;
    private readonly IEdgeLocalStoreService _edgeLocalStoreService = edgeLocalStoreService;

    private CancellationTokenSource? _cts;

    public GateKeeper Gate { get; } = new();

    public RSA? Rsa { get; private set; } = null;

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

                var edgeLocalEntity = (await _edgeLocalStoreService.Get(_cts.Token)).GetValueOrThrow();
                byte[] requestToken = [];
                byte[] privateKey = [];
                byte[] publicKey = [];

                handshakeCommand.OnCommand(callback =>
                {
                    if (callback.Command == null)
                    {
                        return;
                    }
                    if (callback.Command.RequestToken.Length != 0)
                    {
                        if (Rsa != null)
                        {
                            return;
                        }
                        try
                        {
                            Rsa = RSA.Create(EdgeDefaults.EdgeHandshakeRSABitsLength);
                            privateKey = Rsa.ExportRSAPrivateKey();
                            publicKey = Rsa.ExportRSAPublicKey();
                        }
                        catch { }
                        if (publicKey.Length != 0)
                        {
                            requestToken = callback.Command.RequestToken;
                            callback.Respond(new HandshakeResponseDto()
                            {
                                PublicKey = publicKey,
                                RequestAcknowledgedToken = []
                            });
                        }
                    }
                    else if (callback.Command.EncryptedHandshakeToken.Length != 0 && requestToken.Length != 0)
                    {
                        if (Rsa == null)
                        {
                            return;
                        }
                        bool isOk = false;
                        try
                        {
                            byte[] decryptedHandshakeToken = SecureDataHelpers.Decrypt(callback.Command.EncryptedHandshakeToken, privateKey);
                            string handshakeToken = Encoding.UTF8.GetString(decryptedHandshakeToken);
                            if (edgeLocalEntity.Token == handshakeToken)
                            {
                                isOk = true;
                            }
                        }
                        catch { }
                        if (isOk)
                        {
                            byte[] requestAcknowledgedToken = Rsa.Encrypt(requestToken, RSAEncryptionPadding.OaepSHA256);
                            callback.Respond(new HandshakeResponseDto()
                            {
                                PublicKey = [],
                                RequestAcknowledgedToken = requestAcknowledgedToken
                            });
                            Gate.SetOpen();
                        }
                    }
                });

                if (await Gate.WaitForOpen(_cts.Token.WithTimeout(EdgeDefaults.HandshakeTimeout)))
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
                Gate.SetOpen();
            }

        }, stoppingToken);
    }

    protected void Dispose(bool disposing)
    {
        if (disposing)
        {
            Gate.SetOpen();
            Rsa?.Dispose();
            _cts?.Dispose();
        }
    }
}
