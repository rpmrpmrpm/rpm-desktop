using RPM.Core.Library;
using RPM.Desktop.Presentation.Dialogs;
using Microsoft.UI.Xaml.Controls;

namespace RPM.Desktop.Tests;

/// <summary>
/// The question asked before songs go on a playlist that already holds some of them, and what each
/// button means for the write.
/// </summary>
public class AlreadyInPlaylistPromptTests
{
    [Theory]
    [InlineData(0, 1, AlreadyInPlaylistQuestion.None)]
    [InlineData(0, 12, AlreadyInPlaylistQuestion.None)]
    [InlineData(1, 1, AlreadyInPlaylistQuestion.Track)]
    [InlineData(12, 12, AlreadyInPlaylistQuestion.AllTracks)]
    [InlineData(1, 12, AlreadyInPlaylistQuestion.SomeTracks)]
    [InlineData(11, 12, AlreadyInPlaylistQuestion.SomeTracks)]
    public void The_question_follows_how_many_are_already_there(int held, int total, AlreadyInPlaylistQuestion expected)
        => Assert.Equal(expected, AlreadyInPlaylistPrompt.Question(held, total));

    /// <summary>No songs already there, so no question: the add goes ahead, and nothing it writes can
    /// be a second copy.</summary>
    [Fact]
    public void With_nothing_there_the_add_goes_ahead_without_copies()
        => Assert.Equal(AlreadyInPlaylist.LeaveIt,
            AlreadyInPlaylistPrompt.Answer(AlreadyInPlaylistQuestion.None, ContentDialogResult.None));

    [Theory]
    [InlineData(AlreadyInPlaylistQuestion.Track)]
    [InlineData(AlreadyInPlaylistQuestion.AllTracks)]
    public void Add_again_adds_a_second_copy_and_anything_else_adds_nothing(AlreadyInPlaylistQuestion question)
    {
        Assert.Equal(AlreadyInPlaylist.AddAnother,
            AlreadyInPlaylistPrompt.Answer(question, ContentDialogResult.Primary));
        Assert.Null(AlreadyInPlaylistPrompt.Answer(question, ContentDialogResult.None));
    }

    [Fact]
    public void With_some_there_the_first_button_adds_only_the_new_ones_and_the_second_adds_all()
    {
        Assert.Equal(AlreadyInPlaylist.LeaveIt,
            AlreadyInPlaylistPrompt.Answer(AlreadyInPlaylistQuestion.SomeTracks, ContentDialogResult.Primary));
        Assert.Equal(AlreadyInPlaylist.AddAnother,
            AlreadyInPlaylistPrompt.Answer(AlreadyInPlaylistQuestion.SomeTracks, ContentDialogResult.Secondary));
        Assert.Null(AlreadyInPlaylistPrompt.Answer(AlreadyInPlaylistQuestion.SomeTracks, ContentDialogResult.None));
    }
}
