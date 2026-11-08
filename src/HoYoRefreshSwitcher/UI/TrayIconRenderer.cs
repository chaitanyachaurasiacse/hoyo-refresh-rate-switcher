using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace HoYoRefreshSwitcher.UI
{
    /// <summary>Draws the tray icon: a coloured rounded square showing the current refresh rate.</summary>
    internal static class TrayIconRenderer
    {
        public static readonly Color DesktopColor = Color.FromArgb(38, 120, 220);  // blue: desktop rate
        public static readonly Color GamingColor = Color.FromArgb(224, 128, 0);    // amber: gaming rate
        public static readonly Color NeutralColor = Color.FromArgb(110, 110, 110); // grey: auto off / other
        public static readonly Color ErrorColor = Color.FromArgb(200, 40, 40);     // red: error

        public static Icon Create(string text, Color background)
        {
            int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
            using (var bmp = new Bitmap(size, size))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.Clear(Color.Transparent);

                    int r = Math.Max(3, size / 4);
                    using (var path = RoundedRect(new Rectangle(0, 0, size - 1, size - 1), r))
                    using (var brush = new SolidBrush(background))
                        g.FillPath(brush, path);

                    // Largest bold font whose text fits inside the square.
                    var fmt = StringFormat.GenericTypographic;
                    float em = size;
                    SizeF measured;
                    Font font = null;
                    do
                    {
                        font?.Dispose();
                        font = new Font("Segoe UI", em, FontStyle.Bold, GraphicsUnit.Pixel);
                        measured = g.MeasureString(text, font, int.MaxValue, fmt);
                        em -= 0.5f;
                    }
                    while ((measured.Width > size - 1 || measured.Height > size) && em > 5);

                    using (font)
                    {
                        float x = (size - measured.Width) / 2f;
                        float y = (size - measured.Height) / 2f;
                        g.DrawString(text, font, Brushes.White, x, y, fmt);
                    }
                }

                IntPtr hIcon = bmp.GetHicon();
                try
                {
                    using (var temp = Icon.FromHandle(hIcon))
                        return (Icon)temp.Clone();
                }
                finally
                {
                    DestroyIcon(hIcon);
                }
            }
        }

        private static GraphicsPath RoundedRect(Rectangle b, int radius)
        {
            int d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(b.X, b.Y, d, d, 180, 90);
            p.AddArc(b.Right - d, b.Y, d, d, 270, 90);
            p.AddArc(b.Right - d, b.Bottom - d, d, d, 0, 90);
            p.AddArc(b.X, b.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);
    }
}
