using TouchDeck.Core.Session;

namespace TouchDeck.Core.Updates;

/// <summary>
/// Whether a board that just connected deserves a fresh update check. The daily check chooses
/// firmware only for the board connected at that moment, so a board plugged in later would
/// otherwise wait up to a day for its offer.
/// </summary>
public static class UpdateRecheck
{
    /// <param name="checkedFor">The board the last check chose firmware for, or null if none was connected.</param>
    /// <param name="connected">The board that just connected.</param>
    public static bool OnConnect(FirmwareInfo? checkedFor, FirmwareInfo connected)
    {
        // A replug of the same board keeps its offer; a new model or version (after an install) is checked.
        return checkedFor is null || checkedFor.Board != connected.Board || checkedFor.Version != connected.Version;
    }
}
