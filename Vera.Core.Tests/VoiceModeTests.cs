using Vera.Core.Speech;
using Xunit;

namespace Vera.Core.Tests;

public class VoiceModeTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;
    private static DateTimeOffset At(double s) => T0 + TimeSpan.FromSeconds(s);

    [Fact]
    public void ConversationLoopsHandsFree()
    {
        var exit = 10;
        var f = new VoiceModeFlow(() => exit);
        Assert.Equal(VoiceModeAction.StartListening, f.Enter(At(0)));
        f.OnListening();
        f.OnSpeech(At(1), "включи");
        Assert.Equal(VoicePhase.Hearing, f.Phase);
        Assert.Equal("включи", f.Partial);
        f.OnHeard(At(2), "включи музыку");
        Assert.Equal(VoicePhase.Thinking, f.Phase);
        f.OnTurnStarted();
        f.OnAnswerText();
        Assert.Equal(VoicePhase.Answering, f.Phase);
        f.OnSound(true, At(3));
        f.OnTurnEnded(At(3.5));
        Assert.Equal(VoiceModeAction.None, f.Tick(At(4))); // ещё звучит «голосок»
        f.OnSound(false, At(4));
        Assert.Equal(VoicePhase.Waiting, f.Phase);
        Assert.Equal(VoiceModeAction.None, f.Tick(At(4.2)));
        Assert.Equal(VoiceModeAction.StartListening, f.Tick(At(4.5)));
        Assert.Equal(VoiceModeAction.None, f.Tick(At(5))); // один раз
    }

    [Fact]
    public void LongSilenceExitsUnlessNever()
    {
        var exit = 10;
        var f = new VoiceModeFlow(() => exit);
        f.Enter(At(0));
        Assert.Equal(VoiceModeAction.None, f.OnNothingHeard(At(6)));
        Assert.Equal(VoiceModeAction.StartListening, f.Tick(At(7)));
        Assert.Equal(VoiceModeAction.Exit, f.OnNothingHeard(At(10.5)));
        exit = 0;
        Assert.Equal(VoiceModeAction.None, f.OnNothingHeard(At(500)));
    }

    [Fact]
    public void SpeechResetsSilenceTimer()
    {
        var f = new VoiceModeFlow(() => 10);
        f.Enter(At(0));
        f.OnSpeech(At(8));
        Assert.Equal(VoiceModeAction.None, f.OnNothingHeard(At(15)));
        Assert.Equal(VoiceModeAction.Exit, f.OnNothingHeard(At(18.1)));
    }

    [Fact]
    public void InactiveIgnoresEvents()
    {
        var f = new VoiceModeFlow(() => 10);
        f.OnTurnEnded(At(1));
        Assert.Equal(VoiceModeAction.None, f.Tick(At(5)));
        f.Enter(At(0));
        f.Exit();
        f.OnTurnEnded(At(1));
        Assert.Equal(VoiceModeAction.None, f.Tick(At(5)));
        Assert.False(f.Active);
    }

    [Theory]
    [InlineData("full", false, 600, 300)]
    [InlineData("fast", false, 200, 100)]
    [InlineData("off", false, 0, 0)]
    [InlineData("full", true, 0, 0)]
    public void TransitionDurations(string anim, bool reduced, int enter, int exit)
    {
        Assert.Equal(enter, VoiceModeFlow.EnterMs(anim, reduced));
        Assert.Equal(exit, VoiceModeFlow.ExitMs(anim, reduced));
    }
}
