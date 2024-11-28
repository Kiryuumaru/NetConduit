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

public class BooleanWaiter
{
    private readonly ManualResetEventSlim _waiterEvent = new(false);

    public bool Value { get; private set; }

    public void SetValue(bool value)
    {
        Value = value;

        if (value)
        {
            _waiterEvent.Set();
        }
        else
        {
            _waiterEvent.Reset();
        }
    }

    public Task Wait(CancellationToken cancellationToken)
    {
        return Task.Run(() => _waiterEvent.Wait(cancellationToken), cancellationToken);
    }
}
