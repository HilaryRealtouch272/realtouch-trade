using System.Text.Json;
using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Providers;
namespace RealtouchSmartTrade.Tests;
public class MarketHistoryBridgeTests : IDisposable
{
    private readonly string path=Path.Combine(Path.GetTempPath(),"rst-market-"+Guid.NewGuid()+".json");
    private static readonly DateTime Now=new(2026,11,23,12,0,0,DateTimeKind.Utc);
    private List<object> Rows()=>Enumerable.Range(0,360).Select(i=>Now.Date.AddDays(i-359))
        .Where(t=>t.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
        .Select(t=>(object)new {openTimeUtc=t,closeTimeUtc=t.AddDays(1),open=100,high=103,low=97,close=101}).ToList();
    private void Write(DateTime? generated=null,string provider="Exness",string feed="USOILm",List<object>? rows=null,string? error=null)
    {
        var m=new Dictionary<string,object?>{{"provider",provider},{"feedSymbol",feed},{"candles",rows??Rows()}};
        if(error!=null)m["error"]=error;
        File.WriteAllText(path,JsonSerializer.Serialize(new {version=1,generatedAtUtc=generated??Now,
            markets=new Dictionary<string,object>{{"WTI/USD",m}}}));
    }
    [Fact] public void FreshDailyHistoryDistinguishesFormingBarAndKeepsSource()
    {
        Write();var bars=MarketHistoryBridge.Load(path,"WTI/USD",Timeframe.Daily,Now);
        Assert.True(bars.Count>220);Assert.False(bars[^1].IsComplete);
        Assert.True(bars[^2].IsComplete);Assert.All(bars,b=>Assert.Equal("Exness",b.Provider));
    }
    [Theory][InlineData(-21)][InlineData(3)] public void StaleOrFutureSnapshotFailsClosed(int minutes)
    {
        Write(Now.AddMinutes(minutes));Assert.Throws<InvalidOperationException>(()=>MarketHistoryBridge.Load(path,"WTI/USD",Timeframe.Daily,Now));
    }
    [Fact] public void WrongInstrumentCannotMasqueradeAsOil()
    {
        Write(feed:"US500m");Assert.Throws<InvalidOperationException>(()=>MarketHistoryBridge.Load(path,"WTI/USD",Timeframe.Daily,Now));
    }
    [Fact] public void DuplicateDailyBarsAreRejected()
    {
        var rows=Rows();rows.Add(rows[0]);Write(rows:rows);
        Assert.Throws<InvalidOperationException>(()=>MarketHistoryBridge.Load(path,"WTI/USD",Timeframe.Daily,Now));
    }
    [Fact] public void UpstreamErrorIsNotSilentlyReplacedByOldYahooData()
    {
        Write(error:"unavailable");Assert.Throws<InvalidOperationException>(()=>MarketHistoryBridge.Load(path,"WTI/USD",Timeframe.Daily,Now));
    }
    [Fact] public void WeeklyAggregationUsesMondayUtcAndLeavesCurrentWeekIncomplete()
    {
        Write();var bars=MarketHistoryBridge.Load(path,"WTI/USD",Timeframe.Weekly,Now);
        Assert.All(bars,b=>Assert.Equal(DayOfWeek.Monday,b.OpenTimeUtc.DayOfWeek));
        Assert.False(bars[^1].IsComplete);Assert.Equal(103m,bars[^1].High);
    }
    public void Dispose(){File.Delete(path);}
}
