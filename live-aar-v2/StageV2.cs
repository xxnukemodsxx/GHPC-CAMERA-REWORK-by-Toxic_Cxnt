using System;
using System.Collections;
using System.Collections.Generic;

namespace GHPCLiveAARV2
{
    internal sealed class AarStage
    {
        private readonly List<object> _created = new List<object>();
        private readonly List<object> _resources = new List<object>();
        private readonly Dictionary<object, object> _rendererMap = new Dictionary<object, object>(RefEq.Instance);

        private object _root;
        private object _baseCam;
        private object _xrayCam;
        private object _center;
        private float _radius = 3f;
        private int _aarLayer = 30;
        private int _xrayLayer = 31;
        private DateTime _hideAt;
        private bool _active;

        private object _mainMat;
        private object _mainGrayMat;
        private object _spallMat;
        private object _jetMat;
        private object _damageMat;

        internal void Tick()
        {
            if (_active && DateTime.UtcNow >= _hideAt) Hide();
        }

        internal void Hide()
        {
            _active = false;
            for (int i = _created.Count - 1; i >= 0; i--) U.Destroy(_created[i]);
            for (int i = _resources.Count - 1; i >= 0; i--) U.Destroy(_resources[i]);
            _created.Clear();
            _resources.Clear();
            _rendererMap.Clear();
            _root = _baseCam = _xrayCam = _center = null;
            _mainMat = _mainGrayMat = _spallMat = _jetMat = _damageMat = null;
        }

        internal void Show(object shot, object targetVehicle)
        {
            Hide();
            U.Init();
            ResolveLayers();

            object vehicleGo = GetGameObject(targetVehicle);
            if (vehicleGo == null) throw new InvalidOperationException("target vehicle GameObject unavailable");
            object vehicleTr = R.Get(vehicleGo, "transform");
            if (vehicleTr == null) throw new InvalidOperationException("target vehicle transform unavailable");

            List<object> roots = CollectVehicleRoots(vehicleGo);
            NativeSnapshot snap = new NativeSnapshot(shot, targetVehicle, vehicleGo, roots);
            snap.Capture();

            _root = U.GO("GHPC_LiveAAR_V2_Stage");
            _created.Add(_root);
            object stageTr = R.Get(_root, "transform");
            R.Set(stageTr, "position", U.V(0f, -100000f, 0f));

            BuildMaterials();
            int shellCount = CloneShell(roots, vehicleTr, stageTr, snap);
            int nativeCount = CloneNative(vehicleTr, stageTr, snap);

            if (shellCount == 0 && nativeCount == 0)
                throw new InvalidOperationException("no usable GHPC vehicle/AAR renderers were captured");

            ApplyRecordedCrewState(targetVehicle, snap.RecordedPose);
            FitCameras();
            CreateShotLines(shot, vehicleTr, stageTr);

            _hideAt = DateTime.UtcNow.AddSeconds(8);
            _active = true;

            LiveAar.Log("snapshot vehicle=" + (R.Str(vehicleGo, "name") ?? targetVehicle.GetType().Name) +
                        ", roots=" + roots.Count +
                        ", AarVisual=" + snap.AarVisualCount +
                        ", AarModel=" + snap.AarModelCount +
                        ", hitModels=" + snap.HitModelCount +
                        ", shell=" + shellCount +
                        ", native=" + nativeCount +
                        ", traces=" + snap.ShotFamily.Count);
        }

        private object GetGameObject(object o)
        {
            if (o == null) return null;
            object go = R.Get(o, "gameObject");
            if (go != null) return go;
            object tr = R.Get(o, "transform");
            if (tr != null) return R.Get(tr, "gameObject");
            return null;
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

        private List<object> CollectVehicleRoots(object vehicleGo)
        {
            List<object> roots = new List<object>();
            HashSet<object> seen = new HashSet<object>(RefEq.Instance);
            AddRoot(roots, seen, vehicleGo);

            Type lateType = R.Find("LateFollowTarget");
            if (lateType != null)
            {
                List<object> lates = U.Components(vehicleGo, lateType, true);
                object direct = U.GetComponent(vehicleGo, lateType);
                if (direct != null) lates.Insert(0, direct);

                for (int i = 0; i < lates.Count; i++)
                {
                    List<object> followers = R.List(R.Get(lates[i], "_lateFollowers"));
                    if (followers.Count == 0) followers = R.List(R.Get(lates[i], "LateFollowers"));
                    for (int j = 0; j < followers.Count; j++)
                    {
                        object f = followers[j];
                        object tr = R.Get(f, "transform");
                        if (tr == null)
                        {
                            object fgo = R.Get(f, "gameObject");
                            tr = fgo == null ? null : R.Get(fgo, "transform");
                        }
                        object cur = tr;
                        for (int up = 0; cur != null && up < 4; up++)
                        {
                            AddRoot(roots, seen, R.Get(cur, "gameObject"));
                            cur = R.Get(cur, "parent");
                        }
                    }
                }
            }

            // Also include the strongest nearby AAR root if it is detached from the vehicle root.
            Type avType = R.Find("AarVisual");
            if (avType == null) avType = R.Find("GHPC.AarVisual");
            int bestScore = 0;
            object best = null;
            for (int i = 0; i < roots.Count; i++)
            {
                int score = avType == null ? 0 : U.Components(roots[i], avType, true).Count;
                if (score > bestScore) { bestScore = score; best = roots[i]; }
            }
            if (best != null) AddRoot(roots, seen, best);
            return roots;
        }

        private void AddRoot(List<object> roots, HashSet<object> seen, object go)
        {
            if (go == null || seen.Contains(go)) return;
            seen.Add(go);
            roots.Add(go);
        }

        private void BuildMaterials()
        {
            _mainMat = U.SolidMaterial(.92f, .94f, .96f, 1f);
            _mainGrayMat = U.SolidMaterial(.55f, .58f, .62f, 1f);
            _spallMat = U.SolidMaterial(1f, .20f, .025f, 1f);
            _jetMat = U.SolidMaterial(1f, .76f, .06f, 1f);
            _damageMat = U.SolidMaterial(1f, .24f, .025f, 1f);
            AddResource(_mainMat); AddResource(_mainGrayMat); AddResource(_spallMat); AddResource(_jetMat); AddResource(_damageMat);
        }

        private void AddResource(object o)
        {
            if (o != null) _resources.Add(o);
        }

        private int CloneShell(List<object> roots, object vehicleTr, object stageTr, NativeSnapshot snap)
        {
            HashSet<object> seen = new HashSet<object>(RefEq.Instance);
            int count = 0;

            for (int ri = 0; ri < roots.Count; ri++)
            {
                List<object> rs = U.Components(roots[ri], U.RendererType, true);
                for (int i = 0; i < rs.Count; i++)
                {
                    object sr = rs[i];
                    if (sr == null || seen.Contains(sr)) continue;
                    seen.Add(sr);
                    if (snap.NativeRenderers.Contains(sr)) continue;
                    if (!R.Bool(sr, "enabled", false)) continue;
                    if (!IsShellCandidate(sr, vehicleTr)) continue;

                    object dr = CloneRendererSnapshot(sr, vehicleTr, stageTr, _aarLayer, R.Get(sr, "sharedMaterials"));
                    if (dr == null) continue;
                    _rendererMap[sr] = dr;
                    count++;
                }
            }
            return count;
        }

        private bool IsShellCandidate(object sr, object vehicleTr)
        {
            string typeName = sr.GetType().Name.ToLowerInvariant();
            if (typeName.Contains("line") || typeName.Contains("particle") || typeName.Contains("trail")) return false;

            object st = R.Get(sr, "transform");
            string n = ((R.Str(sr, "name") ?? "") + " " + (R.Str(st, "name") ?? "")).ToLowerInvariant();
            if (n.Contains("shadow") || n.Contains("decal") || n.Contains("dust") || n.Contains("smoke") ||
                n.Contains("particle") || n.Contains("marker") || n.Contains("icon") || n.Contains("canvas") ||
                n.Contains("crew") || n.Contains("ammo") || n.Contains("rack") || n.Contains("interior") ||
                n.Contains("internal") || n.Contains("xray") || n.Contains("aar") || n.Contains("hitmodel") ||
                n.Contains("damage") || n.Contains("collider")) return false;

            object b = R.Get(sr, "bounds");
            object c = b == null ? null : R.Get(b, "center");
            object e = b == null ? null : R.Get(b, "extents");
            if (c == null || e == null) return false;
            float er = U.Mag(e);
            if (er < .025f || er > 12f) return false;
            object lc = R.Call(vehicleTr, "InverseTransformPoint", c);
            if (lc == null || U.Mag(lc) > 18f) return false;
            return true;
        }

        private int CloneNative(object vehicleTr, object stageTr, NativeSnapshot snap)
        {
            int count = 0;
            foreach (object sr in snap.NativeRenderers)
            {
                if (sr == null) continue;

                AarRule rule;
                bool hasRule = snap.AarRules.TryGetValue(sr, out rule);
                bool directHit = snap.DirectHitRenderers.Contains(sr);
                bool ammo = snap.AarModelRenderers.Contains(sr);

                if (hasRule && rule.Hidden) continue;
                if (hasRule && rule.HighlightOnly && !rule.Highlighted) continue;

                int layer = _aarLayer;
                if (directHit || ammo || (hasRule && rule.Xrayed)) layer = _xrayLayer;

                object mats = R.Get(sr, "sharedMaterials");
                if (hasRule && rule.SwitchMaterials && rule.AarMaterial != null)
                    mats = U.MaterialArray(rule.AarMaterial, Math.Max(1, U.ArrayLen(mats)));
                else if (directHit && _damageMat != null)
                    mats = U.MaterialArray(_damageMat, Math.Max(1, U.ArrayLen(mats)));

                object dr = CloneRendererSnapshot(sr, vehicleTr, stageTr, layer, mats);
                if (dr == null) continue;
                _rendererMap[sr] = dr;
                count++;
            }
            return count;
        }

        private object CloneRendererSnapshot(object sr, object vehicleTr, object stageTr, int layer, object materials)
        {
            object st = R.Get(sr, "transform");
            if (st == null) return null;

            object go = U.GO("LiveAAR_" + (R.Str(st, "name") ?? "renderer"));
            _created.Add(go);
            R.Set(go, "layer", layer);
            object dt = R.Get(go, "transform");
            R.Call(dt, "SetParent", stageTr, false);

            object wp = R.Get(st, "position");
            object lp = R.Call(vehicleTr, "InverseTransformPoint", wp);
            if (lp != null) R.Set(dt, "localPosition", lp);

            object vr = R.Get(vehicleTr, "rotation");
            object wr = R.Get(st, "rotation");
            if (vr != null && wr != null) R.Set(dt, "localRotation", U.RelativeRotation(vr, wr));

            object sl = R.Get(st, "lossyScale");
            object vl = R.Get(vehicleTr, "lossyScale");
            if (sl != null && vl != null) R.Set(dt, "localScale", U.DivScale(sl, vl));

            object dr = null;
            Type actual = sr.GetType();
            if (U.SkinType != null && U.SkinType.IsAssignableFrom(actual))
            {
                object baked = U.BakeSkin(sr);
                if (baked == null) return null;
                AddResource(baked);
                object mf = U.AddComponent(go, U.MeshFilterType);
                R.Set(mf, "sharedMesh", baked);
                dr = U.AddComponent(go, U.MeshRendererType);
            }
            else if (U.MeshRendererType != null && U.MeshRendererType.IsAssignableFrom(actual))
            {
                object smf = U.GetComponent(R.Get(st, "gameObject"), U.MeshFilterType);
                if (smf == null) return null;
                object mf = U.AddComponent(go, U.MeshFilterType);
                R.Set(mf, "sharedMesh", R.Get(smf, "sharedMesh"));
                dr = U.AddComponent(go, U.MeshRendererType);
            }
            else return null;

            R.Set(dr, "sharedMaterials", materials);
            R.Set(dr, "enabled", true);
            return dr;
        }

        private void ApplyRecordedCrewState(object vehicle, object pose)
        {
            if (vehicle == null || pose == null) return;
            Array states = R.Get(pose, "CrewPresentStatuses") as Array;
            if (states == null) return;

            object crew = R.Get(vehicle, "CrewManager");
            if (crew == null) crew = R.Call(vehicle, "get_CrewManager");
            if (crew == null) return;

            for (int i = 0; i < states.Length; i++)
            {
                object state = states.GetValue(i);
                if (state == null || R.Bool(state, "present", true)) continue;
                object pos = R.Get(state, "position");
                object member = pos == null ? null : R.Call(crew, "GetCrewMember", pos);
                if (member == null) continue;

                List<object> visuals = R.List(R.Get(member, "AllAarVisuals"));
                for (int v = 0; v < visuals.Count; v++)
                {
                    List<object> rs = NativeSnapshot.VisualRenderers(visuals[v]);
                    for (int j = 0; j < rs.Count; j++)
                    {
                        object clone;
                        if (_rendererMap.TryGetValue(rs[j], out clone)) R.Set(clone, "enabled", false);
                    }
                }
            }
        }

        private void FitCameras()
        {
            object rootTr = R.Get(_root, "transform");
            List<object> rs = U.Components(_root, U.RendererType, true);
            bool have = false;
            float minX=0, minY=0, minZ=0, maxX=0, maxY=0, maxZ=0;

            for (int i = 0; i < rs.Count; i++)
            {
                object rr = rs[i];
                if (rr == null || !R.Bool(rr, "enabled", true)) continue;
                string tn = rr.GetType().Name.ToLowerInvariant();
                if (tn.Contains("line")) continue;

                object b = R.Get(rr, "bounds");
                object cc = b == null ? null : R.Get(b, "center");
                object e = b == null ? null : R.Get(b, "extents");
                if (cc == null || e == null) continue;
                float er = U.Mag(e);
                if (er < .01f || er > 14f) continue;

                object lc = R.Call(rootTr, "InverseTransformPoint", cc);
                if (lc == null || U.Mag(lc) > 18f) continue;
                float cx=U.X(lc), cy=U.Y(lc), cz=U.Z(lc);
                float ex=Math.Abs(U.X(e)), ey=Math.Abs(U.Y(e)), ez=Math.Abs(U.Z(e));
                float x0=cx-ex, y0=cy-ey, z0=cz-ez, x1=cx+ex, y1=cy+ey, z1=cz+ez;
                if (!have)
                {
                    minX=x0; minY=y0; minZ=z0; maxX=x1; maxY=y1; maxZ=z1; have=true;
                }
                else
                {
                    if (x0<minX) minX=x0; if (y0<minY) minY=y0; if (z0<minZ) minZ=z0;
                    if (x1>maxX) maxX=x1; if (y1>maxY) maxY=y1; if (z1>maxZ) maxZ=z1;
                }
            }

            object center = R.Get(rootTr, "position");
            float radius = 3f;
            if (have)
            {
                float mx=(minX+maxX)*.5f, my=(minY+maxY)*.5f, mz=(minZ+maxZ)*.5f;
                center = R.Call(rootTr, "TransformPoint", U.V(mx,my,mz));
                float hx=(maxX-minX)*.5f, hy=(maxY-minY)*.5f, hz=(maxZ-minZ)*.5f;
                radius = (float)Math.Sqrt(hx*hx + hy*hy + hz*hz);
                if (radius < 1.6f) radius = 1.6f;
                if (radius > 7f) radius = 7f;
            }
            _center = center;
            _radius = radius;

            object camPos = U.Add(center, U.V(radius*2.15f, radius*.58f, -radius*3.15f));
            _baseCam = U.Camera("GHPC_LiveAAR_V2_BaseCamera");
            object bgo = R.Get(_baseCam, "gameObject"); _created.Add(bgo);
            object bt = R.Get(bgo, "transform"); R.Set(bt, "position", camPos); R.Call(bt, "LookAt", center);
            ConfigureCamera(_baseCam, U.Mask(_aarLayer), 50f, false);

            _xrayCam = U.Camera("GHPC_LiveAAR_V2_XrayCamera");
            object xgo = R.Get(_xrayCam, "gameObject"); _created.Add(xgo);
            object xt = R.Get(xgo, "transform"); R.Set(xt, "position", camPos); R.Set(xt, "rotation", R.Get(bt, "rotation"));
            ConfigureCamera(_xrayCam, U.Mask(_xrayLayer), 51f, true);

            int both = U.Mask(_aarLayer) | U.Mask(_xrayLayer);
            object l1 = U.PointLight("GHPC_LiveAAR_V2_Key", U.Add(center, U.V(radius*1.3f, radius*1.35f, -radius*1.2f)), radius*6f, 2.8f, both);
            object l2 = U.PointLight("GHPC_LiveAAR_V2_Fill", U.Add(center, U.V(-radius*1.2f, radius*.45f, radius*1.1f)), radius*5f, 1.25f, both);
            if (l1 != null) _created.Add(l1); if (l2 != null) _created.Add(l2);
        }

        private void ConfigureCamera(object cam, int mask, float depth, bool overlay)
        {
            R.Set(cam, "nearClipPlane", .03f);
            R.Set(cam, "farClipPlane", 100f);
            R.Set(cam, "orthographic", false);
            R.Set(cam, "fieldOfView", 28f);
            R.Set(cam, "cullingMask", mask);
            R.Set(cam, "depth", depth);
            R.Set(cam, "enabled", true);
            R.Set(cam, "allowHDR", false);
            R.Set(cam, "allowMSAA", true);
            if (overlay) U.DepthOnly(cam); else U.SolidBlack(cam);

            float sw=U.ScreenW(), sh=U.ScreenH();
            float w=Math.Max(400f, Math.Min(sw*.29f, 620f));
            float h=w*9f/16f;
            float px=sw-w-18f;
            float py=96f;
            float nx=px/sw;
            float ny=1f-((py+h)/sh);
            R.Set(cam, "rect", U.Rect(nx,ny,w/sw,h/sh));
            R.Set(cam, "aspect", 16f/9f);
        }

        private void CreateShotLines(object rootShot, object vehicleTr, object stageTr)
        {
            List<object> family = CollectShotFamily(rootShot);
            List<TraceCandidate> children = new List<TraceCandidate>();

            for (int i = 0; i < family.Count; i++)
            {
                object s = family[i];
                if (object.ReferenceEquals(s, rootShot)) continue;
                TraceCandidate tc = BuildTrace(s, vehicleTr, stageTr, true);
                if (tc != null) children.Add(tc);
            }

            TraceCandidate main = BuildTrace(rootShot, vehicleTr, stageTr, false);
            if (main != null) DrawMain(main.Points);

            children.Sort(delegate(TraceCandidate a, TraceCandidate b) { return b.Score.CompareTo(a.Score); });
            int max = Math.Min(36, children.Count);
            for (int i = 0; i < max; i++) DrawChild(children[i]);
        }

        private TraceCandidate BuildTrace(object shot, object vehicleTr, object stageTr, bool child)
        {
            List<object> pts = new List<object>();
            object start = R.Get(shot, "StartPosition");
            if (start != null) pts.Add(MapPoint(vehicleTr, stageTr, start));

            bool jet = false;
            List<object> frames = R.List(R.Get(shot, "AllShotFrames"));
            for (int i = 0; i < frames.Count; i++)
            {
                object wp = R.Get(frames[i], "WorldPosition");
                if (wp != null) pts.Add(MapPoint(vehicleTr, stageTr, wp));
                if (R.Bool(frames[i], "IsJet", false)) jet = true;
            }
            object stop = R.Get(shot, "StopPosition");
            if (stop != null) pts.Add(MapPoint(vehicleTr, stageTr, stop));

            pts = CleanAndClamp(pts, child ? _radius*1.25f : _radius*1.7f);
            if (pts.Count < 2) return null;

            float length=0f;
            for (int i=1;i<pts.Count;i++)
            {
                float dx=U.X(pts[i])-U.X(pts[i-1]);
                float dy=U.Y(pts[i])-U.Y(pts[i-1]);
                float dz=U.Z(pts[i])-U.Z(pts[i-1]);
                length += (float)Math.Sqrt(dx*dx+dy*dy+dz*dz);
            }
            if (child && length < .08f) return null;

            float pen=R.Float(shot, "ActualPen", 0f);
            TraceCandidate t=new TraceCandidate();
            t.Points=pts; t.Jet=jet; t.Pen=pen; t.Score=length + Math.Min(25f, Math.Abs(pen))*.05f + (jet?4f:0f);
            return t;
        }

        private List<object> CleanAndClamp(List<object> pts, float maxRadius)
        {
            List<object> outp = new List<object>();
            for (int i=0;i<pts.Count;i++)
            {
                object p=ClampPoint(pts[i],maxRadius);
                if (p==null) continue;
                if (outp.Count>0)
                {
                    object q=outp[outp.Count-1];
                    float dx=U.X(p)-U.X(q), dy=U.Y(p)-U.Y(q), dz=U.Z(p)-U.Z(q);
                    if (dx*dx+dy*dy+dz*dz<.000025f) continue;
                }
                outp.Add(p);
            }
            return outp;
        }

        private object ClampPoint(object p, float maxRadius)
        {
            if (p==null || _center==null) return p;
            float dx=U.X(p)-U.X(_center), dy=U.Y(p)-U.Y(_center), dz=U.Z(p)-U.Z(_center);
            float m=(float)Math.Sqrt(dx*dx+dy*dy+dz*dz);
            if (m<=maxRadius || m<.0001f) return p;
            float k=maxRadius/m;
            return U.V(U.X(_center)+dx*k,U.Y(_center)+dy*k,U.Z(_center)+dz*k);
        }

        private void DrawMain(List<object> pts)
        {
            float width=Math.Max(.009f,_radius*.0045f);
            for (int i=0;i<pts.Count-1;i++)
            {
                List<object> seg=new List<object>(); seg.Add(pts[i]); seg.Add(pts[i+1]);
                object mat=(i%2==0)?_mainMat:_mainGrayMat;
                object line=U.Line("LiveAAR_MainTrace",seg,width,.92f,.94f,.96f,1f,_xrayLayer,mat);
                if (line!=null) _created.Add(line);
            }
        }

        private void DrawChild(TraceCandidate t)
        {
            float width=Math.Max(.006f,_radius*.0032f);
            object mat=t.Jet?_jetMat:_spallMat;
            float r=1f,g=t.Jet?.76f:.20f,b=t.Jet?.06f:.025f;
            object line=U.Line(t.Jet?"LiveAAR_HEATJet":"LiveAAR_Spall",t.Points,width,r,g,b,.96f,_xrayLayer,mat);
            if (line!=null) _created.Add(line);
        }

        private object MapPoint(object vehicleTr, object stageTr, object world)
        {
            object local=R.Call(vehicleTr,"InverseTransformPoint",world);
            return local==null?null:R.Call(stageTr,"TransformPoint",local);
        }

        private List<object> CollectShotFamily(object rootShot)
        {
            List<object> family=new List<object>(); family.Add(rootShot);
            try
            {
                Type at=R.Find("GHPC.AarController");
                object ac=R.GetStatic(at,"Instance");
                List<object> shots=R.List(R.Get(ac,"SessionShots"));
                for(int i=0;i<shots.Count;i++)
                {
                    object s=shots[i]; if(s==null||object.ReferenceEquals(s,rootShot)) continue;
                    object p=R.Get(s,"ParentShot"); int guard=0;
                    while(p!=null&&guard++<20)
                    {
                        if(object.ReferenceEquals(p,rootShot)){family.Add(s);break;}
                        p=R.Get(p,"ParentShot");
                    }
                }
            }
            catch { }
            return family;
        }
    }

    internal sealed class TraceCandidate
    {
        internal List<object> Points;
        internal bool Jet;
        internal float Pen;
        internal float Score;
    }

    internal sealed class AarRule
    {
        internal object AarMaterial;
        internal bool SwitchMaterials;
        internal bool Hidden;
        internal bool Xrayed;
        internal bool HighlightOnly;
        internal bool Highlighted;
    }

    internal sealed class NativeSnapshot
    {
        private readonly object _shot;
        private readonly object _vehicle;
        private readonly object _vehicleGo;
        private readonly List<object> _roots;

        internal readonly Dictionary<object,AarRule> AarRules=new Dictionary<object,AarRule>(RefEq.Instance);
        internal readonly HashSet<object> AarModelRenderers=new HashSet<object>(RefEq.Instance);
        internal readonly HashSet<object> DirectHitRenderers=new HashSet<object>(RefEq.Instance);
        internal readonly HashSet<object> NativeRenderers=new HashSet<object>(RefEq.Instance);
        internal readonly List<object> ShotFamily=new List<object>();
        internal object RecordedPose;
        internal int AarVisualCount;
        internal int AarModelCount;
        internal int HitModelCount;

        internal NativeSnapshot(object shot, object vehicle, object vehicleGo, List<object> roots)
        { _shot=shot; _vehicle=vehicle; _vehicleGo=vehicleGo; _roots=roots; }

        internal void Capture()
        {
            CollectFamily();
            RecordedPose=FindPose();
            CollectHitModels();
            CollectAarVisuals();
            CollectAarModels();
            foreach(object r in AarRules.Keys) NativeRenderers.Add(r);
            foreach(object r in AarModelRenderers) NativeRenderers.Add(r);
            foreach(object r in DirectHitRenderers) NativeRenderers.Add(r);
        }

        private void CollectFamily()
        {
            ShotFamily.Add(_shot);
            try
            {
                Type at=R.Find("GHPC.AarController");
                object ac=R.GetStatic(at,"Instance");
                List<object> shots=R.List(R.Get(ac,"SessionShots"));
                for(int i=0;i<shots.Count;i++)
                {
                    object s=shots[i]; if(s==null||object.ReferenceEquals(s,_shot)) continue;
                    object p=R.Get(s,"ParentShot"); int guard=0;
                    while(p!=null&&guard++<20)
                    {
                        if(object.ReferenceEquals(p,_shot)){ShotFamily.Add(s);break;}
                        p=R.Get(p,"ParentShot");
                    }
                }
            }
            catch { }
        }

        private object FindPose()
        {
            List<object> poses=R.List(R.Get(_shot,"UnitPoses"));
            for(int i=0;i<poses.Count;i++)
            {
                object p=poses[i]; if(p==null) continue;
                object u=R.Get(p,"Unit");
                if(object.ReferenceEquals(u,_vehicle)) return p;
                object go=u==null?null:R.Get(u,"gameObject");
                if(go!=null&&object.ReferenceEquals(go,_vehicleGo)) return p;
            }
            return null;
        }

        private void CollectHitModels()
        {
            HashSet<object> models=new HashSet<object>(RefEq.Instance);
            for(int s=0;s<ShotFamily.Count;s++)
            {
                List<object> frames=R.List(R.Get(ShotFamily[s],"AllShotFrames"));
                for(int i=0;i<frames.Count;i++)
                {
                    object hm=R.Get(frames[i],"HitModel");
                    if(hm==null||models.Contains(hm)) continue;
                    models.Add(hm);
                    List<object> rs=ModelRenderers(hm);
                    for(int j=0;j<rs.Count;j++) if(rs[j]!=null) DirectHitRenderers.Add(rs[j]);
                }
            }
            HitModelCount=models.Count;
        }

        private void CollectAarVisuals()
        {
            Type avType=R.Find("AarVisual"); if(avType==null) avType=R.Find("GHPC.AarVisual");
            if(avType==null) return;
            HashSet<object> seen=new HashSet<object>(RefEq.Instance);

            for(int ri=0;ri<_roots.Count;ri++)
            {
                List<object> vs=U.Components(_roots[ri],avType,true);
                for(int i=0;i<vs.Count;i++)
                {
                    object av=vs[i]; if(av==null||seen.Contains(av)) continue; seen.Add(av); AarVisualCount++;
                    List<object> rs=VisualRenderers(av); if(rs.Count==0) continue;
                    string mode=(R.Get(av,"RenderMode")??R.Get(av,"_renderMode")??"").ToString();
                    bool hit=false; for(int j=0;j<rs.Count;j++) if(DirectHitRenderers.Contains(rs[j])){hit=true;break;}
                    AarRule rule=new AarRule();
                    rule.AarMaterial=R.Get(av,"AarMaterial");
                    rule.SwitchMaterials=R.Bool(av,"SwitchMaterials",true);
                    rule.Hidden=R.Bool(av,"Hidden",false);
                    rule.Xrayed=mode.IndexOf("Xray",StringComparison.OrdinalIgnoreCase)>=0;
                    rule.HighlightOnly=mode.IndexOf("Highlighted",StringComparison.OrdinalIgnoreCase)>=0;
                    rule.Highlighted=hit||R.Bool(av,"Highlighted",false);
                    for(int j=0;j<rs.Count;j++) if(rs[j]!=null) AarRules[rs[j]]=rule;
                }
            }
        }

        private void CollectAarModels()
        {
            HashSet<object> models=new HashSet<object>(RefEq.Instance);
            Type amType=R.Find("AarModel");
            if(amType!=null)
            {
                for(int ri=0;ri<_roots.Count;ri++)
                {
                    List<object> ms=U.Components(_roots[ri],amType,true);
                    for(int i=0;i<ms.Count;i++) AddModel(ms[i],models);
                }
            }

            Type rackType=R.Find("GHPC.Weapons.AmmoRack"); if(rackType==null) rackType=R.Find("AmmoRack");
            if(rackType!=null)
            {
                List<object> racks=U.Components(_vehicleGo,rackType,true);
                for(int i=0;i<racks.Count;i++)
                {
                    AddModel(R.Call(racks[i],"GetAarModel"),models);
                    AddModel(R.Call(racks[i],"GetChargeAarModel"),models);
                }
            }
            AarModelCount=models.Count;
        }

        private void AddModel(object m, HashSet<object> models)
        {
            if(m==null||models.Contains(m)) return; models.Add(m);
            List<object> rs=ModelRenderers(m);
            for(int i=0;i<rs.Count;i++) if(rs[i]!=null) AarModelRenderers.Add(rs[i]);
        }

        private List<object> ModelRenderers(object m)
        {
            List<object> rs=R.List(R.Get(m,"_renderers"));
            if(rs.Count==0)
            {
                object go=R.Get(m,"gameObject");
                if(go==null){object tr=R.Get(m,"transform");go=tr==null?null:R.Get(tr,"gameObject");}
                if(go!=null) rs=U.Components(go,U.RendererType,true);
            }
            return rs;
        }

        internal static List<object> VisualRenderers(object av)
        {
            List<object> rs=R.List(R.Get(av,"_renderers"));
            if(rs.Count==0) rs=R.List(R.Get(av,"SpecificRenderers"));
            if(rs.Count==0)
            {
                object go=R.Get(av,"gameObject");
                if(go!=null) rs=U.Components(go,U.RendererType,true);
            }
            return rs;
        }
    }
}
