namespace Cord.App.Ui;

internal enum SetupStep
{
    Welcome,
    SignIn,
    Pair,
    Quality,
    Done,
}

/// <summary>Order and gating of the first-run setup steps.</summary>
internal sealed class SetupFlow
{
    public SetupStep Step { get; private set; }

    /// <summary>Signing in is required to pair; pairing can be skipped and done later from the home screen.</summary>
    public bool CanContinue(bool signedIn, bool hasPairedConsole) => Step switch
    {
        SetupStep.SignIn => signedIn,
        SetupStep.Pair => hasPairedConsole,
        SetupStep.Done => false,
        _ => true,
    };

    public bool CanSkip => Step == SetupStep.Pair;

    public void Next()
    {
        if (Step != SetupStep.Done)
            Step++;
    }

    public void Back()
    {
        if (Step is not SetupStep.Welcome and not SetupStep.Done)
            Step--;
    }
}
