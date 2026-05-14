using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;

namespace GxGenie.Explorer
{
    /// <summary>
    /// Phase 1 proof-of-concept: opens a GeneXus 17 Knowledge Base from outside
    /// the IDE and lists every object found in the design model.
    ///
    /// Compile (.NET Framework 4.8):
    ///   csc.exe /target:exe /out:bin\GxExplorer.exe /r:"C:\GxSDK17\*.dll" Program.cs
    ///
    /// Run:
    ///   bin\GxExplorer.exe                            (uses default KB)
    ///   bin\GxExplorer.exe "C:\KB\Other\KB.gxw"        (custom KB path)
    /// </summary>
    internal static class Program
    {
        private const string DefaultSdkDir = @"C:\GxSDK17";
        private const string DefaultGxDir = @"C:\Program Files (x86)\GeneXus\GeneXus17U1";
        private const string DefaultKbPath = @"C:\KB\Gx17U1\SampleKB\SampleKB.gxw";

        private static readonly string SdkDir =
            Environment.GetEnvironmentVariable("GX_SDK_DIR") ?? DefaultSdkDir;

        private static readonly string GxDir =
            Environment.GetEnvironmentVariable("GX_PROGRAM_DIR") ?? DefaultGxDir;

        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            string kbPath = args.Length > 0 ? args[0] : DefaultKbPath;

            Console.WriteLine("=== GxExplorer — Fase 1 (GeneXus 17 KB walkthrough) ===");
            Console.WriteLine("SDK dir : {0}", SdkDir);
            Console.WriteLine("GX  dir : {0}", GxDir);
            Console.WriteLine("KB  path: {0}", kbPath);
            Console.WriteLine();

            if (!Directory.Exists(SdkDir))
            { Console.Error.WriteLine("ERROR: SDK dir not found: {0}", SdkDir); return 2; }
            if (!Directory.Exists(GxDir))
            { Console.Error.WriteLine("ERROR: GeneXus install dir not found: {0}", GxDir); return 2; }
            if (!File.Exists(kbPath))
            { Console.Error.WriteLine("ERROR: KB file not found: {0}", kbPath); return 2; }

            AppDomain.CurrentDomain.AssemblyResolve += ResolveArtechAssembly;

            // GeneXus locates many of its native helpers via the current
            // working directory. Point it at the install dir before opening.
            Directory.SetCurrentDirectory(GxDir);

            // Pre-load every Artech.* assembly in the SDK so type lookups by
            // full name succeed without us having to know the exact assembly.
            PreloadArtechAssemblies(SdkDir);

            try
            {
                return ExploreKb(kbPath);
            }
            catch (TargetInvocationException tie)
            {
                ReportFailure(tie.InnerException ?? tie);
                return 1;
            }
            catch (Exception ex)
            {
                ReportFailure(ex);
                return 1;
            }
        }

        private static int ExploreKb(string kbPath)
        {
            Assembly archCommon = Assembly.Load("Artech.Architecture.Common");
            Type tKnowledgeBase = archCommon.GetType("Artech.Architecture.Common.Objects.KnowledgeBase", true);
            Type tOpenOptions = archCommon.GetType("Artech.Architecture.Common.Objects.KnowledgeBase+OpenOptions", true);

            object options = Activator.CreateInstance(tOpenOptions, new object[] { kbPath });
            SetProp(options, "ReadOnly", true);
            SetProp(options, "AvoidIndexing", true);
            SetProp(options, "AvoidStartupUpdate", true);

            MethodInfo mOpen = tKnowledgeBase.GetMethod("Open", BindingFlags.Public | BindingFlags.Static);

            Console.WriteLine("Opening KB (read-only)...");
            DateTime t0 = DateTime.UtcNow;
            object kb;
            try
            {
                kb = mOpen.Invoke(null, new object[] { options });
            }
            catch (TargetInvocationException tie)
            {
                // Preserve the original stack trace from inside GeneXus so we
                // can see exactly where the failure happened (license check,
                // database access, etc.).
                if (tie.InnerException != null)
                    ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
                throw;
            }
            Console.WriteLine("KB opened in {0:N0} ms.", (DateTime.UtcNow - t0).TotalMilliseconds);
            Console.WriteLine();

            string kbName = (string)GetProp(kb, "Name");
            string kbLocation = (string)GetProp(kb, "Location");
            bool kbIsOpen = (bool)GetProp(kb, "IsOpen");

            Console.WriteLine("KB Name    : {0}", kbName);
            Console.WriteLine("KB Location: {0}", kbLocation);
            Console.WriteLine("KB IsOpen  : {0}", kbIsOpen);

            object designModel = GetProp(kb, "DesignModel");
            if (designModel == null)
            {
                Console.WriteLine("DesignModel is null — cannot enumerate objects.");
                TryClose(kb);
                return 1;
            }

            object kbObjects = GetProp(designModel, "Objects");
            if (kbObjects == null)
            {
                Console.WriteLine("DesignModel.Objects is null.");
                TryClose(kb);
                return 1;
            }

            MethodInfo getAll = kbObjects.GetType().GetMethod("GetAll", Type.EmptyTypes);
            if (getAll == null)
            {
                // GetAll might be inherited via interface — look it up that way.
                Type iface = kbObjects.GetType().GetInterface("Artech.Architecture.Common.Objects.IKBModelObjects");
                if (iface != null) getAll = iface.GetMethod("GetAll", Type.EmptyTypes);
            }
            if (getAll == null)
            {
                Console.WriteLine("Could not locate IKBModelObjects.GetAll().");
                TryClose(kb);
                return 1;
            }

            Console.WriteLine();
            Console.WriteLine("Enumerating objects...");
            DateTime tEnum = DateTime.UtcNow;
            var objects = (System.Collections.IEnumerable)getAll.Invoke(kbObjects, null);

            var byType = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            int total = 0;
            foreach (var o in objects)
            {
                total++;
                string typeName = SafeStringProp(o, "TypeName") ?? "(unknown)";
                string qname = SafeStringProp(o, "QualifiedNameString") ?? SafeStringProp(o, "Name") ?? "(no name)";
                List<string> list;
                if (!byType.TryGetValue(typeName, out list))
                {
                    list = new List<string>();
                    byType[typeName] = list;
                }
                list.Add(qname);
            }
            Console.WriteLine("Enumerated {0} objects in {1:N0} ms.", total, (DateTime.UtcNow - tEnum).TotalMilliseconds);
            Console.WriteLine();

            Console.WriteLine("--- Object counts by type ---");
            foreach (var kv in byType.OrderByDescending(x => x.Value.Count))
            {
                Console.WriteLine("  {0,5}  {1}", kv.Value.Count, kv.Key);
            }

            Console.WriteLine();
            Console.WriteLine("--- First 30 objects (sample) ---");
            int shown = 0;
            foreach (var kv in byType)
            {
                foreach (var name in kv.Value)
                {
                    Console.WriteLine("  [{0,-20}] {1}", kv.Key, name);
                    if (++shown >= 30) break;
                }
                if (shown >= 30) break;
            }

            TryClose(kb);
            Console.WriteLine();
            Console.WriteLine("=== Done. Phase 1 exploration successful. ===");
            return 0;
        }

        private static void TryClose(object kb)
        {
            try
            {
                var close = kb.GetType().GetMethod("Close", Type.EmptyTypes);
                if (close != null) close.Invoke(kb, null);
            }
            catch { /* best-effort cleanup */ }
        }

        private static void ReportFailure(Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine("!!! Operation failed !!!");
            for (Exception cur = ex; cur != null; cur = cur.InnerException)
            {
                Console.WriteLine("--- {0} ---", cur.GetType().FullName);
                Console.WriteLine(cur.Message);
                if (cur.StackTrace != null)
                {
                    var frames = cur.StackTrace.Split('\n').Take(20);
                    foreach (var f in frames) Console.WriteLine(f.TrimEnd());
                }
            }

            string typeFullName = ex.GetType().FullName ?? "";
            string msg = ex.Message ?? "";
            string stack = ex.StackTrace ?? "";

            if (typeFullName.IndexOf("License", StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("license", StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("licencia", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Console.WriteLine();
                Console.WriteLine(">>> Looks like a LICENSING error from GeneXus. <<<");
                Console.WriteLine(">>> This is expected information for Phase 1 and is not a failure of the explorer itself. <<<");
                return;
            }

            // The most common failure when driving the SDK outside the IDE is
            // that KnowledgeBase.m_KBFactory is null — the IDE normally
            // registers an IKnowledgeBaseFactory during package bootstrap and
            // KnowledgeBase.Open() crashes immediately without it.
            if (ex is NullReferenceException &&
                stack.IndexOf("KnowledgeBase.Open", StringComparison.Ordinal) >= 0)
            {
                Console.WriteLine();
                Console.WriteLine(">>> NRE inside KnowledgeBase.Open() <<<");
                Console.WriteLine("    The static field KnowledgeBase.m_KBFactory is almost certainly null.");
                Console.WriteLine("    The GeneXus IDE registers an IKnowledgeBaseFactory during its");
                Console.WriteLine("    Package Manager bootstrap; that step has not run in this host.");
                Console.WriteLine("    See FASE1_NOTES.md for the next investigation steps.");
            }
        }

        private static object GetProp(object instance, string name)
        {
            var p = instance.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            return p == null ? null : p.GetValue(instance, null);
        }

        private static string SafeStringProp(object instance, string name)
        {
            try { return (string)GetProp(instance, name); } catch { return null; }
        }

        private static void SetProp(object instance, string name, object value)
        {
            var p = instance.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p != null && p.CanWrite) p.SetValue(instance, value, null);
        }

        private static void PreloadArtechAssemblies(string dir)
        {
            foreach (var f in Directory.GetFiles(dir, "Artech*.dll"))
            {
                try { Assembly.LoadFrom(f); }
                catch (BadImageFormatException) { /* native / mixed mode: skip */ }
                catch (FileLoadException) { /* duplicate or locked: skip */ }
            }
        }

        private static Assembly ResolveArtechAssembly(object sender, ResolveEventArgs args)
        {
            string simple = new AssemblyName(args.Name).Name;
            foreach (var d in new[] { SdkDir, GxDir })
            {
                string candidate = Path.Combine(d, simple + ".dll");
                if (File.Exists(candidate))
                {
                    try { return Assembly.LoadFrom(candidate); }
                    catch { return null; }
                }
            }
            return null;
        }
    }
}
