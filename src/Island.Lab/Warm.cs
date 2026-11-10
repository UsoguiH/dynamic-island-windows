using System.Reflection;
using System.Runtime.CompilerServices;

namespace Island.Lab;

/// <summary>
/// Compiles the island's code ahead of its first use, quietly, on a background thread.
/// .NET compiles each method the first time it runs. For a screen you open for the first time since the
/// island started (the tab library, the "describe a tab" line, an agent's console…) that is hundreds of
/// methods at once, on the thread that draws: the very first entrance of every screen stuttered. Doing it
/// here costs a second or two of idle processor after start-up and the entrances find their code ready.
/// </summary>
public static class Warm
{
    public static void Start()
    {
        if (Environment.GetEnvironmentVariable("ISLAND_NOWARM") == "1") return;
        new Thread(Run) { IsBackground = true, Name = "Warm", Priority = ThreadPriority.Lowest }.Start();
    }

    static void Run()
    {
        Thread.Sleep(1200); // let the island appear first
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int n = 0;
        Assembly[] all =
        [
            typeof(Warm).Assembly,
            typeof(Vortice.Direct2D1.ID2D1DeviceContext).Assembly,
            typeof(Vortice.DirectWrite.IDWriteFactory).Assembly,
            typeof(Vortice.Direct3D11.ID3D11Device).Assembly,
            typeof(Vortice.DXGI.IDXGISwapChain1).Assembly,
            typeof(Vortice.DirectComposition.IDCompositionDevice).Assembly,
            typeof(SharpGen.Runtime.ComObject).Assembly,
        ];
        const BindingFlags Every = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        foreach (var asm in all.Distinct())
        {
            Type[] types;
            try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray()!; } catch { continue; }
            foreach (var t in types)
            {
                if (t.ContainsGenericParameters || t.IsInterface) continue;
                try
                {
                    foreach (var m in t.GetMethods(Every)) n += Prepare(m);
                    foreach (var c in t.GetConstructors(Every)) n += Prepare(c);
                }
                catch { }
            }
        }
        Diag.Log($"warm: {n} methods ready in {sw.ElapsedMilliseconds} ms");
    }

    static int Prepare(MethodBase m)
    {
        if (m.IsAbstract || m.ContainsGenericParameters || (m.MethodImplementationFlags & MethodImplAttributes.InternalCall) != 0) return 0;
        try { RuntimeHelpers.PrepareMethod(m.MethodHandle); return 1; } catch { return 0; }
    }
}
