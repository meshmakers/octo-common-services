namespace Meshmakers.Octo.Services.StreamData.Dtos;

/// <summary>
/// Operators for stream data field filters
/// </summary>
public enum StreamDataFieldFilterOperator
{
    /// <summary>Equality check (=)</summary>
    Equals,

    /// <summary>Inequality check (!=)</summary>
    NotEquals,

    /// <summary>Greater than (&gt;)</summary>
    GreaterThan,

    /// <summary>Greater than or equal (&gt;=)</summary>
    GreaterThanOrEqual,

    /// <summary>Less than (&lt;)</summary>
    LessThan,

    /// <summary>Less than or equal (&lt;=)</summary>
    LessThanOrEqual,

    /// <summary>SQL LIKE pattern match</summary>
    Like
}

/// <summary>
/// A single field filter condition for stream data queries
/// </summary>
/// <param name="FieldName">Column name</param>
/// <param name="Operator">Comparison operator</param>
/// <param name="Value">Comparison value</param>
/// <param name="IsDataField">Whether the field is a dynamic data column (data['name'] syntax)</param>
public record StreamDataFieldFilterDto(
    string FieldName,
    StreamDataFieldFilterOperator Operator,
    string Value,
    bool IsDataField);
