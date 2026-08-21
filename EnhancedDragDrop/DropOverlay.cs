using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
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
        var desired = targets
            .Where(target => target.IsVisible && !target.Bounds.IsEmpty)
            .GroupBy(target => target.Hwnd)
            .ToDictionary(group => group.Key, group => group.First());
        for (var i = forms.Count - 1; i >= 0; i--)
        {
            if (!desired.ContainsKey(forms[i].TargetHwnd))
            {
                forms[i].Close();
                forms.RemoveAt(i);
            }
        }

        foreach (var target in desired.Values)
        {
            var form = forms.FirstOrDefault(candidate => candidate.TargetHwnd == target.Hwnd);
            if (form is null)
            {
                form = new TargetForm(target, selected => hovered = selected);
                forms.Add(form);
                form.Show();
            }
            else
            {
                form.RefreshTarget(target);
            }
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
        private ExplorerTarget target;
        private readonly Action<ExplorerTarget> onHover;
        private nint zOrderAnchor;
        public nint TargetHwnd => target.Hwnd;
        public TargetForm(ExplorerTarget target, Action<ExplorerTarget> onHover)
        {
            this.target = target;
            this.onHover = onHover;
            zOrderAnchor = GetExplorerPredecessor(target.Hwnd);
            Bounds = target.Bounds;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = false;
            BackColor = Color.DeepSkyBlue;
            Opacity = 0.18;
            AllowTransparency = true;
            SetStyle(ControlStyles.Selectable, false);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            MouseEnter += (_, _) => { BackColor = Color.LimeGreen; Opacity = 0.32; onHover(target); };
            MouseLeave += (_, _) => { BackColor = Color.DeepSkyBlue; Opacity = 0.18; };
            Paint += (_, e) => { using var pen = new Pen(Color.White, 3); e.Graphics.DrawRectangle(pen, 1, 1, Width - 3, Height - 3); TextRenderer.DrawText(e.Graphics, target.FolderPath, Font, new Point(12, 12), Color.White, Color.FromArgb(160, 20, 20, 20)); };
            Shown += (_, _) => PlaceAboveExplorer();
        }

        public void RefreshTarget(ExplorerTarget next)
        {
            var boundsChanged = target.Bounds != next.Bounds;
            var nextAnchor = GetExplorerPredecessor(next.Hwnd);
            var zOrderChanged = zOrderAnchor != nextAnchor;
            target = next;
            zOrderAnchor = nextAnchor;
            if (boundsChanged)
            {
                Bounds = next.Bounds;
            }

            if (boundsChanged || zOrderChanged)
            {
                PlaceAboveExplorer();
            }
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= 0x08000000 | 0x00000080;
                return parameters;
            }
        }

        private void PlaceAboveExplorer()
        {
            if (!IsHandleCreated) return;
            var insertAfter = zOrderAnchor == Handle ? GetWindow(Handle, GW_HWNDPREV) : zOrderAnchor;
            if (insertAfter == 0) insertAfter = HWND_TOP;
            _ = SetWindowPos(Handle, insertAfter, Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        private nint GetExplorerPredecessor(nint explorerHwnd)
        {
            var predecessor = GetWindow(explorerHwnd, GW_HWNDPREV);
            if (predecessor != 0 && predecessor == Handle)
            {
                predecessor = GetWindow(predecessor, GW_HWNDPREV);
            }

            return predecessor == 0 ? HWND_TOP : predecessor;
        }

        [DllImport("user32.dll")] private static extern nint GetWindow(nint hWnd, uint command);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint hWnd, nint insertAfter, int x, int y, int width, int height, uint flags);
        private const uint GW_HWNDPREV = 3;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private static readonly nint HWND_TOP = 0;
    }
}
