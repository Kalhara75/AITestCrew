using AiTestCrew.Agents.DbAgent;
using FluentAssertions;
using Xunit;

namespace AiTestCrew.Agents.Tests.DbAgent;

public class DbCheckSqlGuardrailsTests
{
    [Theory]
    [InlineData("SELECT * FROM Jobs WHERE MessageID = 'X'")]
    [InlineData("WITH cte AS (SELECT 1 AS x) SELECT * FROM cte")]
    [InlineData("SELECT COUNT(*) FROM Logs")]
    public void Allows_read_only_select(string sql)
    {
        var (ok, reason) = DbCheckSqlGuardrails.Validate(sql);
        ok.Should().BeTrue($"reason: {reason}");
    }

    [Theory]
    [InlineData("SELECT * FROM x; DROP TABLE y", "';'")]
    [InlineData("SELECT 1; SELECT 2", "';'")]
    public void Rejects_semicolons(string sql, string expectedFragment)
    {
        var (ok, reason) = DbCheckSqlGuardrails.Validate(sql);
        ok.Should().BeFalse();
        reason.Should().Contain(expectedFragment);
    }

    [Theory]
    [InlineData("INSERT INTO Jobs (Id) VALUES (1)")]
    [InlineData("UPDATE Jobs SET Status = 'X'")]
    [InlineData("DELETE FROM Jobs")]
    [InlineData("MERGE INTO Jobs USING src ON 1=1 WHEN MATCHED THEN UPDATE SET x=1")]
    [InlineData("DROP TABLE Jobs")]
    [InlineData("ALTER TABLE Jobs ADD x int")]
    [InlineData("CREATE TABLE Jobs (id int)")]
    [InlineData("EXEC sp_who")]
    [InlineData("EXECUTE sp_who")]
    [InlineData("TRUNCATE TABLE Jobs")]
    [InlineData("SELECT * INTO NewJobs FROM Jobs")]
    public void Rejects_write_or_ddl_statements(string sql)
    {
        var (ok, _) = DbCheckSqlGuardrails.Validate(sql);
        ok.Should().BeFalse();
    }

    [Fact]
    public void Rejects_write_keyword_smuggled_inside_cte()
    {
        // Contrived but plausible — denied keywords are scanned across the
        // full cleaned SQL, not just the leading verb.
        var sql = "WITH x AS (SELECT 1) INSERT INTO y SELECT * FROM x";
        var (ok, reason) = DbCheckSqlGuardrails.Validate(sql);
        ok.Should().BeFalse();
        reason.Should().Contain("INSERT");
    }

    [Fact]
    public void Rejects_empty_sql()
    {
        var (ok, _) = DbCheckSqlGuardrails.Validate("");
        ok.Should().BeFalse();
    }

    [Fact]
    public void Rejects_non_select_leading_verb()
    {
        var (ok, _) = DbCheckSqlGuardrails.Validate("SHUTDOWN");
        ok.Should().BeFalse();
    }

    [Fact]
    public void Comments_are_stripped_before_validation()
    {
        // A SELECT prefixed by a comment that contains a denied keyword should still pass —
        // the validator strips comments first.
        var sql = "-- DROP this comment\nSELECT 1";
        var (ok, _) = DbCheckSqlGuardrails.Validate(sql);
        ok.Should().BeTrue();
    }
}
