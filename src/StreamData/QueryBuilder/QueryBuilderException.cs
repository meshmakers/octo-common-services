namespace Meshmakers.Octo.Services.Common.StreamData.QueryBuilder;

/// <summary>
/// Query Builder Exception
/// </summary>
public class QueryBuilderException : Exception
{
    private QueryBuilderException()
    {
    }

    /// <summary>
    /// Query Builder Exception
    /// </summary>
    /// <param name="message"></param>
    private QueryBuilderException(string message) : base(message)
    {
    }

    /// <summary>
    /// Query Builder Exception
    /// </summary>
    /// <param name="message"></param>
    /// <param name="inner"></param>
    private QueryBuilderException(string message, Exception inner) : base(message, inner)
    {
    }
    
    internal static QueryBuilderException OrderByVariableNotFound(string variableName)
    {
        return new QueryBuilderException($"OrderBy Variable not found: '{variableName}'");
    }

    internal static QueryBuilderException LimitMustBeGreaterThanZero()
    {
        return new QueryBuilderException("Limit must be greater than zero");
    }
    
    internal static QueryBuilderException OffsetMustBeGreaterThanZero()
    {
        return new QueryBuilderException("Offset must be a positive integer");
    }

    internal static QueryBuilderException WhereInVariableNotFound(string variableName)
    {
        return new QueryBuilderException($"WhereIn Variable not found: '{variableName}'");
    }
}