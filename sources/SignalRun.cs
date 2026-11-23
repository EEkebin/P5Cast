using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Cadrivect
{
    // A self-contained endless runner doubles as a moving source for a streaming test.
    sealed class SignalRun : Form
    {
        const float Horizon = 215f;
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly Timer timer = new Timer();
        readonly HashSet<Keys> keys = new HashSet<Keys>();
        readonly Font title = new Font("Segoe UI", 28, FontStyle.Bold, GraphicsUnit.Pixel);
        readonly Font text = new Font("Segoe UI", 16, FontStyle.Regular, GraphicsUnit.Pixel);
        readonly Font small = new Font("Consolas", 13, FontStyle.Bold, GraphicsUnit.Pixel);
        readonly PointF[] stars = new PointF[56];
        readonly SoundPlayer chime = MakeSound(660, 990);
        readonly SoundPlayer miss = MakeSound(170, 100);
        readonly Color cyan = Color.FromArgb(113, 252, 221);
        double distance, lastTime, nextPadScan, noticeUntil;
        int gate, score, streak, padIndex = -1;
        float position;
        bool muted, boosting;
        string notice = "";
        Rectangle savedBounds;
        FormWindowState savedState;

        [StructLayout(LayoutKind.Sequential)]
        struct XGamepad
        {
            public ushort buttons;
            public byte leftTrigger, rightTrigger;
            public short leftX, leftY, rightX, rightY;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct XState { public uint packet; public XGamepad pad; }
        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        static extern uint ReadPad(uint index, out XState state);
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main()
        {
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.Run(new SignalRun());
        }

        SignalRun()
        {
            Text = "Signal Run | Cadrivect";
            ClientSize = new Size(960, 540);
            MinimumSize = new Size(500, 310);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(7, 13, 29);
            DoubleBuffered = true;
            ResizeRedraw = true;
            KeyPreview = true;
            Random random = new Random(16);
            for (int i = 0; i < stars.Length; ++i)
                stars[i] = new PointF(random.Next(960), random.Next(156));
            timer.Interval = 15;
            timer.Tick += UpdateFrame;
            timer.Start();
        }

        static SoundPlayer MakeSound(double startHz, double endHz)
        {
            const int rate = 22050, count = 3600;
            MemoryStream wave = new MemoryStream();
            BinaryWriter writer = new BinaryWriter(wave);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + count * 2);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(rate);
            writer.Write(rate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            writer.Write(count * 2);
            double phase = 0;
            for (int i = 0; i < count; ++i)
            {
                double t = (double)i / count;
                phase += 2 * Math.PI * (startHz + (endHz - startHz) * t) / rate;
                double envelope = Math.Min(1, t * 32) * Math.Pow(1 - t, 2);
                writer.Write((short)(Math.Sin(phase) * 5600 * envelope));
            }
            writer.Flush();
            wave.Position = 0;
            SoundPlayer player = new SoundPlayer(wave);
            player.Load();
            return player;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (!keys.Add(e.KeyCode)) return;
            if (e.KeyCode == Keys.M) muted = !muted;
            if (e.KeyCode == Keys.Escape) Close();
            if (e.KeyCode == Keys.F11)
            {
                if (FormBorderStyle == FormBorderStyle.None)
                {
                    FormBorderStyle = FormBorderStyle.Sizable;
                    Bounds = savedBounds;
                    WindowState = savedState;
                }
                else
                {
                    savedState = WindowState;
                    savedBounds = savedState == FormWindowState.Normal ? Bounds : RestoreBounds;
                    WindowState = FormWindowState.Normal;
                    FormBorderStyle = FormBorderStyle.None;
                    Bounds = Screen.FromControl(this).Bounds;
                }
            }
        }
        protected override void OnKeyUp(KeyEventArgs e) { keys.Remove(e.KeyCode); base.OnKeyUp(e); }
        protected override void OnDeactivate(EventArgs e) { keys.Clear(); base.OnDeactivate(e); }
        protected override bool IsInputKey(Keys key)
        {
            Keys k = key & Keys.KeyCode;
            return k == Keys.Left || k == Keys.Right || base.IsInputKey(key);
        }

        XGamepad PollController(double now)
        {
            XState state;
            if (padIndex >= 0)
            {
                if (ReadPad((uint)padIndex, out state) == 0) return state.pad;
                padIndex = -1;
            }
            if (now >= nextPadScan)
            {
                nextPadScan = now + 2;
                for (uint index = 0; index < 4; ++index)
                    if (ReadPad(index, out state) == 0) { padIndex = (int)index; return state.pad; }
            }
            return new XGamepad();
        }
        static float GateLane(int number)
        {
            int[] lanes = { 0, -1, 0, 1, -1, 1, 0, 1, 1, -1, 0, -1 };
            return lanes[number % lanes.Length] * .67f;
        }
        void UpdateFrame(object sender, EventArgs e)
        {
            double now = clock.Elapsed.TotalSeconds;
            double elapsed = Math.Min(.08, now - lastTime);
            lastTime = now;
            XGamepad pad = PollController(now);
            float steer = Math.Abs((int)pad.leftX) > 5500 ? pad.leftX / 32768f : 0;
            if (keys.Contains(Keys.Left) || keys.Contains(Keys.A) || (pad.buttons & 4) != 0) steer = -1;
            if (keys.Contains(Keys.Right) || keys.Contains(Keys.D) || (pad.buttons & 8) != 0) steer = 1;
            boosting = keys.Contains(Keys.Space) || (pad.buttons & 0x1000) != 0 || pad.rightTrigger > 60;
            position = Math.Max(-.94f, Math.Min(.94f, position + steer * (float)elapsed * 1.7f));
            distance += elapsed * (boosting ? .67 : .34);
            if (distance >= gate + 1)
            {
                bool hit = Math.Abs(position - GateLane(gate)) < .24;
                if (hit) { ++streak; score += 100 + 10 * Math.Min(streak - 1, 10); }
                else streak = 0;
                notice = hit ? "GATE CLEARED" : "GATE MISSED";
                noticeUntil = now + .85;
                if (!muted) { try { (hit ? chime : miss).Play(); } catch (InvalidOperationException) { } }
                ++gate;
            }
            Invalidate();
        }
        static PointF P(float x, float y) { return new PointF(x, y); }
        static void Polygon(Graphics g, Color color, params PointF[] points)
        { using (Brush brush = new SolidBrush(color)) g.FillPolygon(brush, points); }
        static void Line(Graphics g, Color color, float width, float x1, float y1, float x2, float y2)
        { using (Pen pen = new Pen(color, width)) g.DrawLine(pen, x1, y1, x2, y2); }
        static void Label(Graphics g, string s, Font font, Color color, float x, float y)
        { using (Brush brush = new SolidBrush(color)) g.DrawString(s, font, brush, x, y); }

        void Landscape(Graphics g)
        {
            using (Brush sky = new LinearGradientBrush(new Rectangle(0, 0, 960, 230),
                Color.FromArgb(11, 19, 41), Color.FromArgb(138, 108, 130), 90f))
                g.FillRectangle(sky, 0, 0, 960, 230);
            using (Brush b = new SolidBrush(Color.FromArgb(160, 211, 235, 235)))
                foreach (PointF p in stars) g.FillEllipse(b, p.X, p.Y, 1.4f, 1.4f);
            using (Brush sun = new LinearGradientBrush(new Rectangle(601, 56, 126, 126),
                Color.FromArgb(255, 223, 162), Color.FromArgb(239, 133, 101), 90f))
                g.FillEllipse(sun, 601, 56, 126, 126);
            Polygon(g, Color.FromArgb(70, 72, 101), P(0,177), P(132,128), P(190,151), P(264,116),
                P(381,173), P(480,152), P(551,156), P(624,111), P(655,132), P(721,108), P(879,162), P(960,136), P(960,228), P(0,228));
            Polygon(g, Color.FromArgb(45, 53, 80), P(0,158), P(70,185), P(192,142), P(256,173),
                P(301,161), P(408,183), P(521,169), P(630,191), P(762,144), P(807,165), P(865,144), P(960,177), P(960,229), P(0,229));
            using (Brush land = new LinearGradientBrush(new Rectangle(0, 208, 960, 332),
                Color.FromArgb(42, 64, 79), Color.FromArgb(10, 24, 34), 90f))
                g.FillRectangle(land, 0, 208, 960, 332);
            Polygon(g, Color.FromArgb(23, 37, 50), P(468, Horizon), P(492,Horizon), P(1020,540), P(-60,540));
            for (int i = 0; i < 19; ++i)
            {
                double p = ((i + distance * 4) % 19) / 19;
                float y = Horizon + (float)(p * p) * (540 - Horizon);
                Line(g, Color.FromArgb(35, 121, 175, 173), 1f, 0, y, 960, y);
            }
            foreach (float slope in new float[]{-1, -.333f, .333f, 1})
            {
                bool edge = Math.Abs(slope) > .9;
                Line(g, edge ? cyan : Color.FromArgb(68, 89, 101), edge ? 2.5f : 1,
                    480 + slope * 12, Horizon, 480 + slope * 540, 540);
            }
        }
        void DrawGate(Graphics g, int number, double timeToGate)
        {
            float scale = (float)(1 / (1 + timeToGate * 2.7));
            float y = Horizon + 234 * scale;
            float x = 480 + GateLane(number) * (12 + 387 * scale);
            float w = 80 * scale, h = 68 * scale;
            Color color = Color.FromArgb((int)(65 + 190 * scale), cyan);
            using (Pen shadow = new Pen(Color.FromArgb(40, cyan), Math.Max(2, 10 * scale)))
            using (Pen stroke = new Pen(color, Math.Max(1, 3 * scale)))
            {
                PointF[] shape = {P(x-w,y), P(x-w,y-h*.70f), P(x-w*.72f,y-h), P(x+w*.72f,y-h), P(x+w,y-h*.70f), P(x+w,y)};
                g.DrawLines(shadow, shape);
                g.DrawLines(stroke, shape);
                g.DrawLine(stroke, x-w*.3f, y-h*.88f, x+w*.3f, y-h*.88f);
            }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Color.FromArgb(7, 13, 29));
            float scale = Math.Min(ClientSize.Width / 960f, ClientSize.Height / 540f);
            float left = (ClientSize.Width - scale * 960) / 2, top = (ClientSize.Height - scale * 540) / 2;
            g.TranslateTransform(left, top);
            g.ScaleTransform(scale, scale);
            g.SetClip(new RectangleF(0,0,960,540));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Landscape(g);
            for (int i = gate + 4; i >= gate; --i) DrawGate(g, i, i + 1 - distance);

            float x = 480 + position * 399, y = 443;
            float flame = (boosting ? 40 : 19) + (float)Math.Sin(lastTime * 29) * 3;
            using (Brush shadow = new SolidBrush(Color.FromArgb(60, 0, 0, 0))) g.FillEllipse(shadow, x-35,y+7,70,12);
            Polygon(g, Color.FromArgb(238, 148, 86), P(x-10,y),P(x,y+flame),P(x+10,y));
            Polygon(g, Color.FromArgb(255, 234, 181), P(x-4,y),P(x,y+flame*.65f),P(x+4,y));
            Polygon(g, Color.FromArgb(69, 132, 156), P(x,y-30), P(x-38,y+5),P(x-16,y+1),P(x,y+6),P(x+16,y+1),P(x+38,y+5));
            Polygon(g, Color.FromArgb(184, 229, 229), P(x,y-30),P(x-13,y),P(x,y+3),P(x+13,y));
            Polygon(g, Color.FromArgb(30, 54, 78), P(x,y-19),P(x-4,y-2),P(x+4,y-2));

            Label(g, "SIGNAL RUN", title, Color.White, 28,18);
            Label(g, "FLY THROUGH THE GATES", small, cyan, 31,58);
            Label(g, "SCORE", small, cyan, 798,23);
            Label(g, score.ToString("000000"), title, Color.White, 796,41);
            using (Brush panel = new SolidBrush(Color.FromArgb(235, 10, 22, 33))) g.FillRectangle(panel,0,493,960,47);
            string controls = padIndex < 0 ? "A / D or arrows   Steer       Space   Boost" : "Left stick   Steer       A / RT   Boost";
            Label(g,controls,text,Color.FromArgb(214,228,227),26,506);
            Label(g, muted ? "M   SOUND OFF" : "M   SOUND ON", small, cyan, 813,511);
            if (lastTime < noticeUntil) Label(g, notice, small, Color.White, 429,472);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer.Dispose(); chime.Dispose(); miss.Dispose(); title.Dispose(); text.Dispose(); small.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
