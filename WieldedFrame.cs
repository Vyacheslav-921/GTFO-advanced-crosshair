using System;
using System.Collections.Generic;
using FirstPersonItem;
using Gear;
using Il2CppInterop.Runtime.InteropTypes;
using Player;
using UnityEngine;

namespace V921;

/// <summary>
/// Local player, wielded item, and item kind for this frame.
/// The overlays used to each walk the player again, and a gun was cast to every
/// item type on every update. Kind stays until the wielded item changes.
/// </summary>
internal static class WieldedFrame
{
    private static int _frame = -1;
    private static bool _hasPlayer;
    private static PlayerAgent? _player;
    private static ItemEquippable? _item;

    private static IntPtr _kindPtr;
    private static MeleeWeaponFirstPerson? _melee;
    private static Shotgun? _shotgun;
    private static BulletWeapon? _bullet;
    private static bool _isMelee;
    private static bool _isShotgun;
    private static bool _isBullet;

    private static IntPtr _slotPtr;
    private static bool _hasSlot;
    private static InventorySlot _slot;

    public static bool TryGetPlayer(out PlayerAgent? player)
    {
        Refresh();
        player = _player;
        return _hasPlayer && Alive(_player);
    }

    public static bool TryGetItem(out ItemEquippable? item)
    {
        Refresh();
        item = _item;
        return Alive(_item);
    }

    public static bool TryGetSlot(out InventorySlot slot)
    {
        slot = InventorySlot.None;
        if (!TryGetItem(out var item) || !Alive(item))
            return false;

        var ptr = item!.Pointer;
        if (ptr != _slotPtr)
        {
            _slotPtr = ptr;
            var block = item.ItemDataBlock;
            if (!Alive(block))
            {
                _hasSlot = false;
                _slot = InventorySlot.None;
            }
            else
            {
                _hasSlot = true;
                _slot = block!.inventorySlot;
            }
        }

        if (!_hasSlot)
            return false;

        slot = _slot;
        return true;
    }

    public static bool TryGetMelee(out MeleeWeaponFirstPerson? melee)
    {
        melee = null;
        if (!TryGetItem(out var item) || !Alive(item))
            return false;

        EnsureKind(item!);
        if (!_isMelee || !Alive(_melee))
            return false;

        melee = _melee;
        return true;
    }

    public static bool IsShotgun()
    {
        if (!TryGetItem(out var item) || !Alive(item))
            return false;

        EnsureKind(item!);
        return _isShotgun && Alive(_shotgun);
    }

    public static bool TryGetBulletWeapon(out BulletWeapon? gun)
    {
        gun = null;
        if (!TryGetItem(out var item) || !Alive(item))
            return false;

        EnsureKind(item!);
        if (!_isBullet || !Alive(_bullet))
            return false;

        gun = _bullet;
        return true;
    }

    private static void Refresh()
    {
        var frame = Time.frameCount;
        if (frame == _frame)
            return;

        _player = null;
        _item = null;
        _hasPlayer = false;

        if (!PlayerManager.TryGetLocalPlayerAgent(out var player) || !Alive(player))
        {
            _frame = frame;
            return;
        }

        _player = player;
        _hasPlayer = true;
        var holder = player!.FPItemHolder;
        if (!Alive(holder))
        {
            _frame = frame;
            return;
        }

        var item = holder!.WieldedItem;
        if (Alive(item))
            _item = item;

        _frame = frame;
    }

    private static void EnsureKind(ItemEquippable item)
    {
        var ptr = item.Pointer;
        if (ptr == IntPtr.Zero)
            return;

        if (ptr == _kindPtr && KindWrappersAlive())
            return;

        var melee = item.TryCast<MeleeWeaponFirstPerson>();
        var shotgun = item.TryCast<Shotgun>();
        var bullet = item.TryCast<BulletWeapon>();
        _kindPtr = ptr;
        _melee = melee;
        _shotgun = shotgun;
        _bullet = bullet;
        _isMelee = Alive(_melee);
        _isShotgun = Alive(_shotgun);
        _isBullet = Alive(_bullet);
    }

    private static bool KindWrappersAlive()
    {
        if (_isMelee && !Alive(_melee))
            return false;
        if (_isShotgun && !Alive(_shotgun))
            return false;
        if (_isBullet && !Alive(_bullet))
            return false;
        return true;
    }

    private static bool Alive(Il2CppObjectBase? obj)
    {
        return obj != null && obj.Pointer != IntPtr.Zero;
    }
}

/// <summary>
/// Parsed RRGGBB colors. Config strings repeat every update; the parse does not need to.
/// </summary>
internal static class HudColor
{
    private static readonly Dictionary<string, Color> Cache = new(StringComparer.Ordinal);

    public static Color Parse(string? raw, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;

        if (Cache.TryGetValue(raw, out var cached))
            return cached;

        var text = raw.Trim();
        if (text.Length == 0)
            return fallback;

        if (text[0] != '#')
            text = "#" + text;

        if (!ColorUtility.TryParseHtmlString(text, out var parsed))
            return fallback;

        Cache[raw] = parsed;
        return parsed;
    }
}
