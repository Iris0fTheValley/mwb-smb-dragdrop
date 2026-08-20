using System.Drawing;
using System.Runtime.Versioning;
using System.Windows.Forms;

namespace MouseWithoutBorders.EnhancedDragDrop;

/// <summary>Non-invasive per-window target overlay shown only while a remote manifest is active.</summary>
[SupportedOSPlatform("windows")]
public sealed class DropOverlay : IDisposable
{
    private readonly List<TargetForm> forms = new();
    private ExplorerTarget? hovered;
    public ExplorerTarget? HoveredTarget => hovered;

    public void ShowTargets(IReadOnlyList<ExplorerTarget> targets)
    {
        Close();
        foreach (var target in targets.Where(target => target.IsVisible && !target.Bounds.IsEmpty))
        {
            var form = new TargetForm(target, selected => hovered = selected);
            forms.Add(form);
            form.Show();
        }
    }

    public void Close()
    {
        foreach (var form in forms) form.Close();
        forms.Clear();
        hovered = null;
    }

    public void Dispose() => Close();

    private sealed class TargetForm : Form
    {
        private readonly ExplorerTarget target;
        private readonly Action<ExplorerTarget> onHover;
        public TargetForm(ExplorerTarget target, Action<ExplorerTarget> onHover)
        {
            this.target = target;
            this.onHover = onHover;
            Bounds = target.Bounds;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.DeepSkyBlue;
            Opacity = 0.18;
            AllowTransparency = true;
            SetStyle(ControlStyles.Selectable, false);
            MouseEnter += (_, _) => { BackColor = Color.LimeGreen; Opacity = 0.32; onHover(target); };
            MouseLeave += (_, _) => { BackColor = Color.DeepSkyBlue; Opacity = 0.18; };
            Paint += (_, e) => { using var pen = new Pen(Color.White, 3); e.Graphics.DrawRectangle(pen, 1, 1, Width - 3, Height - 3); TextRenderer.DrawText(e.Graphics, target.FolderPath, Font, new Point(12, 12), Color.White, Color.FromArgb(160, 20, 20, 20)); };
        }
    }
}
