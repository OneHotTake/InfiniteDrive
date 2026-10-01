using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Models;
using InfiniteDrive.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InfiniteDrive.Tests;
public sealed class ImportRecoveryPolicyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-01T14:00:00Z");
    [Fact] public void ItemFailureLaddersAreMonotoneCappedAndSeparateFromDispatchAttempts()
    {
        var e = new ImportEpisode { Attempts = 1 };
        for(var step=1;step<=10;step++)
        { ImportCoveragePolicy.Failed(e,"transport_failure",Now,0,null); Assert.Equal(step,e.ConsecutiveFailures);
          Assert.True(e.NextAttempt<=Now.AddDays(7)); }
        ImportCoveragePolicy.Failed(e,"source_unavailable",Now,.2,Now.AddDays(10));
        Assert.Equal(1,e.ConsecutiveFailures); Assert.Equal(Now.AddDays(10),e.NextAttempt); Assert.Equal(1,e.Attempts);
    }
    [Fact] public void ResponseCircuitEscalatesOncePerBatchAndNeedsRecentResponsiveReplies()
    {
        var h = new ImportSourceHealth(); for(var i=0;i<64;i++) h.Record("transport_failure",Now);
        Assert.Equal(1,h.Escalation); Assert.Equal(Now.AddMinutes(5),h.PausedUntil);
        var later=Now.AddMinutes(6); for(var i=0;i<3;i++) h.Record("lookup_deadline",later);
        Assert.Equal(2,h.Escalation); Assert.Equal(later.AddMinutes(15),h.PausedUntil);
        later=later.AddMinutes(16); h.Record("source_unavailable",later); h.Record("",later); h.Record("",later);
        Assert.True(h.RecoveryReady(later)); Assert.False(h.RecoveryReady(later.AddMinutes(16)));
        Assert.Equal(0,h.Escalation); h.Record("cancelled",later); Assert.True(h.RecoveryReady(later));
        h.Record("source_unavailable",later.AddMinutes(16)); Assert.False(h.RecoveryReady(later.AddMinutes(16)));
        var emptyOnly = new ImportSourceHealth(); for(var i=0;i<3;i++) emptyOnly.Record("source_unavailable",Now);
        Assert.False(emptyOnly.RecoveryReady(Now));
    }
    [Fact] public async Task MaintenanceTransportFailureSubmitsOnlyOnceAndMarksItsLane()
    {
        var handler = new FailureHandler(); using var http = new HttpClient(handler);
        using var client = new AioStreamsClient("https://example.invalid/stremio",null,"",NullLogger.Instance)
            { MaintenanceResolution = true, HttpTransport = http };
        await Assert.ThrowsAsync<AioStreamsUnreachableException>(()=>client.GetMovieStreamsAsync("tt999999999"));
        Assert.Equal(1,handler.Calls); Assert.True(handler.MaintenanceHeader);
    }
    private sealed class FailureHandler : HttpMessageHandler
    {
        public int Calls; public bool MaintenanceHeader;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        { Calls++; MaintenanceHeader=request.Headers.Contains("X-AIOStreams-Maintenance"); throw new HttpRequestException("test"); }
    }
}
