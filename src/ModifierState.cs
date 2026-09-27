namespace TurboToggle;
sealed class ModifierState
{
    static readonly uint[] SlotBit =
    [
        Hotkeys.ModShift, Hotkeys.ModShift, Hotkeys.ModShift,
        Hotkeys.ModControl, Hotkeys.ModControl, Hotkeys.ModControl,
        Hotkeys.ModAlt, Hotkeys.ModAlt, Hotkeys.ModAlt,
        Hotkeys.ModWin, Hotkeys.ModWin,
    ];
    readonly int[] _pressed = new int[SlotBit.Length];
    readonly object _pressedLock = new();
    public uint Mods
    {
        get
        {
            lock (_pressedLock)
            {
                uint mods = 0;
                for (int i = 0; i < _pressed.Length; i++)
                {
                    if (_pressed[i] > 0)
                        mods |= SlotBit[i];
                }
                return mods;
            }
        }
    }
    public static uint Bit(uint vk)
    {
        int slot = SlotOf(vk);
        return slot < 0 ? 0 : SlotBit[slot];
    }
    static int SlotOf(uint vk) => vk switch
    {
        0x10 => 0, 0xA0 => 1, 0xA1 => 2,
        0x11 => 3, 0xA2 => 4, 0xA3 => 5,
        0x12 => 6, 0xA4 => 7, 0xA5 => 8,
        0x5B => 9, 0x5C => 10,
        _ => -1,
    };
    public bool Update(uint vk, bool down)
    {
        int slot = SlotOf(vk);
        if (slot < 0)
            return false;
        lock (_pressedLock)
        {
            _pressed[slot] = down ? 1 : 0;
            if (GenericOf(slot) is { } generic)
                _pressed[generic] = down ? 1 : (_pressed[generic + 1] | _pressed[generic + 2]);
        }
        return true;
    }
    static int? GenericOf(int slot) => slot switch
    {
        1 or 2 => 0,
        4 or 5 => 3,
        7 or 8 => 6,
        _ => null,
    };
}
