namespace AIVTuber.Core.Ui;

/// <summary>
/// Distinguishes an application-initiated return-to-latest scroll from a user scroll.
/// </summary>
public sealed class FollowLatestScrollPolicy
{
    private bool _programmaticScrollPending;

    public void BeginProgrammaticScroll() => _programmaticScrollPending = true;

    public void CompleteProgrammaticScroll() => _programmaticScrollPending = false;

    public bool ShouldPauseFollowing(double verticalChange)
    {
        if (verticalChange == 0 || _programmaticScrollPending)
            return false;

        return true;
    }
}
