using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using StackExchange.Redis;

using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using MichelOliveira.Com.ReactiveLock.Core;
using MichelOliveira.Com.ReactiveLock.DependencyInjection;

public class CountingHandler : DelegatingHandler
{
    private IReactiveLockTrackerController ReactiveLockTrackerController { get; set; }
    private IReactiveLockTrackerState ReactiveLockTrackerState { get; set; }

    public CountingHandler(IReactiveLockTrackerFactory reactiveLockTrackerFactory)
    {
        ReactiveLockTrackerController = reactiveLockTrackerFactory.GetTrackerController(Constant.REACTIVELOCK_HTTP_NAME);
        ReactiveLockTrackerState = reactiveLockTrackerFactory.GetTrackerState(Constant.REACTIVELOCK_HTTP_NAME);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        bool isNotBlocked = !await ReactiveLockTrackerState.IsBlockedAsync();
        if (isNotBlocked)
            await ReactiveLockTrackerController.IncrementAsync().ConfigureAwait(false);
        
        try
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (isNotBlocked)
                await ReactiveLockTrackerController.DecrementAsync().ConfigureAwait(false);
        }
    }
}
