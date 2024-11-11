using Application.StreamPipeline.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Application.Edge.Common;

internal class EdgeRoutingMap
{
    private readonly Dictionary<Guid, StreamTranceiver> _guidStreamPipe = [];
    private readonly Dictionary<int, StreamTranceiver> _indexStreamPipe = [];
    private readonly Dictionary<Guid, int> _guidIndex = [];
    private readonly Dictionary<int, Guid> _indexGuid = [];

    private readonly ReaderWriterLockSlim _rwl = new();

    public void Set(Guid guid, int index, StreamTranceiver streamPipe)
    {
        try
        {
            _guidStreamPipe[guid] = streamPipe;
            _indexStreamPipe[index] = streamPipe;
            _guidIndex[guid] = index;
            _indexGuid[index] = guid;
            _rwl.EnterWriteLock();
        }
        finally
        {
            _rwl.ExitWriteLock();
        }
    }

    public bool Remove(Guid guid)
    {
        try
        {
            _rwl.EnterUpgradeableReadLock();
            if (_guidIndex.TryGetValue(guid, out var index))
            {
                try
                {
                    _rwl.EnterWriteLock();
                    _guidStreamPipe.Remove(guid);
                    _indexStreamPipe.Remove(index);
                    _guidIndex.Remove(guid);
                    _indexGuid.Remove(index);
                    return true;
                }
                finally
                {
                    _rwl.ExitWriteLock();
                }
            }
        }
        finally
        {
            _rwl.ExitUpgradeableReadLock();
        }
        return false;
    }

    public bool Remove(int index)
    {
        try
        {
            _rwl.EnterUpgradeableReadLock();
            if (_indexGuid.TryGetValue(index, out var guid))
            {
                try
                {
                    _rwl.EnterWriteLock();
                    _guidStreamPipe.Remove(guid);
                    _indexStreamPipe.Remove(index);
                    _guidIndex.Remove(guid);
                    _indexGuid.Remove(index);
                    return true;
                }
                finally
                {
                    _rwl.ExitWriteLock();
                }
            }
        }
        finally
        {
            _rwl.ExitUpgradeableReadLock();
        }
        return false;
    }

    public StreamTranceiver Get(Guid guid)
    {
        try
        {
            _rwl.EnterReadLock();
            return _guidStreamPipe[guid];
        }
        finally
        {
            _rwl.ExitReadLock();
        }
    }

    public StreamTranceiver Get(int index)
    {
        try
        {
            _rwl.EnterReadLock();
            return _indexStreamPipe[index];
        }
        finally
        {
            _rwl.ExitReadLock();
        }
    }

    public bool Contains(Guid guid)
    {
        try
        {
            _rwl.EnterReadLock();
            return _guidStreamPipe.ContainsKey(guid);
        }
        finally
        {
            _rwl.ExitReadLock();
        }
    }

    public bool Contains(int index)
    {
        try
        {
            _rwl.EnterReadLock();
            return _indexStreamPipe.ContainsKey(index);
        }
        finally
        {
            _rwl.ExitReadLock();
        }
    }
}
