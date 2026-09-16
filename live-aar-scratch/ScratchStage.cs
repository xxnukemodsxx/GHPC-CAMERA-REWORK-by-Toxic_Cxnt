using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace GHPCNativeLiveAAR
{
    internal sealed class NativeAarStage
    {
        private readonly List<object> _created = new List<object>();
        private object _root;
        private object _baseCam;
        private object _xrayCam;
        private int _aarLayer = 30;
        private int _xrayLayer = 31;
        private float _radius = 3f;
        private object _center;
        private DateTime _hideAt;
        private bool _active;

        internal void Tick()
        {
            if (_active && DateTime.UtcNow >= _hideAt) Hide();
        }

        internal void Hide()
        {
            _active = false;
            for (int i = _created.Count - 1; i >= 0; i--) U.Destroy(_created[i]);
            _created.Clear();
            _root = _baseCam = _xrayCam = null;
        }

        internal void Show(object shot, object targetUnit)
        {
            Hide();
            U.Init();
            ResolveLayers();

            object vehicleGo = R.Get(targetUnit, "gameObject");
            if (vehicleGo == null) vehicleGo = R.Call(targetUnit, "get_gameObject");
            if (vehicleGo == null) vehicleGo = R.Call(targetUnit, "GHPC.IUnit.get_gameObject");
            if (vehicleGo == null) throw new InvalidOperationException("target GameObject unavailable");

            object aarRootGo = ResolveAarRoot(vehicleGo, targetUnit);
            object sourceRoot = R.Get(aarRootGo, "transform");
            if (sourceRoot == null) throw new InvalidOperationException("target AAR transform unavailable");

            _root = U.GO("GHPC_NativeLiveAAR_Stage");
            _created.Add(_root);
            object stageTr = R.Get(_root, "transform");
            R.Set(stageTr, "position", U.V(0f, -100000f, 0f));

            Dictionary<object, object> tmap = new Dictionary<object, object>(RefEq.Instance);
            object visualRoot = CloneTransformTree(sourceRoot, stageTr, tmap);

            object pose = FindRecordedPose(shot, targetUnit);
            ApplyRecordedPose(pose, tmap);

            NativeCaptureState state = new NativeCaptureState();
            try
            {
                state.Begin(shot, aarRootGo, vehicleGo);
                CloneNativeAarRenderers(aarRootGo, sourceRoot, stageTr, tmap, state);
                CloneVehicleShell(vehicleGo, aarRootGo, sourceRoot, stageTr, tmap, state);
            }
            finally
            {
                state.Restore();
            }

            if (state.ClonedRendererCount == 0 && state.ShellRendererCount == 0)
                throw new InvalidOperationException("GHPC exposed no AAR or vehicle renderers for this target");

            ApplyRecordedCrewState(targetUnit, pose, state.RendererMap);
            FitCameras();
            CreateShotLines(shot, sourceRoot, stageTr);
            _hideAt = DateTime.UtcNow.AddSeconds(8);
            _active = true;

            LiveAar.Log("native snapshot: AarVisual=" + state.AarVisualCount +
                        ", AarModel=" + state.AarModelCount +
                        ", directHitModels=" + state.DirectHitModelCount +
                        ", aarRenderers=" + state.ClonedRendererCount +
                        ", shellRenderers=" + state.ShellRendererCount);
        }


        private object ResolveAarRoot(object initialGo, object targetUnit)
        {
            Type avType = R.Find("AarVisual");
            Type amType = R.Find("AarModel");
            Type lateType = R.Find("LateFollowTarget");

            // GHPC deliberately detaches much of a vehicle's visible/armor hierarchy and
            // drives it through LateFollowTarget. Vanilla AarVisuals commonly live on
            // that detached follower tree rather than beneath GHPC.Vehicle.Vehicle.
            if (lateType != null)
            {
                object directLate = U.GetComponent(initialGo, lateType);
                object direct = BestLateFollowerRoot(directLate, avType, amType);
                if (direct != null)
                {
                    int av = avType == null ? 0 : U.Components(direct, avType, true).Count;
                    int am = amType == null ? 0 : U.Components(direct, amType, true).Count;
                    LiveAar.Log("AAR root resolved from vehicle LateFollowTarget with AarVisual=" + av + ", AarModel=" + am);
                    return direct;
                }

                List<object> lates = U.Components(initialGo, lateType, true);
                object bestLateGo = null;
                int bestLateScore = 0;
                for (int i = 0; i < lates.Count; i++)
                {
                    object go = BestLateFollowerRoot(lates[i], avType, amType);
                    if (go == null) continue;
                    int av = avType == null ? 0 : U.Components(go, avType, true).Count;
                    int am = amType == null ? 0 : U.Components(go, amType, true).Count;
                    int score = av * 100 + am;
                    if (score > bestLateScore)
                    {
                        bestLateScore = score;
                        bestLateGo = go;
                    }
                }
                if (bestLateGo != null)
                {
                    int av = avType == null ? 0 : U.Components(bestLateGo, avType, true).Count;
                    int am = amType == null ? 0 : U.Components(bestLateGo, amType, true).Count;
                    LiveAar.Log("AAR root resolved from nested LateFollowTarget with AarVisual=" + av + ", AarModel=" + am);
                    return bestLateGo;
                }
            }

            object best = initialGo;
            object tr = R.Get(initialGo, "transform");
            object cur = tr;

            for (int depth = 0; cur != null && depth < 16; depth++)
            {
                object go = R.Get(cur, "gameObject");
                if (go != null)
                {
                    int av = avType == null ? 0 : U.Components(go, avType, true).Count;
                    int am = amType == null ? 0 : U.Components(go, amType, true).Count;
                    if (av > 0 || am > 0)
                    {
                        LiveAar.Log("AAR root resolved at depth " + depth + " with AarVisual=" + av + ", AarModel=" + am);
                        return go;
                    }
                    best = go;
                }
                cur = R.Get(cur, "parent");
            }

            object[] related = new object[]
            {
                R.Get(targetUnit, "Vehicle"),
                R.Get(targetUnit, "DamageableVehicle"),
                R.Get(targetUnit, "CrewManager"),
                R.Get(targetUnit, "LoadoutManager"),
                R.Get(targetUnit, "Unit")
            };

            for (int i = 0; i < related.Length; i++)
            {
                object o = related[i];
                if (o == null) continue;
                object go = R.Get(o, "gameObject");
                if (go == null)
                {
                    object rt = R.Get(o, "transform");
                    go = rt == null ? null : R.Get(rt, "gameObject");
                }
                if (go == null) continue;

                int av = avType == null ? 0 : U.Components(go, avType, true).Count;
                int am = amType == null ? 0 : U.Components(go, amType, true).Count;
                if (av > 0 || am > 0)
                {
                    LiveAar.Log("AAR root resolved from related object with AarVisual=" + av + ", AarModel=" + am);
                    return go;
                }
            }

            LiveAar.Log("AAR root fallback used; no native AAR components visible under vehicle or LateFollowTarget hierarchy.");
            return best;
        }

        private object BestLateFollowerRoot(object lateFollowTarget, Type avType, Type amType)
        {
            if (lateFollowTarget == null) return null;
            List<object> followers = R.List(R.Get(lateFollowTarget, "_lateFollowers"));
            if (followers.Count == 0) followers = R.List(R.Get(lateFollowTarget, "LateFollowers"));

            object best = null;
            int bestScore = 0;
            for (int i = 0; i < followers.Count; i++)
            {
                object f = followers[i];
                if (f == null) continue;

                object tr = R.Get(f, "transform");
                if (tr == null)
                {
                    object go0 = R.Get(f, "gameObject");
                    tr = go0 == null ? null : R.Get(go0, "transform");
                }
                if (tr == null) continue;

                // A nested follower may point at turret/gun armor. Walk upward a few
                // levels to capture the full detached vehicle visual tree.
                object cur = tr;
                for (int up = 0; cur != null && up < 8; up++)
                {
                    object go = R.Get(cur, "gameObject");
                    if (go == null) break;
                    int av = avType == null ? 0 : U.Components(go, avType, true).Count;
                    int am = amType == null ? 0 : U.Components(go, amType, true).Count;
                    int score = av * 100 + am;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = go;
                    }
                    cur = R.Get(cur, "parent");
                }
            }
            return bestScore > 0 ? best : null;
        }

        private void ResolveLayers()
        {
            int a = 30, x = 31;
            try
            {
                Type t = R.Find("GHPC.ConstantsAndInfoManager");
                object inst = R.GetStatic(t, "Instance");
                if (inst != null)
                {
                    a = R.Int(inst, "AarLayer", a);
                    x = R.Int(inst, "XrayOnlyLayer", x);
                }
            }
            catch { }

            if (a < 0 || a > 31) a = 30;
            if (x < 0 || x > 31 || x == a) x = a == 31 ? 30 : 31;
            _aarLayer = a;
            _xrayLayer = x;
        }

        private object CloneTransformTree(object srcRoot, object parent, Dictionary<object, object> map)
        {
            object rootGo = U.GO("NativeAarVisualRoot");
            _created.Add(rootGo);
            object dstRoot = R.Get(rootGo, "transform");
            R.Call(dstRoot, "SetParent", parent, false);
            R.Set(dstRoot, "localPosition", U.V(0, 0, 0));
            R.Set(dstRoot, "localRotation", U.QIdentity());
            R.Set(dstRoot, "localScale", R.Get(srcRoot, "localScale"));
            map[srcRoot] = dstRoot;
            CloneChildren(srcRoot, dstRoot, map);
            return rootGo;
        }

        private void CloneChildren(object src, object dst, Dictionary<object, object> map)
        {
            int count = R.Int(src, "childCount", 0);
            for (int i = 0; i < count; i++)
            {
                object sc = R.Call(src, "GetChild", i);
                if (sc == null) continue;
                string name = R.Str(sc, "name");
                object go = U.GO(string.IsNullOrEmpty(name) ? "part" : name);
                _created.Add(go);
                object dc = R.Get(go, "transform");
                R.Call(dc, "SetParent", dst, false);
                R.Set(dc, "localPosition", R.Get(sc, "localPosition"));
                R.Set(dc, "localRotation", R.Get(sc, "localRotation"));
                R.Set(dc, "localScale", R.Get(sc, "localScale"));
                map[sc] = dc;
                CloneChildren(sc, dc, map);
            }
        }

        private object FindRecordedPose(object shot, object targetUnit)
        {
            List<object> poses = R.List(R.Get(shot, "UnitPoses"));
            for (int i = 0; i < poses.Count; i++)
            {
                object p = poses[i];
                if (p != null && object.ReferenceEquals(R.Get(p, "Unit"), targetUnit)) return p;
            }
            return null;
        }

        private void ApplyRecordedPose(object pose, Dictionary<object, object> map)
        {
            if (pose == null) return;
            List<object> posed = R.List(R.Get(pose, "PosedTransforms"));
            for (int i = 0; i < posed.Count; i++)
            {
                object pr = posed[i];
                object src = R.Get(pr, "Trans");
                object dst;
                if (src == null || !map.TryGetValue(src, out dst)) continue;

                object psrc = R.Get(pr, "Parent");
                object pdst;
                if (psrc != null && map.TryGetValue(psrc, out pdst))
                    R.Call(dst, "SetParent", pdst, false);

                object pos = R.Get(pr, "Pos");
                object rot = R.Get(pr, "Rot");
                if (pos != null) R.Set(dst, "localPosition", pos);
                if (rot != null) R.Set(dst, "localRotation", rot);
            }
        }

        private void CloneNativeAarRenderers(object aarRootGo, object sourceRoot, object stageTransform, Dictionary<object, object> tmap, NativeCaptureState state)
        {
            HashSet<object> selected = new HashSet<object>(RefEq.Instance);
            foreach (object r in state.AarVisualRules.Keys) selected.Add(r);
            foreach (object r in state.AarModelRenderers) selected.Add(r);
            foreach (object r in state.DirectHitModelRenderers) selected.Add(r);

            if (selected.Count == 0)
                throw new InvalidOperationException("vehicle has no native AarVisual/AarModel renderer set");

            foreach (object sr in selected)
            {
                if (sr == null) continue;

                AarRule rule;
                bool hasRule = state.AarVisualRules.TryGetValue(sr, out rule);
                bool directHit = state.DirectHitModelRenderers.Contains(sr);
                bool modelOnly = state.AarModelRenderers.Contains(sr) || directHit;

                if (hasRule && rule.Hidden) continue;
                if (hasRule && rule.Mode == 0 && !rule.Highlighted) continue;

                object st = R.Get(sr, "transform");
                object dt = null;
                if (st != null) tmap.TryGetValue(st, out dt);

                object dr = dt != null
                    ? CloneRenderer(sr, dt, tmap)
                    : CloneLooseRenderer(sr, sourceRoot, stageTransform, tmap);

                if (dr == null) continue;

                int layer = modelOnly ? _xrayLayer : _aarLayer;
                if (hasRule && rule.Mode == 1) layer = _xrayLayer;

                object dgo = R.Get(R.Get(dr, "transform"), "gameObject");
                if (dgo == null && dt != null) dgo = R.Get(dt, "gameObject");
                if (dgo != null) R.Set(dgo, "layer", layer);

                object mats = R.Get(sr, "sharedMaterials");
                if (hasRule && rule.SwitchMaterials && rule.AarMaterial != null)
                    mats = U.MaterialArray(rule.AarMaterial, Math.Max(1, U.ArrayLen(mats)));
                else if (directHit)
                {
                    Array dmg = U.SolidMaterials(Math.Max(1, U.ArrayLen(mats)), 1f, .28f, .03f, 1f);
                    if (dmg != null) mats = dmg;
                }

                R.Set(dr, "sharedMaterials", mats);
                R.Set(dr, "enabled", true);
                state.RendererMap[sr] = dr;
                state.ClonedRendererCount++;
            }
        }

        private void CloneVehicleShell(object aarRootGo, object sourceRoot, object stageTransform, Dictionary<object, object> tmap, NativeCaptureState state)
        {
            List<object> all = U.Components(aarRootGo, U.RendererType, true);
            HashSet<object> native = new HashSet<object>(RefEq.Instance);
            foreach (object r in state.AarVisualRules.Keys) native.Add(r);
            foreach (object r in state.AarModelRenderers) native.Add(r);
            foreach (object r in state.DirectHitModelRenderers) native.Add(r);

            for (int i = 0; i < all.Count; i++)
            {
                object sr = all[i];
                if (sr == null || native.Contains(sr)) continue;
                if (!R.Bool(sr, "enabled", false)) continue;

                object st = R.Get(sr, "transform");
                if (st == null) continue;
                string n = (R.Str(st, "name") ?? R.Str(sr, "name") ?? "").ToLowerInvariant();
                if (n.Contains("shadow") || n.Contains("decal") || n.Contains("dust") ||
                    n.Contains("smoke") || n.Contains("particle") || n.Contains("marker") ||
                    n.Contains("icon") || n.Contains("text") || n.Contains("canvas"))
                    continue;

                object b = R.Get(sr, "bounds");
                object ext = b == null ? null : R.Get(b, "extents");
                object cen = b == null ? null : R.Get(b, "center");
                if (ext == null || cen == null) continue;

                float er = U.Mag(ext);
                if (er < .03f || er > 10f) continue;

                object lc = R.Call(sourceRoot, "InverseTransformPoint", cen);
                if (lc == null || U.Mag(lc) > 14f) continue;

                object dt;
                if (!tmap.TryGetValue(st, out dt)) continue;

                object dr = CloneRenderer(sr, dt, tmap);
                if (dr == null) continue;

                object dgo = R.Get(dt, "gameObject");
                if (dgo != null) R.Set(dgo, "layer", _aarLayer);

                R.Set(dr, "sharedMaterials", R.Get(sr, "sharedMaterials"));
                R.Set(dr, "enabled", true);
                state.RendererMap[sr] = dr;
                state.ShellRendererCount++;
            }
        }

        private object CloneLooseRenderer(object sr, object sourceRoot, object stageTransform, Dictionary<object, object> tmap)
        {
            object st = R.Get(sr, "transform");
            if (st == null) return null;

            Type actual = sr.GetType();
            if (U.MeshRendererType == null || !U.MeshRendererType.IsAssignableFrom(actual))
                return null;

            object go = U.GO("NativeAarLoose_" + (R.Str(st, "name") ?? "model"));
            _created.Add(go);
            object dt = R.Get(go, "transform");
            R.Call(dt, "SetParent", stageTransform, false);

            object srcPos = R.Get(st, "position");
            object localPos = R.Call(sourceRoot, "InverseTransformPoint", srcPos);
            if (localPos != null) R.Set(dt, "localPosition", localPos);

            object srcRootRot = R.Get(sourceRoot, "rotation");
            object srcRot = R.Get(st, "rotation");
            if (srcRootRot != null && srcRot != null)
                R.Set(dt, "localRotation", U.RelativeRotation(srcRootRot, srcRot));

            object srcLossy = R.Get(st, "lossyScale");
            object rootLossy = R.Get(sourceRoot, "lossyScale");
            if (srcLossy != null && rootLossy != null)
                R.Set(dt, "localScale", U.DivScale(srcLossy, rootLossy));

            object smf = U.GetComponent(R.Get(st, "gameObject"), U.MeshFilterType);
            if (smf != null)
            {
                object dmf = U.AddComponent(go, U.MeshFilterType);
                R.Set(dmf, "sharedMesh", R.Get(smf, "sharedMesh"));
            }

            return U.AddComponent(go, U.MeshRendererType);
        }

        private object CloneRenderer(object sr, object dstTransform, Dictionary<object, object> tmap)
        {
            object dgo = R.Get(dstTransform, "gameObject");
            Type actual = sr.GetType();

            if (U.SkinType != null && U.SkinType.IsAssignableFrom(actual))
            {
                object dr = U.AddComponent(dgo, U.SkinType);
                R.Set(dr, "sharedMesh", R.Get(sr, "sharedMesh"));
                R.Set(dr, "localBounds", R.Get(sr, "localBounds"));

                Array sb = R.Get(sr, "bones") as Array;
                if (sb != null)
                {
                    Type trType = R.Find("UnityEngine.Transform");
                    Array db = Array.CreateInstance(trType, sb.Length);
                    for (int i = 0; i < sb.Length; i++)
                    {
                        object mapped;
                        if (tmap.TryGetValue(sb.GetValue(i), out mapped)) db.SetValue(mapped, i);
                    }
                    R.Set(dr, "bones", db);
                }

                object sroot = R.Get(sr, "rootBone");
                object droot;
                if (sroot != null && tmap.TryGetValue(sroot, out droot)) R.Set(dr, "rootBone", droot);
                R.Set(dr, "updateWhenOffscreen", true);
                return dr;
            }

            if (U.MeshRendererType != null && U.MeshRendererType.IsAssignableFrom(actual))
            {
                object smf = U.GetComponent(R.Get(R.Get(sr, "transform"), "gameObject"), U.MeshFilterType);
                if (smf != null)
                {
                    object dmf = U.AddComponent(dgo, U.MeshFilterType);
                    R.Set(dmf, "sharedMesh", R.Get(smf, "sharedMesh"));
                }
                return U.AddComponent(dgo, U.MeshRendererType);
            }

            return null;
        }

        private void ApplyRecordedCrewState(object unit, object pose, Dictionary<object, object> rendererMap)
        {
            if (unit == null || pose == null) return;
            Array states = R.Get(pose, "CrewPresentStatuses") as Array;
            if (states == null) return;

            object crew = R.Get(unit, "CrewManager");
            if (crew == null) crew = R.Call(unit, "get_CrewManager");
            if (crew == null) return;

            for (int i = 0; i < states.Length; i++)
            {
                object s = states.GetValue(i);
                if (s == null || R.Bool(s, "present", true)) continue;

                object pos = R.Get(s, "position");
                object member = pos == null ? null : R.Call(crew, "GetCrewMember", pos);
                if (member == null) continue;

                List<object> visuals = R.List(R.Get(member, "AllAarVisuals"));
                for (int v = 0; v < visuals.Count; v++)
                {
                    List<object> rs = NativeCaptureState.VisualRenderers(visuals[v]);
                    for (int j = 0; j < rs.Count; j++)
                    {
                        object clone;
                        if (rendererMap.TryGetValue(rs[j], out clone)) R.Set(clone, "enabled", false);
                    }
                }
            }
        }

        private void FitCameras(object visualRoot)
        {
            List<object> rs = U.Components(visualRoot, U.RendererType, true);
            object rootTr = R.Get(_root, "transform");
            object center = R.Get(rootTr, "position");
            float radius = 3f;

            List<float> xs = new List<float>(), ys = new List<float>(), zs = new List<float>();
            List<object> centers = new List<object>();
            List<float> extents = new List<float>();

            for (int i = 0; i < rs.Count; i++)
            {
                if (!R.Bool(rs[i], "enabled", true)) continue;
                object b = R.Get(rs[i], "bounds");
                object cc = b == null ? null : R.Get(b, "center");
                object e = b == null ? null : R.Get(b, "extents");
                if (cc == null || e == null) continue;

                float er = U.Mag(e);
                object local = R.Call(rootTr, "InverseTransformPoint", cc);
                if (local == null) continue;
                float dist = U.Mag(local);
                if (er < .01f || er > 14f || dist > 18f) continue;

                xs.Add(U.X(local)); ys.Add(U.Y(local)); zs.Add(U.Z(local));
                centers.Add(cc); extents.Add(er);
            }

            if (xs.Count > 0)
            {
                xs.Sort(); ys.Sort(); zs.Sort();
                int m = xs.Count / 2;
                float mx = xs[m], my = ys[m], mz = zs[m];
                center = R.Call(rootTr, "TransformPoint", U.V(mx, my, mz));

                float reach = 1.8f;
                for (int i = 0; i < centers.Count; i++)
                {
                    object lc = R.Call(rootTr, "InverseTransformPoint", centers[i]);
                    float dx = U.X(lc)-mx, dy = U.Y(lc)-my, dz = U.Z(lc)-mz;
                    float dd = (float)Math.Sqrt(dx*dx + dy*dy + dz*dz) + extents[i];
                    if (dd > reach) reach = dd;
                }
                radius = Math.Max(1.8f, Math.Min(7.2f, reach * 1.03f));
            }

            _radius = radius;
            _center = center;

            object camPos = U.Add(center, U.V(radius * 2.15f, radius * .58f, -radius * 3.15f));

            _baseCam = U.Camera("GHPC_NativeLiveAAR_BaseCamera");
            object baseGo = R.Get(_baseCam, "gameObject");
            _created.Add(baseGo);
            object bt = R.Get(baseGo, "transform");
            R.Set(bt, "position", camPos);
            R.Call(bt, "LookAt", center);
            ConfigureCamera(_baseCam, U.Mask(_aarLayer), 50f, false);

            _xrayCam = U.Camera("GHPC_NativeLiveAAR_XrayCamera");
            object xgo = R.Get(_xrayCam, "gameObject");
            _created.Add(xgo);
            object xt = R.Get(xgo, "transform");
            R.Set(xt, "position", camPos);
            R.Set(xt, "rotation", R.Get(bt, "rotation"));
            ConfigureCamera(_xrayCam, U.Mask(_xrayLayer), 51f, true);

            int both = U.Mask(_aarLayer) | U.Mask(_xrayLayer);
            object l1 = U.PointLight("GHPC_NativeLiveAAR_L1", U.Add(center, U.V(radius, radius, -radius)), radius*5f, 2.8f, both);
            object l2 = U.PointLight("GHPC_NativeLiveAAR_L2", U.Add(center, U.V(-radius, radius*.40f, radius)), radius*4f, 1.25f, both);
            if (l1 != null) _created.Add(l1);
            if (l2 != null) _created.Add(l2);
        }

        private void ConfigureCamera(object cam, int mask, float depth, bool overlay)
        {
            R.Set(cam, "nearClipPlane", .03f);
            R.Set(cam, "farClipPlane", 100f);
            R.Set(cam, "orthographic", false);
            R.Set(cam, "fieldOfView", 30f);
            R.Set(cam, "cullingMask", mask);
            R.Set(cam, "depth", depth);
            R.Set(cam, "enabled", true);
            R.Set(cam, "allowHDR", false);
            R.Set(cam, "allowMSAA", true);
            if (overlay) U.DepthOnly(cam); else U.SolidBlack(cam);

            float sw = U.ScreenW(), sh = U.ScreenH();
            float w = Math.Max(380f, Math.Min(sw * .30f, 620f));
            float h = w * 9f / 16f;
            float px = sw - w - 18f;
            float py = 74f;
            float nx = px / sw;
            float ny = 1f - ((py + h) / sh);
            R.Set(cam, "rect", U.Rect(nx, ny, w/sw, h/sh));
            R.Set(cam, "aspect", 16f/9f);
        }

        private void CreateShotLines(object rootShot, object sourceRoot, object stageTransform)
        {
            List<object> family = CollectShotFamily(rootShot);

            for (int i = 0; i < family.Count; i++)
            {
                object shot = family[i];
                bool child = !object.ReferenceEquals(shot, rootShot);
                List<object> frames = R.List(R.Get(shot, "AllShotFrames"));
                List<object> pts = new List<object>();

                object start = R.Get(shot, "StartPosition");
                if (start != null) pts.Add(ClampTracePoint(MapPoint(sourceRoot, stageTransform, start), child ? _radius * 1.35f : _radius * 1.75f));

                bool jet = false;
                for (int j = 0; j < frames.Count; j++)
                {
                    object wp = R.Get(frames[j], "WorldPosition");
                    if (wp != null)
                    {
                        object mp = MapPoint(sourceRoot, stageTransform, wp);
                        pts.Add(ClampTracePoint(mp, child ? _radius * 1.35f : _radius * 1.75f));
                    }
                    if (R.Bool(frames[j], "IsJet", false)) jet = true;
                }

                object stop = R.Get(shot, "StopPosition");
                if (stop != null) pts.Add(ClampTracePoint(MapPoint(sourceRoot, stageTransform, stop), child ? _radius * 1.35f : _radius * 1.75f));

                pts = RemoveDuplicatePoints(pts);
                if (pts.Count < 2) continue;

                float width = child ? Math.Max(.009f, _radius * .0045f) : Math.Max(.012f, _radius * .006f);

                if (!child)
                {
                    for (int p = 0; p < pts.Count - 1; p++)
                    {
                        List<object> leg = new List<object>();
                        leg.Add(pts[p]);
                        leg.Add(pts[p + 1]);
                        float shade = (p % 2 == 0) ? 1f : .66f;
                        object line = U.Line("NativeAarMainTrace", leg, width, shade, shade, shade, 1f, _xrayLayer);
                        if (line != null) _created.Add(line);
                    }
                    continue;
                }

                float power = R.Float(shot, "ActualPen", 0f) / 20f;
                if (power < 0f) power = 0f;
                if (power > 1f) power = 1f;

                float rr = 1f;
                float gg = .08f + .92f * power;
                float bb = .015f;

                if (jet)
                {
                    rr = 1f;
                    gg = .72f;
                    bb = .05f;
                    width = Math.Max(width, .011f);
                }

                object spall = U.Line(jet ? "NativeAarJetTrace" : "NativeAarSpallTrace",
                                      pts, width, rr, gg, bb, .96f, _xrayLayer);
                if (spall != null) _created.Add(spall);
            }
        }

        private List<object> CollectShotFamily(object rootShot)
        {
            List<object> family = new List<object>();
            family.Add(rootShot);
            try
            {
                Type at = R.Find("GHPC.AarController");
                object ac = R.GetStatic(at, "Instance");
                List<object> shots = R.List(R.Get(ac, "SessionShots"));
                for (int i = 0; i < shots.Count; i++)
                {
                    object s = shots[i];
                    if (s == null || object.ReferenceEquals(s, rootShot)) continue;
                    object p = R.Get(s, "ParentShot");
                    int guard = 0;
                    while (p != null && guard++ < 20)
                    {
                        if (object.ReferenceEquals(p, rootShot))
                        {
                            family.Add(s);
                            break;
                        }
                        p = R.Get(p, "ParentShot");
                    }
                }
            }
            catch { }
            return family;
        }

        private object ClampTracePoint(object p, float maxRadius)
        {
            if (p == null || _center == null) return p;
            float dx = U.X(p) - U.X(_center);
            float dy = U.Y(p) - U.Y(_center);
            float dz = U.Z(p) - U.Z(_center);
            float mag = (float)Math.Sqrt(dx*dx + dy*dy + dz*dz);
            if (mag <= maxRadius || mag < .0001f) return p;
            float k = maxRadius / mag;
            return U.V(U.X(_center) + dx*k, U.Y(_center) + dy*k, U.Z(_center) + dz*k);
        }

        private List<object> RemoveDuplicatePoints(List<object> pts)
        {
            List<object> result = new List<object>();
            for (int i = 0; i < pts.Count; i++)
            {
                object p = pts[i];
                if (p == null) continue;
                if (result.Count > 0)
                {
                    object q = result[result.Count - 1];
                    float dx = U.X(p)-U.X(q), dy = U.Y(p)-U.Y(q), dz = U.Z(p)-U.Z(q);
                    if (dx*dx + dy*dy + dz*dz < .000025f) continue;
                }
                result.Add(p);
            }
            return result;
        }

        private object MapPoint(object sourceRoot, object stageTransform, object world)
        {
            object local = R.Call(sourceRoot, "InverseTransformPoint", world);
            return R.Call(stageTransform, "TransformPoint", local);
        }
    }

    internal sealed class AarRule
    {
        internal object AarMaterial;
        internal int Mode;
        internal bool SwitchMaterials;
        internal bool Highlighted;
        internal bool Hidden;
    }

    internal sealed class NativeCaptureState
    {
        internal readonly Dictionary<object, AarRule> AarVisualRules = new Dictionary<object, AarRule>(RefEq.Instance);
        internal readonly HashSet<object> AarModelRenderers = new HashSet<object>(RefEq.Instance);
        internal readonly HashSet<object> DirectHitModelRenderers = new HashSet<object>(RefEq.Instance);
        internal readonly Dictionary<object, object> RendererMap = new Dictionary<object, object>(RefEq.Instance);

        internal int AarVisualCount;
        internal int AarModelCount;
        internal int DirectHitModelCount;
        internal int ClonedRendererCount;
        internal int ShellRendererCount;

        private readonly HashSet<object> _seenModels = new HashSet<object>(RefEq.Instance);

        internal void Begin(object shot, object aarRootGo, object vehicleRootGo)
        {
            BuildAarVisualRules(aarRootGo);
            BuildAmmoAarModels(vehicleRootGo);
            if (!object.ReferenceEquals(aarRootGo, vehicleRootGo))
                BuildAmmoAarModels(aarRootGo);
            BuildDirectHitModels(shot);
        }

        internal void Restore()
        {
        }

        private void BuildAarVisualRules(object targetGo)
        {
            Type avType = R.Find("GHPC.AarVisual");
            if (avType == null) avType = R.Find("AarVisual");
            if (avType == null) return;

            List<object> visuals = U.Components(targetGo, avType, true);
            AarVisualCount = visuals.Count;

            for (int i = 0; i < visuals.Count; i++)
            {
                object av = visuals[i];
                List<object> rs = VisualRenderers(av);
                if (rs.Count == 0) continue;

                object mat = R.Get(av, "AarMaterial");
                bool swap = R.Bool(av, "SwitchMaterials", true);
                bool hidden = R.Bool(av, "Hidden", false);

                int mode = 2;
                object mv = R.Get(av, "RenderMode");
                if (mv == null) mv = R.Get(av, "_renderMode");
                if (mv != null) { try { mode = Convert.ToInt32(mv); } catch { } }

                AarRule rule = new AarRule
                {
                    AarMaterial = mat,
                    SwitchMaterials = swap,
                    Mode = mode,
                    Highlighted = true,
                    Hidden = hidden
                };

                for (int j = 0; j < rs.Count; j++)
                    if (rs[j] != null) AarVisualRules[rs[j]] = rule;
            }
        }

        internal static List<object> VisualRenderers(object av)
        {
            List<object> rs = R.List(R.Get(av, "_renderers"));
            if (rs.Count == 0) rs = R.List(R.Get(av, "SpecificRenderers"));
            if (rs.Count == 0)
            {
                object go = R.Get(av, "gameObject");
                if (go != null) rs = U.Components(go, U.RendererType, true);
            }
            return rs;
        }

        private void BuildAmmoAarModels(object rootGo)
        {
            if (rootGo == null) return;

            Type rackType = R.Find("GHPC.Weapons.AmmoRack");
            if (rackType == null) rackType = R.Find("AmmoRack");
            if (rackType == null) return;

            List<object> racks = U.Components(rootGo, rackType, true);
            for (int i = 0; i < racks.Count; i++)
            {
                object m = R.Call(racks[i], "GetAarModel");
                AddModelRenderers(m, false);
                object charge = R.Call(racks[i], "GetChargeAarModel");
                AddModelRenderers(charge, false);
            }
        }

        private void BuildDirectHitModels(object rootShot)
        {
            List<object> family = CollectShotFamily(rootShot);
            HashSet<object> models = new HashSet<object>(RefEq.Instance);

            for (int s = 0; s < family.Count; s++)
            {
                List<object> frames = R.List(R.Get(family[s], "AllShotFrames"));
                for (int i = 0; i < frames.Count; i++)
                {
                    object hm = R.Get(frames[i], "HitModel");
                    if (hm == null || models.Contains(hm)) continue;
                    models.Add(hm);
                    AddModelRenderers(hm, true);
                }
            }

            DirectHitModelCount = models.Count;
        }

        private List<object> CollectShotFamily(object rootShot)
        {
            List<object> family = new List<object>();
            family.Add(rootShot);
            try
            {
                Type at = R.Find("GHPC.AarController");
                object ac = R.GetStatic(at, "Instance");
                List<object> shots = R.List(R.Get(ac, "SessionShots"));
                for (int i = 0; i < shots.Count; i++)
                {
                    object s = shots[i];
                    if (s == null || object.ReferenceEquals(s, rootShot)) continue;
                    object p = R.Get(s, "ParentShot");
                    int guard = 0;
                    while (p != null && guard++ < 20)
                    {
                        if (object.ReferenceEquals(p, rootShot))
                        {
                            family.Add(s);
                            break;
                        }
                        p = R.Get(p, "ParentShot");
                    }
                }
            }
            catch { }
            return family;
        }

        private void AddModelRenderers(object model, bool directHit)
        {
            if (model == null) return;
            if (!_seenModels.Contains(model))
            {
                _seenModels.Add(model);
                AarModelCount++;
            }

            object go = R.Get(model, "gameObject");
            if (go == null)
            {
                object tr = R.Get(model, "transform");
                go = tr == null ? null : R.Get(tr, "gameObject");
            }
            if (go == null) return;

            List<object> rs = R.List(R.Get(model, "_renderers"));
            if (rs.Count == 0) rs = U.Components(go, U.RendererType, true);

            for (int i = 0; i < rs.Count; i++)
            {
                object rr = rs[i];
                if (rr == null) continue;
                AarModelRenderers.Add(rr);
                if (directHit) DirectHitModelRenderers.Add(rr);
            }
        }
    }

}
