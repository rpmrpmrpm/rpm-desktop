using System.Globalization;

namespace RPM.Desktop.Services.Platform;

/// <summary>
/// SVG path data as steps with absolute points, for code that hands a shape to a native API: the macOS
/// splash cuts its window to the wordmark (see <see cref="MacWindowCard.CutToShape"/>). XAML reads the
/// same data for a Path, but the Geometry it makes does not give the points back.
///
/// <para>Only the commands rpm's own shapes use are read: absolute M, L, H, V, C and Z. Data with any
/// other command is refused, so a shape this cannot read is not drawn wrongly.</para>
/// </summary>
internal static class PathData
{
    internal enum Step { Move, Line, Curve, Close }

    /// <summary>
    /// Reads the data into steps: one point for a move or a line, three for a curve (two control points,
    /// then the end), none for a close.
    /// </summary>
    /// <returns>Null when the data has a command other than absolute M, L, H, V, C and Z, or a number
    /// count that does not fit its command.</returns>
    public static List<(Step Step, (double X, double Y)[] Points)>? Read(string data)
    {
        var steps = new List<(Step, (double X, double Y)[])>();
        var numbers = new List<double>();
        var command = '\0';
        (double X, double Y) at = (0, 0), start = (0, 0);

        // The numbers collected after one command, as that command's steps. Repeated groups follow the
        // SVG rule: after the first pair of an M, each pair is a line.
        bool Apply()
        {
            var n = numbers;
            switch (command)
            {
                case '\0':
                    return n.Count == 0;
                case 'Z':
                    if (n.Count != 0)
                        return false;
                    steps.Add((Step.Close, []));
                    at = start;
                    return true;
                case 'M' or 'L':
                    if (n.Count == 0 || n.Count % 2 != 0)
                        return false;
                    for (var i = 0; i < n.Count; i += 2)
                    {
                        at = (n[i], n[i + 1]);
                        var move = command == 'M' && i == 0;
                        if (move)
                            start = at;
                        steps.Add((move ? Step.Move : Step.Line, [at]));
                    }
                    return true;
                case 'H' or 'V':
                    if (n.Count == 0)
                        return false;
                    foreach (var v in n)
                    {
                        at = command == 'H' ? (v, at.Y) : (at.X, v);
                        steps.Add((Step.Line, [at]));
                    }
                    return true;
                case 'C':
                    if (n.Count == 0 || n.Count % 6 != 0)
                        return false;
                    for (var i = 0; i < n.Count; i += 6)
                    {
                        at = (n[i + 4], n[i + 5]);
                        steps.Add((Step.Curve, [(n[i], n[i + 1]), (n[i + 2], n[i + 3]), at]));
                    }
                    return true;
                default:
                    return false;
            }
        }

        var p = 0;
        while (p < data.Length)
        {
            var c = data[p];
            if (char.IsWhiteSpace(c) || c == ',')
            {
                p++;
                continue;
            }

            if (char.IsAsciiLetter(c))
            {
                if (!Apply())
                    return null;
                command = c;
                numbers.Clear();
                p++;
                continue;
            }

            // A number: a sign, digits with at most one point, and an exponent. A sign also ends the
            // number before it, as in "85 -143" written "85-143".
            var from = p;
            if (c is '+' or '-')
                p++;
            var point = false;
            while (p < data.Length && (char.IsAsciiDigit(data[p]) || (data[p] == '.' && !point)))
            {
                point |= data[p] == '.';
                p++;
            }
            if (p < data.Length && data[p] is 'e' or 'E')
            {
                p++;
                if (p < data.Length && data[p] is '+' or '-')
                    p++;
                while (p < data.Length && char.IsAsciiDigit(data[p]))
                    p++;
            }

            if (!double.TryParse(data.AsSpan(from, p - from), NumberStyles.Float, CultureInfo.InvariantCulture,
                                 out var number))
                return null;
            numbers.Add(number);
        }

        return Apply() ? steps : null;
    }

    /// <summary>
    /// The steps stretched uniformly into <paramref name="into"/> and centred there, as a Path with
    /// <c>Stretch="Uniform"</c> places its geometry. Top-left origin, the same units as
    /// <paramref name="into"/>.
    /// </summary>
    /// <remarks>
    /// The bounds are those of every point, the control points included. That is the bounds a Path
    /// stretches when no control point lies outside the curve's own extent, as in the wordmark, whose
    /// outermost points are all the ends of lines.
    /// </remarks>
    /// <returns>Null for steps with no area, or a rectangle with none.</returns>
    public static List<(Step Step, (double X, double Y)[] Points)>? Fit(
        IReadOnlyList<(Step Step, (double X, double Y)[] Points)> steps, Windows.Foundation.Rect into)
    {
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var (_, points) in steps)
        {
            foreach (var (x, y) in points)
            {
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }

        if (right <= left || bottom <= top || into.Width <= 0 || into.Height <= 0)
            return null;

        var scale = Math.Min(into.Width / (right - left), into.Height / (bottom - top));
        var dx = into.X + (into.Width - (right - left) * scale) / 2 - left * scale;
        var dy = into.Y + (into.Height - (bottom - top) * scale) / 2 - top * scale;

        var fitted = new List<(Step, (double X, double Y)[])>(steps.Count);
        foreach (var (step, points) in steps)
            fitted.Add((step, Array.ConvertAll(points, q => (q.X * scale + dx, q.Y * scale + dy))));
        return fitted;
    }
}
