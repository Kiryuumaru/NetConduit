using DisposableHelpers;
using Domain.PortRoute.Entities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Application.Server.PortRoute.Services;

public class PortRouteEventHubService
{
    private readonly ConcurrentDictionary<string, List<Action>> _subscriptions = [];

    public void OnPortRouteChanges(string edgeId)
    {
        if (_subscriptions.TryGetValue(edgeId, out var subscriptions))
        {
            foreach (var s in subscriptions)
            {
                Task.Run(s.Invoke);
            }
        }
    }

    public IDisposable SubscribeEvent(string edgeId, Action onPortRouteChanges)
    {
        _subscriptions.AddOrUpdate(edgeId, _ => [onPortRouteChanges], (_, s) =>
        {
            return new List<Action>(s)
            {
                onPortRouteChanges
            };
        });
        return new Disposable(disposing =>
        {
            if (disposing)
            {
                _subscriptions.AddOrUpdate(edgeId, _ => [], (_, s) =>
                {
                    var newS = new List<Action>(s);
                    newS.Remove(onPortRouteChanges);
                    return newS;
                });
            }
        });
    }
}
