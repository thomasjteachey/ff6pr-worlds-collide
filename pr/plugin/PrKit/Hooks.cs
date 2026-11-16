using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Last.Data.Master;
using Last.Management;

namespace PrKit
{
    // Patching the game without crashing it. Learned on FF6 (PORTING.md, "Plugin"); the engine is
    // the same in every PR, so it holds for all of them:
    //  1. The game's C++ is linked with identical-function folding: tiny methods (empty, one call)
    //     share one machine-code body with many unrelated methods. Hooking one hooks them all, the
    //     hook then reads some other object's fields, and that access violation is something .NET
    //     can't catch: the game closes (BNW v0.6.0 at startup). Patch() refuses methods whose code
    //     is that small. Hook the method that does the work, not a wrapper.
    //  2. Every hook checks it got the type it patched before touching the object:
    //     if (!Hooks.IsA<StatusDetailsController>(__instance)) return;
    //  3. Il2CppClassPointerStore<T>.NativeClassPtr is 0 until the interop type's static
    //     constructor has run; ClassOf<T> runs it first (an IsA that rejected everything silently
    //     turned BNW's hooks off for several versions).
    //  4. A hook that throws logs once (WarnOnce) and leaves the game's own result.
    //  5. Harmony can't patch MasterManager's generic methods; ordinary game methods patch fine.
    //  6. Hooks run on several game threads (stat getters do): lock what they share.
    public static class Hooks
    {
        static readonly HashSet<string> warned = new HashSet<string>();
        static readonly HashSet<string> noted = new HashSet<string>();

        // Patches type.method with hooks.prefix / hooks.postfix (method names on the hooks type).
        // A missing method, shared code or a Harmony error is logged and leaves the game alone.
        public static bool Patch(Harmony h, Type type, string method, Type hooks, string prefix = null, string postfix = null,
            Type[] args = null)
        {
            MethodInfo original;
            try { original = AccessTools.Method(type, method, args); }
            catch (Exception e)
            {
                Kit.Warn($"[patch] {type?.Name}.{method}: {e.Message}");
                return false;
            }
            if (original == null)
            {
                Kit.Warn($"[patch] {type?.Name}.{method} not found");
                return false;
            }
            return Patch(h, original, hooks, prefix, postfix);
        }

        // The same for a method found some other way (an overload picked by its parameters).
        public static bool Patch(Harmony h, MethodBase original, Type hooks, string prefix = null, string postfix = null)
        {
            var where = $"{original?.DeclaringType?.Name}.{original?.Name}";
            try
            {
                if (original == null) { Kit.Warn("[patch] no method given"); return false; }
                var tiny = TinyNativeCode(original);
                if (tiny != null)
                {
                    Kit.Warn($"[patch] {where} not patched: its native code is {tiny}, which other methods share");
                    return false;
                }
                h.Patch(original,
                    prefix: prefix == null ? null : new HarmonyMethod(hooks, prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(hooks, postfix));
                return true;
            }
            catch (Exception e)
            {
                Kit.Warn($"[patch] {where}: {e.Message}");
                return false;
            }
        }

        // A game type by its full name ("Serial.FF5.Management.JobData"), null (logged once) if this
        // game has none: for code that should run in several PRs without referencing a type one lacks.
        public static Type TypeByName(string fullName)
        {
            var t = AccessTools.TypeByName(fullName);
            if (t == null) NoteOnce("type-" + fullName, () => $"[kit] {Game.Describe()} has no {fullName}");
            return t;
        }

        // Empty or forwarding methods compile to a few bytes the linker shares between many methods.
        // Returns a description if the method's machine code is like that (just "ret", or a jump
        // first, or a ret within 6 bytes), else null.
        public static unsafe string TinyNativeCode(MethodBase method)
        {
            try
            {
                var field = Il2CppInterop.Common.Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method);
                var info = field == null ? IntPtr.Zero : (IntPtr)field.GetValue(null);
                if (info == IntPtr.Zero) return null;
                var code = *(byte**)info; // Il2CppMethodInfo.methodPointer is the first field
                if (code == null) return null;
                var hex = $"{code[0]:X2} {code[1]:X2} {code[2]:X2} {code[3]:X2} {code[4]:X2} {code[5]:X2} {code[6]:X2} {code[7]:X2}";
                if (code[0] == 0xC3) return $"empty (ret) [{hex}]";
                if (code[0] == 0xE9 || code[0] == 0xEB) return $"a jump [{hex}]";
                if (code[0] == 0x48 && code[1] == 0xFF && code[2] == 0x25) return $"a jump [{hex}]"; // jmp [rip+x]
                for (int i = 1; i < 6; i++)
                    if (code[i] == 0xC3) return $"{i + 1} bytes [{hex}]";
                return null;
            }
            catch (Exception e)
            {
                Kit.Warn($"[patch] can't inspect {method.Name}: {e.Message}");
                return null;
            }
        }

        // Is o really a T (or derived)? Every hook asks before touching its object (rule 2 above).
        public static bool IsA<T>(Il2CppObjectBase o) where T : Il2CppObjectBase
        {
            if (o == null || o.Pointer == IntPtr.Zero) return false;
            var want = ClassOf<T>.Ptr;
            if (want == IntPtr.Zero) return false;
            var klass = IL2CPP.il2cpp_object_get_class(o.Pointer);
            if (klass == want || IL2CPP.il2cpp_class_is_assignable_from(want, klass)) return true; // as TryCast<T> does
            NoteOnce("guard-" + typeof(T).Name, () =>
                $"[guard] {typeof(T).Name} hook got a {Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass))} (left alone)");
            return false;
        }

        public static IntPtr ClassPtr<T>() where T : Il2CppObjectBase => ClassOf<T>.Ptr;

        // The game class pointer for an interop type, resolved once (rule 3 above).
        static class ClassOf<T> where T : Il2CppObjectBase
        {
            internal static readonly IntPtr Ptr = Resolve();

            static IntPtr Resolve()
            {
                var p = Il2CppClassPointerStore<T>.NativeClassPtr;
                if (p == IntPtr.Zero)
                {
                    System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(T).TypeHandle);
                    p = Il2CppClassPointerStore<T>.NativeClassPtr;
                }
                if (p == IntPtr.Zero) Kit.Warn($"[guard] no game class for {typeof(T).Name}; its hooks are off");
                return p;
            }
        }

        // For the log at startup: the guard accepts the right type and rejects another.
        public static string GuardSelfTest()
        {
            var mm = MasterManager.Instance;
            if (mm == null) return "[guard] self-test: no MasterManager yet";
            return $"[guard] self-test: MasterManager accepted={IsA<MasterManager>(mm)}, as UserDataManager rejected={!IsA<UserDataManager>(mm)}";
        }

        // One warning per place: hooks run every frame and on several threads.
        public static void WarnOnce(string where, Exception e)
        {
            bool first;
            lock (warned) first = warned.Add(where);
            if (first) Kit.Warn($"[{where}] {e.GetType().Name}: {e.Message}");
        }

        public static void NoteOnce(string key, Func<string> text)
        {
            bool first;
            lock (noted) first = noted.Add(key);
            if (first) Kit.Info(text());
        }
    }
}
