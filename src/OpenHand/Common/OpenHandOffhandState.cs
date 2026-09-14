namespace OpenHand.Common;

/// <summary>
/// Server-validated per-player state of the empty-offhand toggle. Independent
/// of the main-hand selection: the offhand substitution can be active with or
/// without Open Hand selected, and the real offhand item is never read, moved,
/// or mutated while the substitution is on.
/// </summary>
public readonly record struct OpenHandOffhandState(bool IsEmpty, int Revision);
