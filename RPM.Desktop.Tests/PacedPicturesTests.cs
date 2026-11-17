using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RPM.Desktop.Services.Art;
using Xunit;

namespace RPM.Desktop.Tests;

/// <summary>
/// The gate every picture on a genre's page is decoded through. It is tested for the two things it
/// exists for: one decode at a time, and decodes in the order the cards asked for them.
///
/// <para>Both matter, and only together. A page makes its six shelves in one go. Without the gate, a
/// dozen decodes run inside a single frame, and the layout that was meant to run in that frame does
/// not. The transition then stops part of the way through. Cards are made top-down, so the order decides
/// which shelf gets its pictures first. A gate that let decodes through in the order their fetches
/// finished would fill the decades further down the page before the shelf on screen.</para>
///
/// <para>These tests do not check milliseconds. What the budget does with a frame is
/// <see cref="FrameBudget"/>'s job and is tested in its own tests. Here the tests check that the gate
/// never runs two at once, never reorders, and is not stopped by one picture that cannot be read.</para>
/// </summary>
public class PacedPicturesTests
{
    /// <summary>Two decodes never overlap. Without this, a screenful of decodes is a screenful of work in
    /// one frame.</summary>
    [Fact]
    public async Task RunsOneAtATime()
    {
        var gate = new PacedPictures(FrameBudget.ShareMs);
        var running = 0;
        var most = 0;

        var all = new List<Task<int>>();
        for (var i = 0; i < 8; i++)
            all.Add(gate.Next(async () =>
            {
                var now = ++running;
                most = Math.Max(most, now);
                await Task.Delay(5);
                running--;
                return now;
            }));

        await Task.WhenAll(all);
        Assert.Equal(1, most);
    }

    /// <summary>Decodes finish in the order they were asked for. That is the order the cards are made:
    /// top-down, so the shelf on screen is decoded before the decades under it.</summary>
    [Fact]
    public async Task KeepsTheOrderTheyWereAskedIn()
    {
        var gate = new PacedPictures(FrameBudget.ShareMs);
        var ran = new List<int>();

        var all = new List<Task<int>>();
        for (var i = 0; i < 6; i++)
        {
            var mine = i;
            all.Add(gate.Next(async () =>
            {
                // Deliberately in reverse. A gate that let the quick ones through first would pass the
                // other tests here and still fill the page from the bottom up.
                await Task.Delay(6 - mine);
                ran.Add(mine);
                return mine;
            }));
        }

        await Task.WhenAll(all);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, ran);
    }

    /// <summary>A picture that cannot be read is one blank card, not a blank page. The exception reaches
    /// the caller that asked for it and the gate goes on.</summary>
    [Fact]
    public async Task OneThatThrowsDoesNotStopTheRest()
    {
        var gate = new PacedPictures(FrameBudget.ShareMs);

        var bad = gate.Next<int>(() => throw new InvalidOperationException("no picture"));
        var good = gate.Next(() => Task.FromResult(7));

        await Assert.ThrowsAsync<InvalidOperationException>(() => bad);
        Assert.Equal(7, await good);
    }

    /// <summary>A picture below the fold waits for every picture above it, including one asked for after
    /// it. Pictures join the gate when their bytes are in, not when they were asked for: on the home
    /// screen the record covers below the fold are a file read each, the banner's photographs are a cut,
    /// and in arrival order the covers were all decoded first.</summary>
    [Fact]
    public async Task WhatIsBelowTheFoldWaitsForWhatIsAbove()
    {
        var gate = new PacedPictures(FrameBudget.ShareMs);
        var ran = new List<string>();
        var hold = new TaskCompletionSource();

        // The first holds the gate, so the two after it are both waiting when it lets go.
        var first = gate.Next(async () =>
        {
            await hold.Task;
            ran.Add("first");
            return 0;
        });
        var below = gate.Next(() =>
        {
            ran.Add("below");
            return Task.FromResult(1);
        }, below: true);
        var above = gate.Next(() =>
        {
            ran.Add("above");
            return Task.FromResult(2);
        });

        hold.SetResult();
        await Task.WhenAll(first, below, above);

        Assert.Equal(new[] { "first", "above", "below" }, ran);
        Assert.Equal(1, await below);
    }

    /// <summary>A gate nobody has used yet answers at once. A page with one card on it must not wait out
    /// a frame it never spent.</summary>
    [Fact]
    public async Task TheFirstOneIsNotHeldUp()
    {
        var gate = new PacedPictures(FrameBudget.ShareMs);
        Assert.Equal(3, await gate.Next(() => Task.FromResult(3)));
    }
}
