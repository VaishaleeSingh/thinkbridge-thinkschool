using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace QueryTranslation.Demo;

/// <summary>
/// Collects the SQL EF Core actually generates, so this demo can print two
/// queries' SQL side by side instead of asking the reader to scroll back
/// through interleaved console output and match statements to queries by eye.
///
/// This is the SAME mechanism the exercise asks for -- DbContextOptionsBuilder
/// .LogTo(...) with EnableSensitiveDataLogging() -- just pointed at a list
/// instead of at Console.WriteLine.
/// </summary>
public sealed class SqlCapture
{
    private readonly List<string> _messages = new();

    public void Add(string message) => _messages.Add(message);

    public void Clear() => _messages.Clear();

    /// <summary>
    /// Every log message captured since the last Clear(), verbatim -- including
    /// the "Executed DbCommand (...) [Parameters=[...]]" preamble. Part 1 needs
    /// this: the preamble is precisely where EnableSensitiveDataLogging shows
    /// its effect, so stripping it (as <see cref="Statements"/> does) would
    /// throw away the thing being demonstrated.
    /// </summary>
    public string LastFullMessage() =>
        _messages.Count == 0 ? "(nothing was logged)" : _messages[^1];

    /// <summary>
    /// Just the statement text of each command, with the log preamble stripped,
    /// for the readable side-by-side comparisons in Parts 2 and 3.
    /// </summary>
    public IReadOnlyList<string> Statements()
    {
        var result = new List<string>();

        foreach (var message in _messages)
        {
            var index = message.IndexOf("SELECT", StringComparison.Ordinal);
            if (index < 0)
                continue;

            result.Add(Dedent(message[index..].Trim()));
        }

        return result;
    }

    /// <summary>
    /// Removes the indentation the continuation lines all share.
    ///
    /// This is needed because slicing a log message from its "SELECT" onward
    /// starts line 1 mid-line (so it loses its leading whitespace) while lines
    /// 2+ keep the six spaces EF indents a statement's FROM/WHERE by. Left
    /// alone, adding a display prefix on top of that pushes FROM further right
    /// than SELECT and makes a flat query look nested.
    ///
    /// Deliberately done HERE rather than in the display helper: the same
    /// dedent applied to a full, unsliced log message would strip the six
    /// spaces that legitimately nest a record's continuation lines under its
    /// "info:" header, which is exactly what Part 1 needs to keep. Only the
    /// sliced view is missing its first line's indent, so only the sliced view
    /// should be dedented.
    ///
    /// Strips the COMMON indent only, so a genuinely nested subquery keeps its
    /// relative shape.
    /// </summary>
    private static string Dedent(string text)
    {
        var lines = text.Split('\n').Select(line => line.TrimEnd('\r', ' ')).ToArray();
        var continuation = lines.Skip(1).Where(line => line.Length > 0).ToArray();

        if (continuation.Length == 0)
            return string.Join('\n', lines);

        var common = continuation.Min(line => line.Length - line.TrimStart(' ').Length);

        return string.Join('\n', lines.Select((line, i) =>
            i == 0 || line.Length == 0 ? line : line[Math.Min(common, line.Length)..]));
    }

    public string SingleStatement()
    {
        var statements = Statements();

        if (statements.Count == 0)
            return "(no SELECT was executed)";

        // A query that materializes in one round trip produces one statement; if
        // EF ever split it, showing all of them is more honest than showing the
        // first and implying it was the only one.
        return statements.Count == 1
            ? statements[0]
            : string.Join(Environment.NewLine + "-- and then --" + Environment.NewLine, statements);
    }

    /// <summary>
    /// Builds options wired to this capture. LogLevel.Information is the level EF
    /// logs executed commands at; filtering to DbLoggerCategory.Database.Command
    /// keeps out EF's startup and model-building chatter, which would otherwise
    /// bury the SQL.
    /// </summary>
    public DbContextOptions<DemoDbContext> Options(string connectionString, bool alsoToConsole = false)
    {
        return new DbContextOptionsBuilder<DemoDbContext>()
            .UseSqlite(connectionString)
            .LogTo(
                message =>
                {
                    Add(message);
                    if (alsoToConsole)
                        Console.WriteLine(message);
                },
                new[] { DbLoggerCategory.Database.Command.Name },
                LogLevel.Information)
            // Without this, parameter values are logged as '?' instead of their
            // actual value. Turning it on is what makes a logged statement
            // reproducible by hand -- and is also exactly why it must stay in
            // development only: it writes real parameter values (which in a real
            // app means real user data) into the log sink.
            .EnableSensitiveDataLogging()
            .Options;
    }
}
