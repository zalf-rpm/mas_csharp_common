using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mas.Schema.Common;
using Mas.Schema.Registry;
using IAdmin = Mas.Schema.Service.IAdmin;
using Timer = System.Timers.Timer;

namespace Mas.Infrastructure.Common.Service;

public class Admin : IAdmin
{
    private readonly Timer _killTimer = new();

    private readonly IRegistry _registry;
    private readonly Timer _timer = new();

    private readonly Action<IdInformation> _updateIdentity;

    public Admin(IRegistry registry, Action<IdInformation> updateIdentity)
    {
        _registry = registry;
        _timer.AutoReset = false;
        _updateIdentity = updateIdentity;
    }

    public string Id { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }

    public void Dispose()
    {
    }

    //private void store_unreg_data(name, unreg_action, rereg_sr)
    //{
    //    self._unreg_sturdy_refs[name] = (unreg_action, rereg_sr)
    //}


    #region implementation of Mas.Rpc.Common.IIdentifiable

    public Task<IdInformation> Info(CancellationToken cancellationToken_ = default)
    {
        return Task.FromResult(new IdInformation
            { Id = Id, Name = Name, Description = Description });
    }

    #endregion

    #region implementation of Persistence.IPersistent

    // heartbeat @0 ();
    public Task Heartbeat(CancellationToken cancellationToken_ = default)
    {
        _timer.Stop();
        _timer.Start();
        return Task.CompletedTask;
    }

    // setTimeout @1 (seconds :UInt64);
    public Task SetTimeout(ulong seconds, CancellationToken cancellationToken_ = default)
    {
        _timer.Interval = Math.Max(0, seconds * 1000);
        if (_timer.Interval > 0)
            _timer.Start();
        else
            _timer.Stop();
        return Task.CompletedTask;
    }

    // stop @2 ();
    public Task Stop(CancellationToken cancellationToken_ = default)
    {
        _killTimer.Interval = 2000;
        _killTimer.Elapsed += (s, e) => Environment.Exit(0);
        _killTimer.Start();
        return Task.CompletedTask;
    }

    // identity @3 () -> Common.IdInformation;
    public Task<IReadOnlyList<IdInformation>> Identities(CancellationToken cancellationToken_ = default)
    {
        var info = _registry.Info().Result;
        IReadOnlyList<IdInformation> l = new List<IdInformation>
        {
            new() { Id = info.Id, Name = info.Name, Description = info.Description }
        };
        return Task.FromResult(l);
    }

    // updateIdentity @4 Common.IdInformation;
    public Task UpdateIdentity(string oldId, IdInformation info, CancellationToken cancellationToken_ = default)
    {
        _updateIdentity(info);
        return Task.CompletedTask;
    }

    #endregion
}