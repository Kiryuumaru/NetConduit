using Application.Common;
using Domain.Edge.Entities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Infrastructure.SignalR.Server.Handshake.Services;

public class HandshakeLockerService
{
    private readonly ConcurrentDictionary<string, EdgeConnectionEntity> _lockedEdgeConnectionEntities = [];

    public void Lock(EdgeConnectionEntity edgeConnectionEntity)
    {
        if (!_lockedEdgeConnectionEntities.TryAdd(edgeConnectionEntity.Id, edgeConnectionEntity))
        {
            throw new Exception("Edge already locked");
        }
    }

    public void Unlock(EdgeConnectionEntity edgeConnectionEntity)
    {
        _lockedEdgeConnectionEntities.TryRemove(edgeConnectionEntity.Id, out _);
    }
}
