using Application.Common;
using Application.Configuration.Extensions;
using Application.Edge.Interfaces;
using Domain.Edge.Dtos;
using Domain.Edge.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RestfulHelpers;
using RestfulHelpers.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Application.Common;

public class GateKeeper
{
    private readonly ManualResetEventSlim _waiterEvent = new(false);

    public bool IsOpen { get; private set; }

    public GateKeeper(bool initialOpenState = false)
    {
        SetOpen(initialOpenState);
    }

    public void SetOpen(bool isOpen = true)
    {
        IsOpen = isOpen;

        if (isOpen)
        {
            _waiterEvent.Set();
        }
        else
        {
            _waiterEvent.Reset();
        }
    }

    public Task<bool> WaitForOpen(CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            try
            {
                _waiterEvent.Wait(cancellationToken);
                return true;
            }
            catch
            {
                return false;
            }

        }, cancellationToken);
    }
}
