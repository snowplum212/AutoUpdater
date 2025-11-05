using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace Updater {
    public class ColoredProgressBar : ProgressBar {
        private Color _fillColor = Color.MediumSeaGreen;

        public ColoredProgressBar() {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        [Browsable(true)]
        [Category("Appearance")]
        [DefaultValue(typeof(Color), nameof(Color.MediumSeaGreen))]
        public Color FillColor {
            get => _fillColor;
            set {
                if (_fillColor == value) {
                    return;
                }

                _fillColor = value;
                Invalidate();
            }
        }

        protected override CreateParams CreateParams {
            get {
                var createParams = base.CreateParams;
                createParams.Style |= 0x04; // PBS_SMOOTH
                return createParams;
            }
        }

        protected override void OnPaint(PaintEventArgs e) {
            var clip = e.ClipRectangle;
            if (clip.Width <= 0 || clip.Height <= 0) {
                return;
            }

            using (var backgroundBrush = new SolidBrush(Parent?.BackColor ?? SystemColors.Control)) {
                e.Graphics.FillRectangle(backgroundBrush, clip);
            }

            var range = Maximum - Minimum;
            var percent = range <= 0 ? 0f : (float)(Value - Minimum) / range;
            var fillWidth = (int)Math.Round(clip.Width * percent, MidpointRounding.AwayFromZero);

            if (fillWidth > 0) {
                var fillRectangle = new Rectangle(clip.X, clip.Y, fillWidth, clip.Height);
                using (var fillBrush = new SolidBrush(_fillColor)) {
                    e.Graphics.FillRectangle(fillBrush, fillRectangle);
                }
            }

            var outline = new Rectangle(clip.X, clip.Y, clip.Width - 1, clip.Height - 1);
            using (var pen = new Pen(SystemColors.ControlDark)) {
                e.Graphics.DrawRectangle(pen, outline);
            }
        }

        protected override void OnResize(EventArgs e) {
            base.OnResize(e);
            Invalidate();
        }

        protected override void OnPaintBackground(PaintEventArgs pevent) {
            // Prevent flicker from base when double-buffering.
        }

        public new int Value {
            get => base.Value;
            set {
                var bounded = Math.Max(Minimum, Math.Min(Maximum, value));
                base.Value = bounded;
                Invalidate();
            }
        }

        public new int Minimum {
            get => base.Minimum;
            set {
                base.Minimum = value;
                Invalidate();
            }
        }

        public new int Maximum {
            get => base.Maximum;
            set {
                base.Maximum = value;
                Invalidate();
            }
        }

        public new void Increment(int value) {
            base.Increment(value);
            Invalidate();
        }

        public new void PerformStep() {
            base.PerformStep();
            Invalidate();
        }
    }
}
