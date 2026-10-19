using System.Diagnostics.CodeAnalysis;

namespace RPM.Fingerprint.Net;

/// <summary>
/// The result of calling another service: its answer, or the fact that it did not answer.
///
/// <para><b>An answer can be "nothing".</b> No such record, no article, no artist with that id. That
/// is a real result: it may be cached, and it may be shown as "nothing was found".</para>
///
/// <para><b>No answer is never "nothing".</b> No network, a refused request, a server error, a
/// timeout, a body that could not be read. It is never cached, and it is shown as a failure.</para>
///
/// <para>A type, not a null or an empty list, because both of those already mean "nothing". A
/// failure returned as null would be cached as "this record has no information" and shown as "No
/// information was found for this album". Use <see cref="Match{TResult}"/> to read one: it takes
/// both cases, so no caller can read the answer without handling a failure.</para>
/// </summary>
public abstract record Asked<T>
{
    private Asked() { }

    /// <summary>The service answered. <see cref="Value"/> may itself be empty or null, meaning the
    /// service knows of nothing.</summary>
    public sealed record Answered(T Value) : Asked<T>
    {
        public override TResult Match<TResult>(Func<T, TResult> answered, Func<string, TResult> unanswered)
            => answered(Value);
    }

    /// <summary>The service did not answer. <see cref="Why"/> is for the log, not for the screen.</summary>
    public sealed record Unanswered(string Why) : Asked<T>
    {
        public override TResult Match<TResult>(Func<T, TResult> answered, Func<string, TResult> unanswered)
            => unanswered(Why);
    }

    public abstract TResult Match<TResult>(Func<T, TResult> answered, Func<string, TResult> unanswered);

    /// <summary>The answer, or false and the reason there is none. For code that awaits between one
    /// step and the next, where <see cref="Match{TResult}"/> cannot be used. <paramref name="why"/> is
    /// not optional: a caller that reads the answer also receives the failure.</summary>
    public bool Answers([MaybeNullWhen(false)] out T value, [NotNullWhen(false)] out string? why)
    {
        if (this is Answered answered)
        {
            value = answered.Value;
            why = null;
            return true;
        }

        value = default;
        why = ((Unanswered)this).Why;
        return false;
    }

    /// <summary>The answer passed through <paramref name="map"/>, or the same failure.</summary>
    public Asked<TResult> Then<TResult>(Func<T, TResult> map)
        => Match<Asked<TResult>>(value => new Asked<TResult>.Answered(map(value)),
                                 why => new Asked<TResult>.Unanswered(why));
}

/// <summary>Shorthand for building an <see cref="Asked{T}"/>.</summary>
public static class Asked
{
    public static Asked<T> Answer<T>(T value) => new Asked<T>.Answered(value);

    public static Asked<T> Failed<T>(string why) => new Asked<T>.Unanswered(why);
}
