using System.Globalization;
using System.Text.RegularExpressions;

namespace AiTestCrew.Agents.AseXmlAgent.Templates;

/// <summary>
/// Grammar-level validator for NEM12 interval CSV payloads embedded in aseXML
/// &lt;CSVIntervalData&gt; elements. Catches LLM-authored malformed CSV before
/// the rendered XML is written or delivered.
///
/// Scope is structural only — record-type sequencing, field counts, and a handful
/// of cheap field-level sanity checks. Not a full AEMO semantic validator: does not
/// verify NMI checksums, interval-value sums vs 500 totals, or quality-flag causality.
///
/// See AEMO Metrology Procedure Part A: Meter Data File Format (NEM12/NEM13) for
/// the authoritative grammar. Record types used:
///   100 — header           (5 fields)
///   200 — NMI datastream   (10 fields)
///   300 — interval data    (2 + N + 5, where N = 1440 / IntervalLength)
///   400 — quality override (6 fields)
///   500 — B2B detail       (5 fields)
///   900 — end              (1 field)
/// </summary>
public static class Nem12CsvValidator
{
    private static readonly Regex HeaderTimestampRx = new(@"^\d{12}$", RegexOptions.Compiled);
    private static readonly Regex DateRx = new(@"^\d{8}$", RegexOptions.Compiled);
    private static readonly Regex QualityFlagRx = new(@"^[AEFNSV](\d{2,3})?$", RegexOptions.Compiled);

    private static readonly HashSet<int> AllowedIntervalLengths = [5, 15, 30];

    public static bool Validate(string csv, out string? error)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            error = "NEM12 body is empty.";
            return false;
        }

        var lines = csv.Replace("\r\n", "\n").Split('\n');
        var state = State.ExpectHeader;
        var currentIntervalLength = 0;
        var currentIntervalCount = 0;
        var lineNo = 0;
        var sawAnyDatastream = false;

        foreach (var rawLine in lines)
        {
            lineNo++;
            var line = rawLine.TrimEnd();
            if (line.Length == 0) continue;

            var fields = line.Split(',');
            var recordType = fields[0];

            switch (recordType)
            {
                case "100":
                    if (state != State.ExpectHeader)
                    {
                        error = $"line {lineNo}: unexpected 100 header — NEM12 must begin with exactly one 100 record.";
                        return false;
                    }
                    if (fields.Length != 5)
                    {
                        error = $"line {lineNo}: 100 header has {fields.Length} fields, expected 5 (100,NEM12,YYYYMMDDHHMM,From,To).";
                        return false;
                    }
                    if (!string.Equals(fields[1], "NEM12", StringComparison.Ordinal))
                    {
                        error = $"line {lineNo}: 100 header field 2 is '{fields[1]}', expected 'NEM12'.";
                        return false;
                    }
                    if (!HeaderTimestampRx.IsMatch(fields[2]))
                    {
                        error = $"line {lineNo}: 100 header timestamp '{fields[2]}' must be 12 digits (YYYYMMDDHHMM).";
                        return false;
                    }
                    state = State.ExpectDatastreamOrEnd;
                    break;

                case "200":
                    if (state is not (State.ExpectDatastreamOrEnd or State.AfterInterval or State.AfterQuality or State.AfterDetail))
                    {
                        error = $"line {lineNo}: unexpected 200 datastream record in state {state}.";
                        return false;
                    }
                    if (fields.Length != 10)
                    {
                        error = $"line {lineNo}: 200 record has {fields.Length} fields, expected 10.";
                        return false;
                    }
                    if (!int.TryParse(fields[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out var intervalLength)
                        || !AllowedIntervalLengths.Contains(intervalLength))
                    {
                        error = $"line {lineNo}: 200 record IntervalLength '{fields[8]}' must be one of {{5, 15, 30}}.";
                        return false;
                    }
                    currentIntervalLength = intervalLength;
                    currentIntervalCount = 1440 / intervalLength;
                    state = State.ExpectInterval;
                    sawAnyDatastream = true;
                    break;

                case "300":
                    if (state is not (State.ExpectInterval or State.AfterInterval or State.AfterQuality or State.AfterDetail))
                    {
                        error = $"line {lineNo}: 300 interval record not permitted in state {state} — must follow a 200 record.";
                        return false;
                    }
                    if (currentIntervalLength == 0)
                    {
                        error = $"line {lineNo}: 300 interval record before any 200 datastream record.";
                        return false;
                    }
                    var expected300 = 2 + currentIntervalCount + 5;
                    if (fields.Length != expected300)
                    {
                        error = $"line {lineNo}: 300 record has {fields.Length} fields, expected {expected300} "
                              + $"(2 + {currentIntervalCount} values + 5 trailing, for IntervalLength={currentIntervalLength}).";
                        return false;
                    }
                    if (!DateRx.IsMatch(fields[1]))
                    {
                        error = $"line {lineNo}: 300 record date '{fields[1]}' must be 8 digits (YYYYMMDD).";
                        return false;
                    }
                    for (var i = 2; i < 2 + currentIntervalCount; i++)
                    {
                        if (fields[i].Length == 0) continue;
                        if (!double.TryParse(fields[i], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                        {
                            error = $"line {lineNo}: 300 record interval value at position {i - 1} is '{fields[i]}', expected numeric or blank.";
                            return false;
                        }
                    }
                    var quality300 = fields[2 + currentIntervalCount];
                    if (!QualityFlagRx.IsMatch(quality300))
                    {
                        error = $"line {lineNo}: 300 record QualityMethod '{quality300}' is not a valid NEM12 flag (A|E|F|N|S|V, optionally with 2-3 digit reason code).";
                        return false;
                    }
                    state = State.AfterInterval;
                    break;

                case "400":
                    if (state is not (State.AfterInterval or State.AfterQuality))
                    {
                        error = $"line {lineNo}: 400 quality override not permitted in state {state} — must follow a 300 or another 400.";
                        return false;
                    }
                    if (fields.Length != 6)
                    {
                        error = $"line {lineNo}: 400 record has {fields.Length} fields, expected 6.";
                        return false;
                    }
                    if (!int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var startInt)
                        || !int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var endInt))
                    {
                        error = $"line {lineNo}: 400 record StartInterval/EndInterval must be integers.";
                        return false;
                    }
                    if (startInt < 1 || endInt < startInt || endInt > currentIntervalCount)
                    {
                        error = $"line {lineNo}: 400 record interval range [{startInt},{endInt}] is invalid "
                              + $"(must satisfy 1 ≤ start ≤ end ≤ {currentIntervalCount}).";
                        return false;
                    }
                    if (!QualityFlagRx.IsMatch(fields[3]))
                    {
                        error = $"line {lineNo}: 400 record QualityMethod '{fields[3]}' is not a valid NEM12 flag.";
                        return false;
                    }
                    state = State.AfterQuality;
                    break;

                case "500":
                    if (state is not (State.AfterInterval or State.AfterQuality))
                    {
                        error = $"line {lineNo}: 500 B2B detail not permitted in state {state} — must follow 300 or 400.";
                        return false;
                    }
                    if (fields.Length != 5)
                    {
                        error = $"line {lineNo}: 500 record has {fields.Length} fields, expected 5.";
                        return false;
                    }
                    state = State.AfterDetail;
                    break;

                case "900":
                    if (state is State.ExpectHeader)
                    {
                        error = $"line {lineNo}: 900 end record before 100 header.";
                        return false;
                    }
                    if (fields.Length != 1)
                    {
                        error = $"line {lineNo}: 900 end record has {fields.Length} fields, expected 1.";
                        return false;
                    }
                    if (!sawAnyDatastream)
                    {
                        error = $"line {lineNo}: 900 end record with zero 200 datastream records — NEM12 body must contain at least one datastream.";
                        return false;
                    }
                    state = State.End;
                    break;

                default:
                    error = $"line {lineNo}: unknown NEM12 record type '{recordType}' (expected 100/200/300/400/500/900).";
                    return false;
            }

            if (state == State.End) break;
        }

        if (state != State.End)
        {
            error = $"NEM12 body is not terminated by a 900 record (final state was {state}).";
            return false;
        }

        error = null;
        return true;
    }

    private enum State
    {
        ExpectHeader,
        ExpectDatastreamOrEnd,
        ExpectInterval,
        AfterInterval,
        AfterQuality,
        AfterDetail,
        End
    }
}
