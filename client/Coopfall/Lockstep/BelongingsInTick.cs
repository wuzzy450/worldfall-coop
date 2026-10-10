using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Coopfall.Lockstep
{
    /// <summary>
    /// What a player carries, wears and drops (Worldfall's bag, gear, ground bundles, crafting).
    /// The bag and spare gear are creature data (DataCalls carries those). What makes or moves
    /// game items, or Worldfall's things on the ground, travels as inputs and runs in a tick:
    /// - putting gear on / taking it off (Gear.PutOn/TakeOff), the character tab's item toggle;
    /// - dropping worn gear, and every bundle set on the ground (Bundles.Drop); picking one up;
    /// - a finished craft (the item is made in the tick, then kept in the bag).
    /// Bundles on the ground age in ticks (they used to fade with each PC's own clock).
    /// </summary>
    public static class BelongingsInTick
    {
        private static Type _piece, _bundle;
        private static MethodInfo _strip, _drop, _nameOf, _parse, _groundAt, _dropSpot, _fromItem, _letGo, _putPiece, _ordinary, _craftRolls, _gearOf;
        private static FieldInfo _list, _age, _pId, _pBy, _pName, _pMods, _pKills;
        private static FieldInfo _queue, _working, _workingFor, _hammers, _rAsset, _rSupply, _rName;
        private static PropertyInfo _modInstance;
        private static readonly ConditionalWeakTable<object, Serial> _serials = new ConditionalWeakTable<object, Serial>();
        private sealed class Serial { public long n; }
        private static long _nextSerial;

        public static void Install(Harmony h, Assembly wf, List<string> features)
        {
            Type mod = wf.GetType("FirstPerson.WorldBoxMod", false), gear = wf.GetType("FirstPerson.Gear", false), bundles = wf.GetType("FirstPerson.Bundles", false), bag = wf.GetType("FirstPerson.Bag", false);
            _modInstance = mod == null ? null : AccessTools.Property(mod, "Instance");
            _piece = gear == null ? null : AccessTools.Inner(gear, "Piece");
            _bundle = bundles == null ? null : AccessTools.Inner(bundles, "Bundle");
            if (mod == null || gear == null || bundles == null || bag == null || _piece == null || _bundle == null) { Log.Warn("lockstep: Worldfall's bag and gear not found: they change one PC's world"); return; }
            _pId = AccessTools.Field(_piece, "Id"); _pBy = AccessTools.Field(_piece, "By"); _pName = AccessTools.Field(_piece, "Name"); _pMods = AccessTools.Field(_piece, "Mods"); _pKills = AccessTools.Field(_piece, "Kills");
            _parse = AccessTools.Method(gear, "Parse", new[] { typeof(string) });
            _nameOf = AccessTools.Method(gear, "NameOf", new[] { _piece });
            _strip = AccessTools.Method(gear, "Strip");
            _fromItem = AccessTools.Method(gear, "FromItem");
            _letGo = AccessTools.Method(gear, "LetGo");
            _gearOf = AccessTools.Method(gear, "Of", new[] { typeof(Actor) });
            _putPiece = AccessTools.Method(bag, "PutPiece");
            _drop = AccessTools.Method(bundles, "Drop");
            _list = AccessTools.Field(bundles, "List");
            _age = AccessTools.Field(_bundle, "Age");
            _groundAt = AccessTools.Method(mod, "GroundAt", new[] { typeof(Vector2) });
            _dropSpot = AccessTools.Method(mod, "DropSpot");
            if (_pId == null || _parse == null || _nameOf == null || _strip == null || _drop == null || _list == null || _age == null || _groundAt == null || _dropSpot == null)
            {
                Log.Warn("lockstep: Worldfall's gear or bundles changed (not found): they change one PC's world");
                return;
            }
            WorldCalls.TextCodec(_piece, PieceText, PieceOf);
            WorldCalls.Codec(_bundle, b => _serials.TryGetValue(b, out Serial s) ? s.n : 0, BundleBySerial);

            // gear on and off
            MethodInfo putOn = AccessTools.Method(gear, "PutOn"), takeOff = AccessTools.Method(gear, "TakeOff");
            WorldCalls.RegisterGuess(h, putOn, (o, a) => a[0] is Actor && a[1] is int i && i >= 0);
            WorldCalls.RegisterGuess(h, takeOff, (o, a) => a[0] is Actor you && a[1] is EquipmentType t && you.equipment?.getSlot(t)?.getItem() != null);
            MethodInfo dropWorn = AccessTools.Method(mod, "DropWorn");
            if (dropWorn != null && WorldCalls.Register(h, AccessTools.Method(typeof(BelongingsInTick), nameof(DropWornAt))))
                h.Patch(dropWorn, prefix: new HarmonyMethod(typeof(BelongingsInTick), nameof(DropWornPrefix)));
            else Log.Warn("lockstep: Worldfall's DropWorn not found: dropping worn gear changes one PC's world");

            // bundles on the ground
            h.Patch(_drop, prefix: new HarmonyMethod(typeof(BelongingsInTick), nameof(DropPrefix)), postfix: new HarmonyMethod(typeof(BelongingsInTick), nameof(DropPostfix)));
            WorldCalls.Register(h, AccessTools.Method(typeof(BelongingsInTick), nameof(DropBundle)));
            MethodInfo pickUp = AccessTools.Method(bundles, "PickUp");
            WorldCalls.RegisterGuess(h, pickUp, (o, a) => a[1] != null);
            MethodInfo age = AccessTools.Method(bundles, "Tick", Type.EmptyTypes);
            if (age != null) h.Patch(age, prefix: new HarmonyMethod(typeof(BelongingsInTick), nameof(NotInLockstepPrefix)));

            // the character tab's item toggle (makes or removes an item)
            Type hud = wf.GetType("FirstPerson.Hud", false);
            MethodInfo toggle = hud == null ? null : AccessTools.Method(hud, "ToggleItem", new[] { typeof(Actor), typeof(EquipmentAsset) });
            if (toggle != null && WorldCalls.Register(h, AccessTools.Method(typeof(BelongingsInTick), nameof(ToggleGear))))
                h.Patch(toggle, prefix: new HarmonyMethod(typeof(BelongingsInTick), nameof(TogglePrefix)));

            // a finished craft
            Type craft = wf.GetType("FirstPerson.Crafting", false), skills = wf.GetType("FirstPerson.Towns.Skills", false) ?? wf.GetType("FirstPerson.Skills", false);
            Type recipe = craft == null ? null : AccessTools.Inner(craft, "Recipe");
            MethodInfo craftUpdate = craft == null ? null : AccessTools.Method(craft, "Update", new[] { typeof(Actor), typeof(float), typeof(Action<string>) });
            _queue = craft == null ? null : AccessTools.Field(craft, "Queue");
            _working = craft == null ? null : AccessTools.Field(craft, "Working");
            _workingFor = craft == null ? null : AccessTools.Field(craft, "WorkingFor");
            _hammers = craft == null ? null : AccessTools.Field(craft, "_hammers");
            _ordinary = craft == null ? null : AccessTools.Method(craft, "Ordinary");
            _craftRolls = skills == null ? null : AccessTools.Method(skills, "CraftRolls");
            _rAsset = recipe == null ? null : AccessTools.Field(recipe, "Asset");
            _rSupply = recipe == null ? null : AccessTools.Field(recipe, "Supply");
            _rName = recipe == null ? null : AccessTools.Field(recipe, "Name");
            if (craftUpdate != null && _queue != null && _workingFor != null && _hammers != null && _working != null && _rAsset != null && _rSupply != null && _fromItem != null && _letGo != null && _putPiece != null
                && WorldCalls.Register(h, AccessTools.Method(typeof(BelongingsInTick), nameof(CraftItem))))
                h.Patch(craftUpdate, prefix: new HarmonyMethod(typeof(BelongingsInTick), nameof(CraftPrefix)));
            else Log.Warn("lockstep: Worldfall's crafting changed (not found): a finished craft is made on one PC only");
            features.Add("bag, gear and bundles");
        }

        private static object Mod() => _modInstance?.GetValue(null, null);
        private static bool Relaying => LockstepControl.Running && LockstepClock.Active && !LockstepClock.InTick;
        private static bool NotInLockstepPrefix() => !LockstepControl.Running || !LockstepClock.Active;

        // ------------------------------------------------------------------ pieces as text

        private static string Clean(string s) => (s ?? "").Replace("|", " ").Replace(",", " ").Replace("+", " ");

        private static string PieceText(object p)
        {
            var mods = (string[])_pMods.GetValue(p) ?? new string[0];
            var m = new string[mods.Length];
            for (int i = 0; i < mods.Length; i++) m[i] = Clean(mods[i]);
            return Clean((string)_pId.GetValue(p)) + "," + string.Join("+", m) + "," + (int)_pKills.GetValue(p) + "," + Clean((string)_pBy.GetValue(p)) + "," + Clean((string)_pName.GetValue(p));
        }

        private static object PieceOf(string s)
        {
            var l = _parse.Invoke(null, new object[] { s }) as IList;
            return l != null && l.Count > 0 ? l[0] : null;
        }

        // ------------------------------------------------------------------ bundles

        private static object BundleBySerial(long n)
        {
            if (!(_list.GetValue(null) is IList l)) return null;
            foreach (object b in l) if (_serials.TryGetValue(b, out Serial s) && s.n == n) return b;
            return null;
        }

        private static float Ground(Vector2 p)
        {
            object mod = Mod();
            try { return mod == null ? 0f : (float)_groundAt.Invoke(mod, new object[] { p }); }
            catch { return 0f; }
        }

        private static bool DropPrefix(Vector2 at, string resource, int count, object piece, ref object __result)
        {
            if (!Relaying) return true;
            __result = null;
            DropBundle(at, resource, count, piece == null ? null : PieceText(piece));   // caught by WorldCalls and sent
            return false;
        }

        private static void DropPostfix(object __result)
        {
            if (__result != null && LockstepClock.InTick && !_serials.TryGetValue(__result, out _)) _serials.Add(__result, new Serial { n = ++_nextSerial });
        }

        /// <summary>Relayed: something set down on the ground (in a tick, on every PC).</summary>
        public static void DropBundle(Vector2 at, string resource, int count, string piece)
        {
            if (!LockstepClock.InTick) return;
            try { _drop.Invoke(null, new object[] { at, (Func<Vector2, float>)Ground, resource, count, piece == null ? null : PieceOf(piece) }); }
            catch (Exception e) { Fail("dropping a bundle", e); }
        }

        /// <summary>Worn gear dropped: where (this PC's view) is worked out here, the rest in a tick.</summary>
        private static bool DropWornPrefix(object __instance, EquipmentType slot, ref bool __result)
        {
            if (!Relaying) return true;
            Actor host = AccessTools.Field(__instance.GetType(), "_host")?.GetValue(__instance) as Actor;
            object[] args = { null };
            if (host == null || !(bool)_dropSpot.Invoke(__instance, args)) { __result = false; return false; }
            DropWornAt(host, slot, (Vector2)args[0]);   // caught by WorldCalls and sent
            __result = true;
            return false;
        }

        /// <summary>Relayed: a player drops what they wear in one slot.</summary>
        public static void DropWornAt(Actor host, EquipmentType slot, Vector2 at)
        {
            if (!LockstepClock.InTick || host == null || !host.isAlive()) return;
            Action<string> toast = PlayerScope.ScopedToast();
            try
            {
                object piece = _strip.Invoke(null, new object[] { host, slot, toast });
                if (piece == null) return;
                _drop.Invoke(null, new object[] { at, (Func<Vector2, float>)Ground, null, 0, piece });
                toast("You drop " + _nameOf.Invoke(null, new[] { piece }));
            }
            catch (Exception e) { Fail("dropping worn gear", e); }
        }

        // ------------------------------------------------------------------ character tab

        private static bool TogglePrefix(Actor host, EquipmentAsset a)
        {
            if (!Relaying) return true;
            if (host != null && a != null) ToggleGear(host, a);   // caught by WorldCalls and sent
            return false;
        }

        /// <summary>Relayed: the character tab puts an item on (made new) or takes it off.</summary>
        public static void ToggleGear(Actor host, EquipmentAsset a)
        {
            if (!LockstepClock.InTick || host == null || a == null || !host.isAlive()) return;
            Action<string> toast = PlayerScope.ScopedToast();
            try
            {
                if (host.equipment == null || !host.understandsHowToUseItems()) { toast("This creature can't use items."); return; }
                ActorEquipmentSlot slot = host.equipment.getSlot(a.equipment_type);
                Item item = slot.getItem();
                if (item != null && item.asset == a) { slot.takeAwayItem(); toast("Took off the " + a.id.Replace('_', ' ') + "."); }
                else
                {
                    Item made = World.world.items.generateItem(a, host.kingdom, host.getName(), 1, host, 0, true);
                    if (made == null) { toast("That won't go on this creature."); return; }
                    if (item != null) slot.takeAwayItem();
                    AccessTools.Method(typeof(ActorEquipmentSlot), "setItem")?.Invoke(slot, new object[] { made, host });
                    toast("You now have " + a.id.Replace('_', ' ') + ".");
                }
                host.setStatsDirty();
            }
            catch (Exception e) { Fail("the character tab's item", e); }
        }

        // ------------------------------------------------------------------ crafting

        /// <summary>Crafting.Update: a finished item craft travels; the rest (hammering sounds, supplies) runs here.</summary>
        private static bool CraftPrefix(object __instance, Actor host, float dt)
        {
            if (!Relaying || host == null) return true;
            if (!(_queue.GetValue(__instance) is IList q) || q.Count == 0) return true;
            object r = q[0];
            if (_rSupply.GetValue(r) != null || !(_rAsset.GetValue(r) is EquipmentAsset asset)) return true;   // supplies go to the bag (data)
            float f = (float)_workingFor.GetValue(__instance);
            if (f + dt < 1.6f) return true;
            q.RemoveAt(0);
            _workingFor.SetValue(__instance, 0f);
            _hammers.SetValue(__instance, 0);
            _working.SetValue(__instance, q.Count > 0 ? q[0] : null);
            CraftItem(host, asset, _rName?.GetValue(r) as string ?? asset.id);   // caught by WorldCalls and sent
            return false;
        }

        /// <summary>Relayed: a crafted item is made and kept in the crafter's bag.</summary>
        public static void CraftItem(Actor host, EquipmentAsset asset, string name)
        {
            if (!LockstepClock.InTick || host == null || asset == null || !host.isAlive()) return;
            Action<string> toast = PlayerScope.ScopedToast();
            try
            {
                int rolls = _craftRolls == null ? 0 : (int)_craftRolls.Invoke(null, new object[] { host });
                Item item = World.world.items.generateItem(asset, host.kingdom, host.getName(), rolls, host, 0, true);
                _ordinary?.Invoke(null, new object[] { item });
                object piece = _fromItem.Invoke(null, new object[] { item });
                _letGo.Invoke(null, new object[] { item });
                if (piece == null) toast("The " + name + " broke while you were making it");
                else if ((bool)_putPiece.Invoke(null, new object[] { host, piece, toast })) toast("Made a " + name + ". It's in your bag (Tab)");
            }
            catch (Exception e) { Fail("crafting", e); toast("The " + name + " broke while you were making it"); }
        }

        // ------------------------------------------------------------------ per tick

        /// <summary>Every tick: bundles on the ground get older; old ones go.</summary>
        public static void RunTick()
        {
            if (!(_list?.GetValue(null) is IList l) || l.Count == 0) return;
            for (int i = l.Count - 1; i >= 0; i--)
            {
                float a = (float)_age.GetValue(l[i]) + LockstepClock.DefaultStep;
                _age.SetValue(l[i], a);
                if (a > 300f) l.RemoveAt(i);
            }
        }

        /// <summary>Bundles carried from the host's last tick into the next epoch (null: none).</summary>
        private static JArray _carried;

        /// <summary>Host, as it saves the world for a new epoch: every bundle on the ground, in list order.</summary>
        public static JArray Save()
        {
            if (!(_list?.GetValue(null) is IList l) || l.Count == 0) return null;
            var a = new JArray();
            Type bt = _bundle;
            foreach (object b in l)
            {
                Vector2 at = (Vector2)AccessTools.Field(bt, "At").GetValue(b);
                object piece = AccessTools.Field(bt, "Piece").GetValue(b);
                a.Add(new JArray(at.x, at.y, (float)AccessTools.Field(bt, "Z").GetValue(b), (float)AccessTools.Field(bt, "Yaw").GetValue(b),
                    (float)_age.GetValue(b), (string)AccessTools.Field(bt, "Resource").GetValue(b), (int)AccessTools.Field(bt, "Count").GetValue(b),
                    piece == null ? null : PieceText(piece)));
            }
            return a;
        }

        public static void Carry(JArray a) => _carried = a;

        public static void Reset()
        {
            _nextSerial = 0;
            if (!(_list?.GetValue(null) is IList l)) return;
            l.Clear();
            // Bundles clears its list when the world changes (map_stats): this world is the new one now
            AccessTools.Field(_list.DeclaringType, "_world")?.SetValue(null, World.world == null ? null : AccessTools.Field(typeof(MapBox), "map_stats").GetValue(World.world));
            if (_carried == null) return;
            int n = 0;
            foreach (JToken t in _carried)
            {
                if (!(t is JArray v) || v.Count < 8) continue;
                try
                {
                    object b = Activator.CreateInstance(_bundle, true);
                    AccessTools.Field(_bundle, "At").SetValue(b, new Vector2((float)v[0], (float)v[1]));
                    AccessTools.Field(_bundle, "Z").SetValue(b, (float)v[2]);
                    AccessTools.Field(_bundle, "Yaw").SetValue(b, (float)v[3]);
                    _age.SetValue(b, (float)v[4]);
                    AccessTools.Field(_bundle, "Resource").SetValue(b, (string)v[5]);
                    AccessTools.Field(_bundle, "Count").SetValue(b, (int)v[6]);
                    string piece = (string)v[7];
                    AccessTools.Field(_bundle, "Piece").SetValue(b, piece == null ? null : PieceOf(piece));
                    l.Add(b);
                    _serials.Add(b, new Serial { n = ++_nextSerial });
                    n++;
                }
                catch (Exception e) { Fail("carrying a bundle", e); }
            }
            if (n > 0) Log.Info("lockstep: " + n + " bundles on the ground carried into this epoch");
        }

        /// <summary>For the test's state line: every bundle (serial, resource/piece, count).</summary>
        public static string Describe()
        {
            if (!(_list?.GetValue(null) is IList l)) return "-";
            var parts = new List<string>();
            foreach (object b in l)
            {
                object piece = AccessTools.Field(_bundle, "Piece").GetValue(b);
                Vector2 at = (Vector2)AccessTools.Field(_bundle, "At").GetValue(b);
                parts.Add((_serials.TryGetValue(b, out Serial s) ? s.n : 0) + ":" + (piece != null ? (string)_pId.GetValue(piece) : (string)AccessTools.Field(_bundle, "Resource").GetValue(b)) + "x" + (int)AccessTools.Field(_bundle, "Count").GetValue(b) + "@" + at.x.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "," + at.y.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
            }
            return parts.Count == 0 ? "-" : string.Join(" ", parts);
        }

        private static readonly HashSet<string> _failed = new HashSet<string>();

        private static void Fail(string what, Exception e)
        {
            if (_failed.Add(what)) Log.Error("lockstep: Worldfall's " + what + " (in a tick): " + (e.InnerException ?? e));
        }
    }
}
