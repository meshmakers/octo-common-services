namespace Meshmakers.Octo.Services.Common.Timeseries;

internal class CrateQueryBuilder
{
    private readonly HashSet<string> _variablesToInclude = new();
    private readonly HashSet<AggregationFunctions> _aggregationFunctionsToInclude = new();
    private readonly DateTime _from;
    private readonly DateTime _to;
    private DateTimeOffset _interval;
    
    public CrateQueryBuilder(DateTime from, DateTime to, DateTimeOffset interval)
    {
        if (from > to)
        {
            throw new ArgumentException("From must be before to");
        }

        _from = from;
        _to = to;
    }
    
    public CrateQueryBuilder AddVariable(string variable)
    {
        _variablesToInclude.Add(variable);
        return this;
    }
    
    public CrateQueryBuilder AddAggregationFunction(AggregationFunctions aggregationFunction)
    {
        _aggregationFunctionsToInclude.Add(aggregationFunction);
        return this;
    }
    
    public CrateQueryBuilder WithInterval(DateTimeOffset interval)
    {
        _interval = interval;
        return this;
    }

    public CrateQueryBuilder WithMaxDataPoints(int maxDataPoints)
    {
        var timespan = _to - _from;
        return this;
    }


    private abstract class IntervalProvider
    {
        public abstract string GetInterval();
    }
    
    private class FixedIntervalProvider : IntervalProvider
    {
        private readonly int _interval;
        private readonly FixedInterval _intervalType;


        public FixedIntervalProvider(int interval, FixedInterval intervalType)
        {
            _interval = interval;
            _intervalType = intervalType;
        }
        
        public override string GetInterval()
        {
            return "date_trunc('hour', t1.\"timestamp\") AS Timestamp";
        }
    }
}


internal enum FixedInterval
{
    Second,
    Minute,
    Hour,
    Day,
    Week,
    Month,
    Year
}


internal enum AggregationFunctions
{
    Avg,
    Min,
    Max,
    Sum,
    Count
}