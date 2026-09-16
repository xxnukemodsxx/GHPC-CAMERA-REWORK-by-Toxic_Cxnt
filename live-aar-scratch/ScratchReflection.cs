using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace GHPCNativeLiveAAR
{
    internal static class R
    {
        internal static Type Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            Type t = Type.GetType(name, false);
            if (t != null) return t;
            Assembly[] a = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < a.Length; i++)
            {
                try
                {
                    t = a[i].GetType(name, false);
                    if (t != null) return t;
                    Type[] ts = a[i].GetTypes();
                    string simple = name;
                    int dot = simple.LastIndexOf('.');
                    if (dot >= 0 && dot + 1 < simple.Length) simple = simple.Substring(dot + 1);
                    for (int j = 0; j < ts.Length; j++)
                    {
                        if (ts[j].FullName == name || ts[j].Name == name || ts[j].Name == simple) return ts[j];
                    }
                }
                catch { }
            }
            return null;
        }

        internal static object Get(object o, string n)
        {
            if (o == null) return null;
            Type t = o.GetType();
            BindingFlags f = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                PropertyInfo p = t.GetProperty(n, f);
                if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(o, null);
            }
            catch { }
            try
            {
                FieldInfo x = t.GetField(n, f);
                if (x != null) return x.GetValue(o);
            }
            catch { }
            return null;
        }

        internal static bool Set(object o, string n, object v)
        {
            if (o == null) return false;
            Type t = o.GetType();
            BindingFlags f = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                PropertyInfo p = t.GetProperty(n, f);
                if (p != null && p.CanWrite)
                {
                    p.SetValue(o, Coerce(v, p.PropertyType), null);
                    return true;
                }
            }
            catch { }
            try
            {
                FieldInfo x = t.GetField(n, f);
                if (x != null)
                {
                    x.SetValue(o, Coerce(v, x.FieldType));
                    return true;
                }
            }
            catch { }
            return false;
        }

        internal static object GetStatic(Type t, string n)
        {
            if (t == null) return null;
            BindingFlags f = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                PropertyInfo p = t.GetProperty(n, f);
                if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(null, null);
            }
            catch { }
            try
            {
                FieldInfo x = t.GetField(n, f);
                if (x != null) return x.GetValue(null);
            }
            catch { }
            return null;
        }

        internal static bool SetStatic(Type t, string n, object v)
        {
            if (t == null) return false;
            BindingFlags f = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                PropertyInfo p = t.GetProperty(n, f);
                if (p != null && p.CanWrite)
                {
                    p.SetValue(null, Coerce(v, p.PropertyType), null);
                    return true;
                }
            }
            catch { }
            try
            {
                FieldInfo x = t.GetField(n, f);
                if (x != null)
                {
                    x.SetValue(null, Coerce(v, x.FieldType));
                    return true;
                }
            }
            catch { }
            return false;
        }

        internal static object Call(object o, string n, params object[] args)
        {
            return o == null ? null : Invoke(o.GetType(), o, n, args, false);
        }

        internal static object CallStatic(Type t, string n, params object[] args)
        {
            return t == null ? null : Invoke(t, null, n, args, true);
        }

        private static object Invoke(Type t, object target, string n, object[] args, bool stat)
        {
            BindingFlags f = (stat ? BindingFlags.Static : BindingFlags.Instance) | BindingFlags.Public | BindingFlags.NonPublic;
            MethodInfo[] ms;
            try { ms = t.GetMethods(f); } catch { return null; }

            for (int i = 0; i < ms.Length; i++)
            {
                MethodInfo m = ms[i];
                if (m.Name != n) continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length != args.Length) continue;
                object[] a = new object[args.Length];
                bool ok = true;
                for (int j = 0; j < args.Length; j++)
                {
                    try { a[j] = Coerce(args[j], ps[j].ParameterType); }
                    catch { ok = false; break; }
                }
                if (!ok) continue;
                try { return m.Invoke(target, a); }
                catch (TargetInvocationException e) { if (e.InnerException != null) throw e.InnerException; throw; }
                catch { }
            }
            return null;
        }

        private static object Coerce(object v, Type t)
        {
            if (v == null) return null;
            if (t.IsInstanceOfType(v)) return v;
            if (t.IsEnum)
            {
                if (v is string) return Enum.Parse(t, (string)v, true);
                return Enum.ToObject(t, Convert.ToInt32(v, CultureInfo.InvariantCulture));
            }
            if (t.IsPrimitive || t == typeof(decimal))
                return Convert.ChangeType(v, t, CultureInfo.InvariantCulture);
            return v;
        }

        internal static List<object> List(object e)
        {
            List<object> r = new List<object>();
            IEnumerable en = e as IEnumerable;
            if (en == null) return r;
            try { foreach (object x in en) r.Add(x); } catch { }
            return r;
        }

        internal static bool Bool(object o, string n, bool d)
        {
            object v = Get(o, n);
            try { return v == null ? d : Convert.ToBoolean(v, CultureInfo.InvariantCulture); } catch { return d; }
        }

        internal static int Int(object o, string n, int d)
        {
            object v = Get(o, n);
            try { return v == null ? d : Convert.ToInt32(v, CultureInfo.InvariantCulture); } catch { return d; }
        }

        internal static float Float(object o, string n, float d)
        {
            object v = Get(o, n);
            try { return v == null ? d : Convert.ToSingle(v, CultureInfo.InvariantCulture); } catch { return d; }
        }

        internal static string Str(object o, string n)
        {
            object v = Get(o, n);
            try { return v == null ? null : (v as string ?? v.ToString()); } catch { return null; }
        }
    }

    internal sealed class RefEq : IEqualityComparer<object>
    {
        internal static readonly RefEq Instance = new RefEq();
        public new bool Equals(object a, object b) { return object.ReferenceEquals(a, b); }
        public int GetHashCode(object o) { return o == null ? 0 : RuntimeHelpers.GetHashCode(o); }
    }

    internal static class U
    {
        private static bool _ok;
        private static Type _go, _obj, _vec3, _quat, _rect, _material, _shader, _line, _camera, _light, _lightType, _renderer, _meshRenderer, _meshFilter, _skin;
        private static ConstructorInfo _goCtor, _vCtor, _rectCtor;

        internal static Type RendererType { get { Init(); return _renderer; } }
        internal static Type MeshRendererType { get { Init(); return _meshRenderer; } }
        internal static Type MeshFilterType { get { Init(); return _meshFilter; } }
        internal static Type SkinType { get { Init(); return _skin; } }
        internal static Type MaterialType { get { Init(); return _material; } }

        internal static void Init()
        {
            if (_ok) return;
            _go = R.Find("UnityEngine.GameObject");
            _obj = R.Find("UnityEngine.Object");
            _vec3 = R.Find("UnityEngine.Vector3");
            _quat = R.Find("UnityEngine.Quaternion");
            _rect = R.Find("UnityEngine.Rect");
            _material = R.Find("UnityEngine.Material");
            _shader = R.Find("UnityEngine.Shader");
            _line = R.Find("UnityEngine.LineRenderer");
            _camera = R.Find("UnityEngine.Camera");
            _light = R.Find("UnityEngine.Light");
            _lightType = R.Find("UnityEngine.LightType");
            _renderer = R.Find("UnityEngine.Renderer");
            _meshRenderer = R.Find("UnityEngine.MeshRenderer");
            _meshFilter = R.Find("UnityEngine.MeshFilter");
            _skin = R.Find("UnityEngine.SkinnedMeshRenderer");
            _goCtor = _go == null ? null : _go.GetConstructor(new Type[] { typeof(string) });
            _vCtor = _vec3 == null ? null : _vec3.GetConstructor(new Type[] { typeof(float), typeof(float), typeof(float) });
            _rectCtor = _rect == null ? null : _rect.GetConstructor(new Type[] { typeof(float), typeof(float), typeof(float), typeof(float) });
            _ok = true;
        }

        internal static object GO(string n) { Init(); return _goCtor.Invoke(new object[] { n }); }
        internal static object AddComponent(object go, Type t) { return R.Call(go, "AddComponent", t); }
        internal static object GetComponent(object go, Type t) { return go == null || t == null ? null : R.Call(go, "GetComponent", t); }
        internal static List<object> Components(object go, Type t, bool inactive) { return go == null || t == null ? new List<object>() : R.List(R.Call(go, "GetComponentsInChildren", t, inactive)); }
        internal static object V(float x, float y, float z) { Init(); return _vCtor.Invoke(new object[] { x, y, z }); }
        internal static object Rect(float x, float y, float w, float h) { Init(); return _rectCtor.Invoke(new object[] { x, y, w, h }); }
        internal static object QIdentity() { Init(); return R.GetStatic(_quat, "identity"); }
        internal static float X(object v) { return R.Float(v, "x", 0); }
        internal static float Y(object v) { return R.Float(v, "y", 0); }
        internal static float Z(object v) { return R.Float(v, "z", 0); }
        internal static float Mag(object v) { float x = X(v), y = Y(v), z = Z(v); return (float)Math.Sqrt(x*x + y*y + z*z); }
        internal static object Add(object a, object b) { return V(X(a)+X(b), Y(a)+Y(b), Z(a)+Z(b)); }
        internal static object Sub(object a, object b) { return V(X(a)-X(b), Y(a)-Y(b), Z(a)-Z(b)); }
        internal static object Mul(object a, float k) { return V(X(a)*k, Y(a)*k, Z(a)*k); }
        internal static object DivScale(object a, object b)
        {
            float bx = Math.Abs(X(b)) < .0001f ? 1f : X(b);
            float by = Math.Abs(Y(b)) < .0001f ? 1f : Y(b);
            float bz = Math.Abs(Z(b)) < .0001f ? 1f : Z(b);
            return V(X(a)/bx, Y(a)/by, Z(a)/bz);
        }
        internal static object RelativeRotation(object rootWorldRotation, object childWorldRotation)
        {
            Init();
            object inv = R.CallStatic(_quat, "Inverse", rootWorldRotation);
            object rel = inv == null ? null : R.CallStatic(_quat, "op_Multiply", inv, childWorldRotation);
            return rel ?? childWorldRotation;
        }
        internal static int Mask(int layer) { return layer >= 0 && layer < 32 ? 1 << layer : 0; }

        internal static void Destroy(object o)
        {
            if (o == null) return;
            Init();
            try
            {
                MethodInfo m = _obj.GetMethod("Destroy", BindingFlags.Public | BindingFlags.Static, null, new Type[] { _obj }, null);
                if (m != null) m.Invoke(null, new object[] { o });
            }
            catch { }
        }

        internal static float ScreenW()
        {
            Type t = R.Find("UnityEngine.Screen");
            object v = R.GetStatic(t, "width");
            try { return Convert.ToSingle(v, CultureInfo.InvariantCulture); } catch { return 1920; }
        }

        internal static float ScreenH()
        {
            Type t = R.Find("UnityEngine.Screen");
            object v = R.GetStatic(t, "height");
            try { return Convert.ToSingle(v, CultureInfo.InvariantCulture); } catch { return 1080; }
        }

        internal static object CloneMaterial(object m)
        {
            if (m == null) return null;
            Init();
            try
            {
                ConstructorInfo c = _material.GetConstructor(new Type[] { _material });
                return c == null ? null : c.Invoke(new object[] { m });
            }
            catch { return null; }
        }

        internal static Array MaterialArray(object m, int n)
        {
            Init();
            Array a = Array.CreateInstance(_material, Math.Max(1, n));
            for (int i = 0; i < a.Length; i++) a.SetValue(m, i);
            return a;
        }

        internal static Array TintedMaterials(object matsObj, float r, float g, float b, float a)
        {
            Init();
            Array src = matsObj as Array;
            if (src == null) return null;
            Array dst = Array.CreateInstance(_material, src.Length);
            for (int i = 0; i < src.Length; i++)
            {
                object original = src.GetValue(i);
                object clone = CloneMaterial(original);
                if (clone == null) clone = original;
                if (clone != null) Color(clone, r, g, b, a);
                dst.SetValue(clone, i);
            }
            return dst;
        }

        internal static Array SolidMaterials(int count, float r, float g, float b, float a)
        {
            Init();
            object sh = Shader("Unlit/Color");
            if (sh == null) sh = Shader("Sprites/Default");
            object m = NewMaterial(sh);
            if (m == null) return null;
            Color(m, r, g, b, a);
            return MaterialArray(m, Math.Max(1, count));
        }

        internal static int ArrayLen(object a) { Array x = a as Array; return x == null ? 0 : x.Length; }

        internal static object Shader(string name)
        {
            Init();
            return R.CallStatic(_shader, "Find", name);
        }

        internal static object NewMaterial(object shader)
        {
            if (shader == null) return null;
            Init();
            try
            {
                ConstructorInfo c = _material.GetConstructor(new Type[] { _shader });
                return c == null ? null : c.Invoke(new object[] { shader });
            }
            catch { return null; }
        }

        internal static object ColorObject(float r, float g, float b, float a)
        {
            Type ct = R.Find("UnityEngine.Color");
            if (ct == null) return null;
            try
            {
                ConstructorInfo c = ct.GetConstructor(new Type[] { typeof(float), typeof(float), typeof(float), typeof(float) });
                return c == null ? null : c.Invoke(new object[] { r, g, b, a });
            }
            catch { return null; }
        }

        internal static void Color(object m, float r, float g, float b, float a)
        {
            if (m == null) return;
            object co = ColorObject(r, g, b, a);
            if (co == null) return;
            try { R.Set(m, "color", co); } catch { }
            try { R.Call(m, "SetColor", "_Color", co); } catch { }
            try { R.Call(m, "SetColor", "_BaseColor", co); } catch { }
        }

        internal static object Line(string name, List<object> points, float width, float r, float g, float b, float a, int layer)
        {
            Init();
            if (points == null || points.Count < 2) return null;
            object go = GO(name);
            R.Set(go, "layer", layer);
            object lr = AddComponent(go, _line);
            R.Set(lr, "positionCount", points.Count);
            R.Set(lr, "useWorldSpace", true);
            R.Set(lr, "startWidth", width);
            R.Set(lr, "endWidth", width);
            for (int i = 0; i < points.Count; i++) R.Call(lr, "SetPosition", i, points[i]);

            object sh = Shader("Unlit/Color");
            if (sh == null) sh = Shader("Sprites/Default");
            object mat = NewMaterial(sh);
            if (mat != null)
            {
                Color(mat, r, g, b, a);
                R.Set(lr, "material", mat);
            }
            object co = ColorObject(r, g, b, a);
            if (co != null)
            {
                R.Set(lr, "startColor", co);
                R.Set(lr, "endColor", co);
            }
            R.Set(lr, "numCapVertices", 2);
            return go;
        }

        internal static object Camera(string name)
        {
            Init();
            object go = GO(name);
            object cam = AddComponent(go, _camera);
            return cam;
        }

        internal static void SolidBlack(object cam)
        {
            try
            {
                object co = ColorObject(.015f, .018f, .022f, 1f);
                if (co != null) R.Set(cam, "backgroundColor", co);
                R.Set(cam, "clearFlags", 2);
            }
            catch { }
        }

        internal static void DepthOnly(object cam)
        {
            try { R.Set(cam, "clearFlags", 3); } catch { }
        }

        internal static object BackdropQuad(string name, object pos, object lookAt, float size, int layer)
        {
            Init();
            Type primitiveType = R.Find("UnityEngine.PrimitiveType");
            if (primitiveType == null) return null;

            object quadEnum;
            try { quadEnum = Enum.Parse(primitiveType, "Quad"); }
            catch { return null; }

            object go = R.CallStatic(_go, "CreatePrimitive", quadEnum);
            if (go == null) return null;

            R.Set(go, "name", name);
            R.Set(go, "layer", layer);
            object tr = R.Get(go, "transform");
            R.Set(tr, "position", pos);
            R.Set(tr, "localScale", V(size, size, 1f));
            R.Call(tr, "LookAt", lookAt);

            Type colliderType = R.Find("UnityEngine.Collider");
            object col = colliderType == null ? null : GetComponent(go, colliderType);
            if (col != null) Destroy(col);

            object mr = GetComponent(go, _meshRenderer);
            if (mr != null)
            {
                object sh = Shader("Unlit/Color");
                if (sh == null) sh = Shader("Sprites/Default");
                object mat = NewMaterial(sh);
                if (mat != null)
                {
                    Color(mat, .008f, .010f, .012f, 1f);
                    R.Set(mr, "sharedMaterial", mat);
                    R.Set(mr, "material", mat);
                }
                R.Set(mr, "enabled", true);
            }

            return go;
        }

        internal static object PointLight(string name, object pos, float range, float intensity, int mask)
        {
            Init();
            object go = GO(name);
            object tr = R.Get(go, "transform");
            R.Set(tr, "position", pos);
            object li = AddComponent(go, _light);
            try { R.Set(li, "type", Enum.Parse(_lightType, "Point")); } catch { }
            R.Set(li, "range", range);
            R.Set(li, "intensity", intensity);
            R.Set(li, "cullingMask", mask);
            return go;
        }
    }
}
