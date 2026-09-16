using System;
using System.Collections.Generic;
using System.Reflection;
using MelonLoader;

[assembly: MelonInfo(typeof(GHPCNativeLiveAAR.Mod), "GHPC Native Live AAR", "1.0.4-scratch", "Toxic_Cxnt")]
[assembly: MelonGame("Radian Simulations LLC", "GHPC")]

namespace GHPCNativeLiveAAR
{
    public sealed class Mod : MelonMod
    {
        public override void OnInitializeMelon()
        {
            LiveAar.Initialize();
            MelonLogger.Msg("GHPC Native Live AAR scratch v1.0.4 loaded.");
        }

        public override void OnUpdate() { LiveAar.Tick(); }
        public override void OnSceneWasInitialized(int buildIndex, string sceneName) { LiveAar.SceneChanged(); }
        public override void OnSceneWasUnloaded(int buildIndex, string sceneName) { LiveAar.SceneChanged(); }
    }

    internal static class LiveAar
    {
        private static bool _ready;
        private static DateTime _nextHook = DateTime.MinValue;
        private static readonly List<PendingShot> _pending = new List<PendingShot>();
        private static readonly HashSet<object> _queued = new HashSet<object>(RefEq.Instance);
        private static NativeAarStage _stage;

        internal static void Initialize()
        {
            if (_ready) return;
            _ready = true;
            _stage = new NativeAarStage();
            TryHook();
        }

        internal static void SceneChanged()
        {
            Initialize();
            _pending.Clear();
            _queued.Clear();
            if (_stage != null) _stage.Hide();
            _nextHook = DateTime.MinValue;
        }

        internal static void Tick()
        {
            Initialize();
            if (_stage != null) _stage.Tick();

            if (!ShotHook.Installed && DateTime.UtcNow >= _nextHook) TryHook();

            DateTime now = DateTime.UtcNow;
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                PendingShot p = _pending[i];
                if (now < p.ReadyUtc) continue;

                object target = FindTarget(p.Shot);
                if (target == null && p.Attempts < 5)
                {
                    p.Attempts++;
                    p.ReadyUtc = now.AddMilliseconds(160);
                    continue;
                }

                _pending.RemoveAt(i);
                _queued.Remove(p.Shot);
                if (target == null) continue;

                try
                {
                    _stage.Show(p.Shot, target);
                }
                catch (Exception ex)
                {
                    Log("show failed: " + ex.GetType().Name + " - " + ex.Message);
                    _stage.Hide();
                }
            }
        }

        public static void ShotEnded(object __instance)
        {
            try
            {
                Initialize();
                if (__instance == null) return;
                if (R.Get(__instance, "ParentShot") != null) return;
                if (!R.Bool(__instance, "IsPlayerShot", false)) return;
                if (_queued.Contains(__instance)) return;

                _queued.Add(__instance);
                _pending.Add(new PendingShot(__instance, DateTime.UtcNow.AddMilliseconds(250)));
                while (_pending.Count > 12)
                {
                    _queued.Remove(_pending[0].Shot);
                    _pending.RemoveAt(0);
                }
            }
            catch (Exception ex) { Log("shot hook error: " + ex.Message); }
        }

        private static object FindTarget(object shot)
        {
            // The vanilla AAR/damage hierarchy hangs off the actual hit vehicle recorded
            // in ShotInfo frames. HitUnits/KilledUnits can be higher-level unit wrappers
            // that do not own AarVisual/AarModel children.
            List<object> frames = R.List(R.Get(shot, "AllShotFrames"));
            for (int i = frames.Count - 1; i >= 0; i--)
            {
                object vehicle = R.Get(frames[i], "HitVehicle");
                if (vehicle != null)
                {
                    Log("target resolved from ShotFrame.HitVehicle: " + vehicle.GetType().FullName);
                    return vehicle;
                }

                object model = R.Get(frames[i], "HitModel");
                if (model != null)
                {
                    object go = R.Get(model, "gameObject");
                    object tr = go == null ? null : R.Get(go, "transform");
                    object cur = tr;
                    for (int up = 0; cur != null && up < 16; up++)
                    {
                        object cg = R.Get(cur, "gameObject");
                        if (cg != null)
                        {
                            Type dv = R.Find("DamageableVehicle");
                            if (dv != null)
                            {
                                object comp = U.GetComponent(cg, dv);
                                if (comp != null)
                                {
                                    Log("target resolved from ShotFrame.HitModel parent: " + comp.GetType().FullName);
                                    return comp;
                                }
                            }
                        }
                        cur = R.Get(cur, "parent");
                    }
                }
            }

            List<object> killed = R.List(R.Get(shot, "KilledUnits"));
            if (killed.Count > 0)
            {
                object unit = killed[killed.Count - 1];
                Log("target fallback from KilledUnits: " + (unit == null ? "null" : unit.GetType().FullName));
                return unit;
            }

            List<object> hit = R.List(R.Get(shot, "HitUnits"));
            if (hit.Count > 0)
            {
                object unit = hit[hit.Count - 1];
                Log("target fallback from HitUnits: " + (unit == null ? "null" : unit.GetType().FullName));
                return unit;
            }

            return null;
        }

        private static void TryHook()
        {
            _nextHook = DateTime.UtcNow.AddSeconds(4);
            try
            {
                if (ShotHook.TryInstall()) Log("shot trigger online");
            }
            catch (Exception ex) { Log("hook install: " + ex.Message); }
        }

        internal static void Log(string s)
        {
            try { MelonLogger.Msg("[NativeLiveAAR] " + s); } catch { }
        }
    }

    internal sealed class PendingShot
    {
        internal object Shot;
        internal DateTime ReadyUtc;
        internal int Attempts;
        internal PendingShot(object shot, DateTime ready) { Shot = shot; ReadyUtc = ready; }
    }

    internal static class ShotHook
    {
        internal static bool Installed;
        private static object _harmony;

        internal static bool TryInstall()
        {
            if (Installed) return true;

            Type shotType = R.Find("GHPC.Weapons.ShotInfo");
            Type harmonyType = R.Find("HarmonyLib.Harmony");
            Type hmType = R.Find("HarmonyLib.HarmonyMethod");
            if (shotType == null || harmonyType == null || hmType == null) return false;

            MethodInfo original = null;
            MethodInfo[] methods = shotType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < methods.Length; i++)
                if (methods[i].Name == "NotifyShotTerminated" && methods[i].GetParameters().Length == 0) { original = methods[i]; break; }
            if (original == null) return false;

            if (_harmony == null)
            {
                ConstructorInfo hc = harmonyType.GetConstructor(new Type[] { typeof(string) });
                if (hc == null) return false;
                _harmony = hc.Invoke(new object[] { "ghpc.native.liveaar.scratch" });
            }

            MethodInfo postfix = typeof(LiveAar).GetMethod("ShotEnded", BindingFlags.Public | BindingFlags.Static);
            object hm = null;
            ConstructorInfo[] cs = hmType.GetConstructors();
            for (int i = 0; i < cs.Length; i++)
            {
                ParameterInfo[] ps = cs[i].GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType == typeof(MethodInfo))
                {
                    hm = cs[i].Invoke(new object[] { postfix });
                    break;
                }
            }
            if (hm == null) hm = Activator.CreateInstance(hmType);

            MethodInfo patch = null;
            MethodInfo[] pms = harmonyType.GetMethods(BindingFlags.Instance | BindingFlags.Public);
            for (int i = 0; i < pms.Length; i++)
            {
                if (pms[i].Name != "Patch") continue;
                ParameterInfo[] ps = pms[i].GetParameters();
                if (ps.Length >= 3 && typeof(MethodBase).IsAssignableFrom(ps[0].ParameterType) &&
                    ps[1].ParameterType == hmType && ps[2].ParameterType == hmType)
                { patch = pms[i]; break; }
            }
            if (patch == null) return false;

            object[] args = new object[patch.GetParameters().Length];
            args[0] = original;
            args[2] = hm;
            patch.Invoke(_harmony, args);
            Installed = true;
            return true;
        }
    }
}
