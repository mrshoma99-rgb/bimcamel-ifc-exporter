using System;
using Autodesk.Navisworks.Api.Automation;

namespace BIMCamel.Tests
{
    /// <summary>
    /// Opens a model in a hidden Navisworks and runs the HeadlessExport add-in on it.
    /// Usage: Runner &lt;model.nwd&gt; &lt;plugin.dll&gt; &lt;out.ifc&gt; [mergeAbove] [IFC4|IFC2X3] [minSizeMm] [simplifyMm]
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] a)
        {
            if (a.Length < 3) { Console.Error.WriteLine("usage: Runner <model.nwd> <plugin.dll> <out.ifc> [mergeAbove] [schema] [minSizeMm] [simplifyMm]"); return 2; }
            var app = new NavisworksApplication();
            try
            {
                app.DisableProgress();
                app.OpenFile(a[0]);
                app.AddPluginAssembly(a[1]);
                app.ExecuteAddInPlugin("HeadlessExport.BCTST", a[2], a.Length > 3 ? a[3] : "16", a.Length > 4 ? a[4] : "IFC4", a.Length > 5 ? a[5] : "0", a.Length > 6 ? a[6] : "0");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
            finally { app.Dispose(); }
        }
    }
}
