// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace MouseWithoutBorders.Core;

#pragma warning disable CA1416, SA1107, SA1132, SA1133, SA1501, SA1503, SA1513, SA1516

/// <summary>
/// Thin wrapper over Windows Shell's native file operation engine. The shell
/// owns recursive directory handling, progress, conflicts and its native cancel
/// UI; MWB only supplies source and destination parsing names.
/// </summary>
internal static class ShellFileOperation
{
    private const uint FOFX_SHOWELEVATIONPROMPT = 0x00010000;
    private const uint FOFX_NOCOPYSECURITYATTRIBS = 0x08000000;
    private static readonly BlockingCollection<OperationWork> StaQueue = new();
    private static readonly System.Threading.Thread StaThread = StartStaThread();

    internal static Task CopyAsync(IReadOnlyList<string> sources, string destinationDirectory, nint ownerWindow, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0) return Task.CompletedTask;
        var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        StaQueue.Add(new OperationWork(sources, destinationDirectory, ownerWindow, completion, cancellationToken), cancellationToken);
        return completion.Task;
    }

    private static System.Threading.Thread StartStaThread()
    {
        var thread = new System.Threading.Thread(() =>
        {
            foreach (var work in StaQueue.GetConsumingEnumerable())
            {
                if (work.CancellationToken.IsCancellationRequested)
                {
                    work.Completion.TrySetCanceled(work.CancellationToken);
                    continue;
                }

                try
                {
                    Copy(work.Sources, work.DestinationDirectory, work.OwnerWindow, work.CancellationToken);
                    work.Completion.TrySetResult(new object());
                }
                catch (OperationCanceledException ex)
                {
                    work.Completion.TrySetCanceled(ex.CancellationToken);
                }
                catch (Exception ex)
                {
                    work.Completion.TrySetException(ex);
                }
            }
        })
        {
            IsBackground = true,
            Name = "MWB Shell File Operation STA",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread;
    }

    private sealed record OperationWork(
        IReadOnlyList<string> Sources,
        string DestinationDirectory,
        nint OwnerWindow,
        TaskCompletionSource<object> Completion,
        CancellationToken CancellationToken);

    private static void Copy(IReadOnlyList<string> sources, string destinationDirectory, nint ownerWindow, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var operation = (IFileOperation)(object)new FileOperation();
        try
        {
            operation.SetOwnerWindow(ownerWindow);
            operation.SetOperationFlags(FOFX_SHOWELEVATIONPROMPT | FOFX_NOCOPYSECURITYATTRIBS);
            using var destination = ShellItem.FromPath(destinationDirectory);
            foreach (var sourcePath in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var source = ShellItem.FromPath(sourcePath);
                operation.CopyItem(source.Value, destination.Value, Path.GetFileName(Path.TrimEndingDirectorySeparator(sourcePath)), null);
            }

            operation.PerformOperations();
            operation.GetAnyOperationsAborted(out var aborted);
            if (aborted) throw new OperationCanceledException("Windows Shell file operation was cancelled.", cancellationToken);
        }
        finally
        {
            Marshal.FinalReleaseComObject(operation);
        }
    }

    private sealed class ShellItem : IDisposable
    {
        internal IShellItem Value { get; }

        private ShellItem(IShellItem value) => Value = value;

        internal static ShellItem FromPath(string path)
        {
            var iid = typeof(IShellItem).GUID;
            var result = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var item);
            if (result < 0) Marshal.ThrowExceptionForHR(result);
            return new ShellItem(item);
        }

        public void Dispose() => Marshal.FinalReleaseComObject(Value);
    }

    [ComImport, Guid("3AD05575-8857-4850-9277-11B85BDB8E09"), ClassInterface(ClassInterfaceType.None)]
    private sealed class FileOperation
    {
    }

    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise([MarshalAs(UnmanagedType.Interface)] object progressSink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint operationFlags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string progressMessage);
        void SetProgressDialog([MarshalAs(UnmanagedType.Interface)] object progressDialog);
        void SetProperties([MarshalAs(UnmanagedType.Interface)] object propertyArray);
        void SetOwnerWindow(nint ownerWindow);
        void ApplyPropertiesToItem(IShellItem item);
        void ApplyPropertiesToItems([MarshalAs(UnmanagedType.Interface)] object items);
        void RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Interface)] object progressSink);
        void RenameItems([MarshalAs(UnmanagedType.Interface)] object items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        void MoveItem(IShellItem item, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string newName, [MarshalAs(UnmanagedType.Interface)] object progressSink);
        void MoveItems([MarshalAs(UnmanagedType.Interface)] object items, IShellItem destinationFolder);
        void CopyItem(IShellItem item, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string newName, [MarshalAs(UnmanagedType.Interface)] object progressSink);
        void CopyItems([MarshalAs(UnmanagedType.Interface)] object items, IShellItem destinationFolder);
        void DeleteItem(IShellItem item, [MarshalAs(UnmanagedType.Interface)] object progressSink);
        void DeleteItems([MarshalAs(UnmanagedType.Interface)] object items);
        void NewItem(IShellItem destinationFolder, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string templateName, [MarshalAs(UnmanagedType.Interface)] object progressSink);
        void PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, nint bindContext, ref Guid riid, out IShellItem item);
}
