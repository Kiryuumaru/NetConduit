using Application.Common.Extensions;
using Application.Common.Features;
using Application.Configuration.Extensions;
using Application.Edge.Common;
using Application.Edge.Interfaces;
using Application.Edge.Workers;
using Application.StreamPipeline.Common;
using Application.StreamPipeline.Interfaces;
using Application.StreamPipeline.Services;
using DisposableHelpers.Attributes;
using Domain.Edge.Dtos;
using Domain.Edge.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

namespace Application.Edge.Services.Handshake;

[Disposable]
internal partial class EdgeClientHandshakeService(ILogger<EdgeClientHandshakeService> logger, IConfiguration configuration, IEdgeLocalStoreService edgeLocalStoreService)
    : ISecureStreamFactory
{
    private readonly ILogger<EdgeClientHandshakeService> _logger = logger;
    private readonly IConfiguration _configuration = configuration;
    private readonly IEdgeLocalStoreService _edgeLocalStoreService = edgeLocalStoreService;

    private CancellationTokenSource? _cts;

    public GateKeeper AcceptGate { get; } = new();

    public RSA? ClientRsa { get; private set; } = null;

    public RSA? ServerRsa { get; private set; } = null;

    public void Begin(GateKeeper dependent, string tcpHost, int tcpPort, StreamPipelineService streamPipelineService, CancellationToken stoppingToken)
    {
        if (_cts != null)
        {
            throw new InvalidOperationException($"{nameof(EdgeClientHandshakeService)} already started");
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(CancelWhenDisposing(), stoppingToken);

        _cts.Token.Register(Dispose);

        var handshakeCommand = streamPipelineService.SetCommandPipe<HandshakeAttemptDto, HandshakeResponseDto>(EdgeDefaults.HandshakeChannel, $"handshake_channel");

        Task.Run(async () =>
        {
            using var _ = _logger.BeginScopeMap(nameof(EdgeClientHandshakeService), nameof(Begin), new()
            {
                ["ServerHost"] = tcpHost,
                ["ServerPort"] = tcpPort
            });

            try
            {
                await dependent.WaitForOpen(_cts.Token);
                if (_cts.IsCancellationRequested)
                {
                    return;
                }

                _logger.LogInformation("Attempting handshake to {ServerHost}:{ServerPort}...", tcpHost, tcpPort);

                var stoppingToken = _cts.Token.WithTimeout(EdgeDefaults.HandshakeTimeout);

                var edgeTokenedEntity = (await _edgeLocalStoreService.Get(_cts.Token)).GetValueOrThrow();
                var edgeKeyedEntity = EdgeEntityHelpers.Decode(edgeTokenedEntity.Token);

                ClientRsa = RSA.Create(EdgeDefaults.EdgeHandshakeRSABitsLength);

                var initialHandshakeRequest = new HandshakeAttemptDto()
                {
                    PublicKey = ClientRsa.ExportRSAPublicKey(),
                    EncryptedEdgeToken = null,
                    EncryptedHandshakeToken = null,
                };
                var handshakeRequestResult = await handshakeCommand.Send(initialHandshakeRequest, stoppingToken);
                if (!handshakeRequestResult.SuccessAndHasValue(out HandshakeResponseDto? initialHandshakeResponse) ||
                    initialHandshakeResponse.PublicKey == null ||
                    initialHandshakeResponse.PublicKey.Length == 0)
                {
                    throw new Exception("Premature handshake sequence");
                }

                var handshakeToken = _configuration.GetHandshakeToken();

                ServerRsa = RSA.Create();

                byte[] encryptedEdgeToken;
                byte[] encryptedHandshakeToken;
                try
                {
                    ServerRsa.ImportRSAPublicKey(initialHandshakeResponse.PublicKey, out var serverRsaPublicKeyBytesRead);
                    encryptedEdgeToken = SecureDataHelpers.EncryptString(edgeTokenedEntity.Token, ServerRsa);
                    encryptedHandshakeToken = SecureDataHelpers.EncryptString(handshakeToken, ServerRsa);
                }
                catch
                {
                    throw new Exception("Premature handshake sequence");
                }

                var tokenHandshakeRequest = new HandshakeAttemptDto()
                {
                    PublicKey = null,
                    EncryptedEdgeToken = encryptedEdgeToken,
                    EncryptedHandshakeToken = encryptedHandshakeToken,
                };
                var handshakeEstablishResult = await handshakeCommand.Send(tokenHandshakeRequest, stoppingToken);
                if (!handshakeEstablishResult.SuccessAndHasValue(out HandshakeResponseDto? tokenHandshakeResponse) ||
                    tokenHandshakeResponse.EncryptedAcceptedEdgeKey == null ||
                    tokenHandshakeResponse.EncryptedAcceptedEdgeKey.Length == 0)
                {
                    throw new Exception("Invalid handshake token");
                }

                try
                {
                    if (SecureDataHelpers.Decrypt(tokenHandshakeResponse.EncryptedAcceptedEdgeKey, ClientRsa) is not byte[] acceptedEdgeKey ||
                        !edgeKeyedEntity.Key.SequenceEqual(acceptedEdgeKey))
                    {
                        throw new Exception();
                    }
                }
                catch
                {
                    throw new Exception("Invalid handshake token");
                }

                _logger.LogInformation("Handshake {ServerHost}:{ServerPort} accepted", tcpHost, tcpPort);
            }
            catch (Exception ex)
            {
                _logger.LogError("Handshake {ServerHost}:{ServerPort} declined: {ErrorMessage}", tcpHost, tcpPort, ex.Message);
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
