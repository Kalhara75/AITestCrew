using System.Text.Json.Serialization;

namespace AiTestCrew.Agents.DbAgent;

/// <summary>
/// Operators supported by <see cref="ColumnAssertion"/>. Serialised as the
/// camelCased enum name (e.g. <c>"equals"</c>, <c>"isNull"</c>).
///
/// <list type="bullet">
///   <item><description>String ops (<c>Equals</c>, <c>NotEquals</c>, <c>Contains</c>, <c>NotContains</c>,
///     <c>StartsWith</c>, <c>EndsWith</c>, <c>Regex</c>) work on the column's string projection.</description></item>
///   <item><description><c>GreaterThan</c> / <c>LessThan</c> / <c>Between</c> compare via decimal first,
///     falling back to <see cref="System.DateTimeOffset"/> for date-shaped values.</description></item>
///   <item><description><c>IsNull</c> / <c>IsNotNull</c> distinguish SQL NULL (or "JSON null" after
///     a path extraction) from empty string. <c>"missing path"</c> behaves like SQL NULL
///     for these operators.</description></item>
///   <item><description><c>EqualsNumeric</c> parses both sides as <see cref="decimal"/> with
///     <see cref="System.Globalization.CultureInfo.InvariantCulture"/> and compares with
///     <see cref="ColumnAssertion.ToleranceDelta"/>.</description></item>
///   <item><description><c>EqualsDate</c> parses both sides as <see cref="System.DateTimeOffset"/>
///     (falling back to <see cref="System.DateTime"/>) and compares with
///     <see cref="ColumnAssertion.ToleranceSeconds"/>.</description></item>
/// </list>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AssertionOperator
{
    Equals = 0,
    NotEquals,
    Contains,
    NotContains,
    StartsWith,
    EndsWith,
    Regex,
    GreaterThan,
    LessThan,
    Between,
    IsNull,
    IsNotNull,
    EqualsNumeric,
    EqualsDate,
}
