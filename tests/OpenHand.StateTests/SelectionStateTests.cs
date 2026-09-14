using OpenHand.Common;

internal static class SelectionStateTests
{
    internal static void Run()
    {
        OpenHandSelectionState initial = OpenHandSelectionState.Unselected(9);
        TestHarness.Equal(false, initial.IsSelected, "initial selection");
        TestHarness.Equal(9, initial.RememberedHotbarSlot, "initial slot");

        OpenHandSelectionState selected = initial.Select(9, 1);
        TestHarness.Equal(true, selected.IsSelected, "direct selection");
        TestHarness.Equal(9, selected.RememberedHotbarSlot, "selected slot");
        TestHarness.Equal(1, selected.Revision, "selected revision");

        OpenHandSelectionState exitedForward = selected.Deselect(0, 2);
        TestHarness.Equal(false, exitedForward.IsSelected, "forward wheel exit");
        TestHarness.Equal(0, exitedForward.RememberedHotbarSlot, "forward wheel destination");

        OpenHandSelectionState exitedBackward = selected.Deselect(9, 2);
        TestHarness.Equal(9, exitedBackward.RememberedHotbarSlot, "backward wheel destination");
        TestHarness.Equal(9, OpenHandSelectionState.NormalizePhysicalSlot(99), "invalid upper slot");
        TestHarness.Equal(9, OpenHandSelectionState.NormalizePhysicalSlot(-1), "invalid lower slot");

        // Offhand state: an independent toggle state with its own revision counter;
        // it never reads or mutates the real offhand slot.
        OpenHandOffhandState offhandInitial = default;
        TestHarness.Equal(false, offhandInitial.IsEmpty, "offhand substitution starts off");
        TestHarness.Equal(0, offhandInitial.Revision, "offhand initial revision");
        OpenHandOffhandState offhandEmpty = offhandInitial with { IsEmpty = true, Revision = 1 };
        TestHarness.Equal(true, offhandEmpty.IsEmpty, "offhand substitution activates");
        TestHarness.Equal(1, offhandEmpty.Revision, "offhand activation revision");
        OpenHandOffhandState offhandRestored = offhandEmpty with { IsEmpty = false, Revision = 2 };
        TestHarness.Equal(false, offhandRestored.IsEmpty, "offhand substitution drops");
        TestHarness.Equal(2, offhandRestored.Revision, "offhand drop revision");
    }
}
