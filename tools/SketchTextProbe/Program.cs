// SketchTextProbe — standalone diagnostic tool.
// Opens one SLDPRT and exhaustively reports every sketch and every text segment found.
// Run:  dotnet run --project tools\SketchTextProbe -- "C:\path\to\part.SLDPRT"

using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

// COM requires STA.
var tcs = new TaskCompletionSource<int>();
var staThread = new Thread(() =>
{
    try   { tcs.SetResult(Run(args)); }
    catch (Exception ex) { tcs.SetException(ex); }
});
staThread.SetApartmentState(ApartmentState.STA);
staThread.Start();
return await tcs.Task;

static int Run(string[] args)
{
    if (args.Length < 1)
    {
        Console.WriteLine("Usage: SketchTextProbe <path-to-sldprt>");
        return 1;
    }

    string path = Path.GetFullPath(args[0]);
    Console.WriteLine($"File : {path}");
    Console.WriteLine($"Exists: {File.Exists(path)}\n");

    // ── Launch SolidWorks ─────────────────────────────────────────────────────
    ISldWorks sw;
    try
    {
        var progId = Type.GetTypeFromProgID("SldWorks.Application")
            ?? throw new InvalidOperationException("SldWorks.Application ProgID not found — is SOLIDWORKS installed?");
        sw = (ISldWorks)Activator.CreateInstance(progId)!;
        sw.Visible = false;
        Console.WriteLine("SolidWorks started OK.\n");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERROR starting SolidWorks: {ex.Message}");
        return 2;
    }

    try
    {
        // ── Open the document ─────────────────────────────────────────────────
        int warnings = 0, errors = 0;
        var doc = sw.OpenDoc6(
            path,
            (int)swDocumentTypes_e.swDocPART,
            (int)swOpenDocOptions_e.swOpenDocOptions_Silent |
            (int)swOpenDocOptions_e.swOpenDocOptions_ReadOnly,
            "",
            ref warnings,
            ref errors) as IModelDoc2;

        if (doc == null)
        {
            Console.WriteLine($"ERROR: OpenDoc6 returned null (errors={errors}, warnings={warnings})");
            return 3;
        }

        Console.WriteLine($"Document opened. errors={errors} warnings={warnings}\n");
        Console.WriteLine("══════════════════════════════════════════════════");
        Console.WriteLine("FEATURE WALK");
        Console.WriteLine("══════════════════════════════════════════════════\n");

        int featCount = 0, sketchCount = 0, textTotal = 0;

        var featsObj = doc.FeatureManager.GetFeatures(true);
        if (featsObj is object[] feats)
        {
            foreach (var obj in feats)
            {
                if (obj is not IFeature feat) continue;
                featCount++;

                string typeName = "(null)";
                try { typeName = feat.GetTypeName2() ?? "(null)"; } catch { }
                string name = "(null)";
                try { name = feat.Name ?? "(null)"; } catch { }

                Console.WriteLine($"[{featCount,3}] Feature  name='{name}'  type='{typeName}'");

                // Try direct sketch on this feature
                ProbeSketch(feat.GetSpecificFeature2() as ISketch, "      direct", ref sketchCount, ref textTotal);

                // Walk sub-features
                IFeature? sub = null;
                try { sub = feat.GetFirstSubFeature() as IFeature; } catch { }
                int subIdx = 0;
                while (sub != null)
                {
                    subIdx++;
                    string subType = "(null)";
                    try { subType = sub.GetTypeName2() ?? "(null)"; } catch { }
                    string subName = "(null)";
                    try { subName = sub.Name ?? "(null)"; } catch { }

                    Console.WriteLine($"       [{subIdx}] SubFeat  name='{subName}'  type='{subType}'");
                    ProbeSketch(sub.GetSpecificFeature2() as ISketch, "             sub", ref sketchCount, ref textTotal);

                    try { sub = sub.GetNextSubFeature() as IFeature; } catch { sub = null; }
                }
            }
        }
        else
        {
            Console.WriteLine("  GetFeatures(true) returned null or non-array.");
        }

        Console.WriteLine();
        Console.WriteLine("══════════════════════════════════════════════════");
        Console.WriteLine("SUMMARY");
        Console.WriteLine("══════════════════════════════════════════════════");
        Console.WriteLine($"  Total features walked : {featCount}");
        Console.WriteLine($"  ISketch objects found : {sketchCount}");
        Console.WriteLine($"  Text segments total   : {textTotal}");

        if (textTotal > 0)
            Console.WriteLine("\n  ✓ SKETCH TEXT DETECTED — API is working.");
        else
            Console.WriteLine("\n  ✗ No sketch text found.");

        sw.CloseDoc(path);
        return 0;
    }
    finally
    {
        try { Marshal.ReleaseComObject(sw); } catch { }
    }
}

static void ProbeSketch(ISketch? sketch, string label, ref int sketchCount, ref int textTotal)
{
    if (sketch == null) return;
    sketchCount++;

    object[]? segments = null;
    string result;
    try
    {
        segments = sketch.GetSketchTextSegments() as object[];
        result = segments == null || segments.Length == 0
            ? "GetSketchTextSegments() → (none)"
            : $"GetSketchTextSegments() → {segments.Length} text segment(s) *** FOUND ***";
    }
    catch (Exception ex)
    {
        result = $"GetSketchTextSegments() threw: {ex.Message}";
    }

    Console.WriteLine($"{label} → ISketch  {result}");

    if (segments != null)
    {
        foreach (var seg in segments)
        {
            textTotal++;
            if (seg is ISketchText st)
            {
                string text = "(could not read)";
                try { text = st.Text ?? "(null)"; } catch { }
                Console.WriteLine($"{label}   text='{text}'");
            }
        }
    }
}
