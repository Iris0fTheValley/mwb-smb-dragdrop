// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Drawing;
using System.Windows.Forms;

namespace MouseWithoutBorders.Core;

#pragma warning disable SA1107, SA1501, SA1503, SA1513, SA1515

internal enum EnhancedDragConflictPolicy
{
    Ask = 0,
    Replace = 1,
    Skip = 2,
    KeepBoth = 3,
    Cancel = 4,
}

internal readonly record struct EnhancedDragProgress(long TotalBytes, long TransferredBytes, int TotalItems, int CompletedItems, double BytesPerSecond)
{
    internal int Percent => TotalBytes <= 0 ? 0 : Math.Clamp((int)(TransferredBytes * 100L / TotalBytes), 0, 100);
}

internal sealed class EnhancedDragProgressForm : System.Windows.Forms.Form
{
    private readonly Label summary = new();
    private readonly Label detail = new();
    private readonly ProgressBar progress = new();
    private readonly Button cancel = new();
    private readonly Action cancelAction;
    private bool closing;

    internal EnhancedDragProgressForm(Action cancelAction)
    {
        this.cancelAction = cancelAction;
        Text = "Mouse Without Borders - File Transfer / 文件传输";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(430, 132);
        MinimizeBox = false;
        MaximizeBox = false;
        summary.SetBounds(14, 12, 400, 24);
        summary.Font = new Font(Font, FontStyle.Bold);
        detail.SetBounds(14, 40, 400, 22);
        progress.SetBounds(14, 68, 400, 18);
        progress.Minimum = 0;
        progress.Maximum = 100;
        cancel.Text = "Cancel / 取消";
        cancel.SetBounds(315, 96, 99, 26);
        cancel.Click += (_, _) => this.cancelAction();
        Controls.AddRange(new Control[] { summary, detail, progress, cancel });
        FormClosing += (_, e) =>
        {
            if (!closing)
            {
                e.Cancel = true;
                this.cancelAction();
            }
        };
    }

    internal void Report(EnhancedDragProgress value, string item = "")
    {
        if (IsDisposed) return;
        void Update()
        {
            if (IsDisposed)
            {
                return;
            }
            summary.Text = $"{value.Percent}%  {value.CompletedItems}/{value.TotalItems} items / 个文件";
            detail.Text = $"{FormatBytes(value.TransferredBytes)} / {FormatBytes(value.TotalBytes)}    {FormatBytes((long)value.BytesPerSecond)}/s";
            progress.Value = value.Percent;
            if (!string.IsNullOrWhiteSpace(item)) Text = $"{item} - Mouse Without Borders / 文件传输";
        }
        if (InvokeRequired) BeginInvoke((Action)Update); else Update();
    }

    internal void Complete(string message)
    {
        if (IsDisposed) return;
        void Update()
        {
            if (IsDisposed)
            {
                return;
            }
            closing = true;
            Close();
        }
        if (InvokeRequired) BeginInvoke((Action)Update); else Update();
    }

    private static string FormatBytes(long value)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double amount = Math.Max(0, value);
        var unit = 0;
        while (amount >= 1024 && unit < units.Length - 1)
        {
            amount /= 1024;
            unit++;
        }
        return $"{amount:0.##} {units[unit]}";
    }
}

internal static class EnhancedDragConflictDialog
{
    internal static (EnhancedDragConflictPolicy Policy, bool ApplyToAll) Show(string name, bool isDirectory)
    {
        using var dialog = new System.Windows.Forms.Form
        {
            Text = "File conflict / 文件冲突",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new Size(430, 190),
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
        };
        var label = new Label { Text = $"{(isDirectory ? "Folder / 文件夹" : "File / 文件")}: {name}\r\nAlready exists. Choose an action.\r\n已存在，请选择操作。", AutoSize = false, Bounds = new Rectangle(16, 14, 395, 58) };
        var apply = new CheckBox { Text = "Apply to all / 应用到全部", Bounds = new Rectangle(16, 78, 220, 24) };
        var replace = new Button { Text = "Replace / 替换", DialogResult = DialogResult.Yes, Bounds = new Rectangle(16, 126, 96, 30) };
        var skip = new Button { Text = "Skip / 跳过", DialogResult = DialogResult.No, Bounds = new Rectangle(120, 126, 96, 30) };
        var keep = new Button { Text = "Keep both / 保留两者", DialogResult = DialogResult.Retry, Bounds = new Rectangle(224, 126, 105, 30) };
        var cancel = new Button { Text = "Cancel / 取消", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(337, 126, 78, 30) };
        dialog.Controls.AddRange(new Control[] { label, apply, replace, skip, keep, cancel });
        dialog.AcceptButton = replace;
        dialog.CancelButton = cancel;
        var result = dialog.ShowDialog();
        var policy = result switch
        {
            DialogResult.Yes => EnhancedDragConflictPolicy.Replace,
            DialogResult.No => EnhancedDragConflictPolicy.Skip,
            DialogResult.Retry => EnhancedDragConflictPolicy.KeepBoth,
            _ => EnhancedDragConflictPolicy.Cancel,
        };
        return (policy, apply.Checked);
    }
}
