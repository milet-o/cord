namespace Cord.Protocol.Input;

/// <summary>Turns state changes into feedback history events.</summary>
public static class HistoryRecorder
{
    private static readonly ControllerButtons[] AllButtons = Enum.GetValues<ControllerButtons>()
        .Where(b => b != ControllerButtons.None)
        .ToArray();

    /// <summary>Releases first, then touch changes, then presses, then analog triggers.</summary>
    public static List<HistoryEvent> Diff(in ControllerState previous, in ControllerState current)
    {
        var events = new List<HistoryEvent>();

        foreach (var button in AllButtons)
        {
            if (previous.Buttons.HasFlag(button) && !current.Buttons.HasFlag(button))
                events.Add(FeedbackEncoding.ButtonEvent(button, pressed: false));
        }

        AddTouch(events, previous.Touch0, current.Touch0);
        AddTouch(events, previous.Touch1, current.Touch1);

        foreach (var button in AllButtons)
        {
            if (!previous.Buttons.HasFlag(button) && current.Buttons.HasFlag(button))
                events.Add(FeedbackEncoding.ButtonEvent(button, pressed: true));
        }

        if (previous.L2 != current.L2)
            events.Add(FeedbackEncoding.TriggerEvent(right: false, current.L2));
        if (previous.R2 != current.R2)
            events.Add(FeedbackEncoding.TriggerEvent(right: true, current.R2));

        return events;
    }

    private static void AddTouch(List<HistoryEvent> events, TouchPoint previous, TouchPoint current)
    {
        if (previous.Id >= 0 && previous.Id != current.Id)
            events.Add(FeedbackEncoding.TouchEvent(down: false, (byte)previous.Id, previous.X, previous.Y));
        if (current.Id >= 0 && current != previous)
            events.Add(FeedbackEncoding.TouchEvent(down: true, (byte)current.Id, current.X, current.Y));
    }
}
