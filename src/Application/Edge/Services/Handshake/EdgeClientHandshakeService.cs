using Application.Common;
using Application.Configuration.Extensions;
using Application.Edge.Common;
using Application.Edge.Interfaces;
using Application.Edge.Workers;
using Application.StreamPipeline.Services;
using DisposableHelpers.Attributes;
using Domain.Edge.Dtos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Application.Edge.Services.Handshake;

[Disposable]
internal partial class EdgeClientHandshakeService(ILogger<EdgeClientHandshakeService> logger, IConfiguration configuration, IEdgeLocalStoreService edgeLocalStoreService)
{
    private readonly ILogger<EdgeClientHandshakeService> _logger = logger;
    private readonly IConfiguration _configuration = configuration;
    private readonly IEdgeLocalStoreService _edgeLocalStoreService = edgeLocalStoreService;

    private CancellationTokenSource? _cts;

    public GateKeeper Gate { get; } = new();

    public RSA? Rsa { get; private set; } = null;

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

                var edgeLocalEntity = (await _edgeLocalStoreService.Get(_cts.Token)).GetValueOrThrow();
                byte[] requestToken = RandomHelpers.ByteArray(EdgeDefaults.EdgeHandshakeRequestLength);

                var initialHandshakeRequest = new HandshakeAttemptDto()
                {
                    EdgeWithTokenDto = edgeLocalEntity,
                    EncryptedHandshakeToken = []
                };
                var handshakeRequestResult = await handshakeCommand.Send(initialHandshakeRequest, stoppingToken);
                if (!handshakeRequestResult.SuccessAndHasValue(out HandshakeResponseDto? initialHandshakeResponse) ||
                    initialHandshakeResponse.PublicKey.Length == 0)
                {
                    throw new Exception("Premature handshake sequence");
                }

                var handshakeToken = _configuration.GetHandshakeToken();
                byte[] handshakeTokenBytes = Encoding.UTF8.GetBytes(handshakeToken);

                byte[] requestAcknowledgedToken;
                byte[] encryptedHandshakeTokenBytes;
                try
                {
                    requestAcknowledgedToken = SecureDataHelpers.Encrypt(requestToken, initialHandshakeResponse.PublicKey);
                    encryptedHandshakeTokenBytes = SecureDataHelpers.Encrypt(handshakeTokenBytes, initialHandshakeResponse.PublicKey);
                }
                catch (Exception ex)
                {
                    throw new Exception("Premature handshake sequence");
                }

                var tokenHandshakeRequest = new HandshakeAttemptDto()
                {
                    EdgeWithTokenDto = null,
                    EncryptedHandshakeToken = encryptedHandshakeTokenBytes
                };
                var handshakeEstablishResult = await handshakeCommand.Send(tokenHandshakeRequest, stoppingToken);
                if (!handshakeRequestResult.SuccessAndHasValue(out HandshakeResponseDto? tokenHandshakeResponse) ||
                    tokenHandshakeResponse.RequestAcknowledgedToken.Length == 0 ||
                    !tokenHandshakeResponse.RequestAcknowledgedToken.SequenceEqual(requestAcknowledgedToken))
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
