using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace BIMCamel.UI
{
    /// <summary>The About dialog — shown from the ribbon "About" button. Links to bimcamel.com.</summary>
    internal static class AboutDialog
    {
        public const string Url = "https://bimcamel.com";

        /// <summary>Load a PNG from the plugin's deployed Resources folder (next to the DLL).</summary>
        internal static Image? Icon(string file)
        {
            try
            {
                var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                var p = Path.Combine(dir!, "Resources", file);
                return File.Exists(p) ? Image.FromFile(p) : null;
            }
            catch { return null; }
        }

        private static void Open() { try { Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true }); } catch { } }

        public static void Show()
        {
            using var f = new Form
            {
                Text = "About BIMCamel", FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterScreen, MaximizeBox = false, MinimizeBox = false,
                ShowInTaskbar = false, ClientSize = new Size(430, 230), BackColor = Color.White
            };
            var pic = new PictureBox { Image = Icon("camel_32.png"), SizeMode = PictureBoxSizeMode.CenterImage, Location = new Point(18, 18), Size = new Size(40, 40) };
            var hdr = new Label { Text = "BIMCamel — fast Navisworks → IFC", Font = new Font("Segoe UI Semibold", 11.5f), Location = new Point(66, 18), AutoSize = true };
            var sub = new Label { Text = "Free, open-source IFC exporter (IFC4 / IFC2x3).", Location = new Point(66, 44), AutoSize = true, ForeColor = Color.DimGray, Font = new Font("Segoe UI", 8.75f) };
            var body = new Label { Text = "Visit bimcamel.com for more free BIM tools, updates and docs:", Location = new Point(20, 96), AutoSize = true, Font = new Font("Segoe UI", 9.5f) };
            var link = new LinkLabel { Text = Url, Location = new Point(20, 124), AutoSize = true, Font = new Font("Segoe UI", 10.5f, FontStyle.Bold) };
            link.LinkClicked += (_, _) => Open();
            var visit = new Button { Text = "Visit bimcamel.com", Location = new Point(20, 182), Size = new Size(150, 30), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0, 112, 192), ForeColor = Color.White, Cursor = Cursors.Hand };
            visit.Click += (_, _) => Open();
            var ok = new Button { Text = "Close", DialogResult = DialogResult.OK, Location = new Point(330, 182), Size = new Size(80, 30) };
            f.Controls.AddRange(new Control[] { pic, hdr, sub, body, link, visit, ok });
            f.AcceptButton = ok;
            f.ShowDialog();
        }
    }
}
