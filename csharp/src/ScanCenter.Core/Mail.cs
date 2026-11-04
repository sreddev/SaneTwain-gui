using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace ScanCenter.Core;

/// <summary>Opens a new message in the default mail program with files attached (Simple MAPI).</summary>
public static class Mail
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MapiFileDescW
    {
        public uint Reserved, Flags, Position;
        public IntPtr PathName, FileName, FileType;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MapiMessageW
    {
        public uint Reserved;
        public IntPtr Subject, NoteText, MessageType, DateReceived, ConversationId;
        public uint Flags;
        public IntPtr Originator;
        public uint RecipCount;
        public IntPtr Recips;
        public uint FileCount;
        public IntPtr Files;
    }

    [DllImport("mapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "MAPISendMailW")]
    private static extern uint MAPISendMailW(IntPtr session, IntPtr uiParam, ref MapiMessageW message, uint flags, uint reserved);

    private const uint MapiLogonUi = 0x1, MapiDialog = 0x8;

    /// <summary>Blocks until the message window is handed over or closed. Returns false if no mail program answered.</summary>
    public static bool Send(IReadOnlyList<string> paths, IntPtr owner)
    {
        var allocs = new List<IntPtr>();
        IntPtr Str(string s) { var p = Marshal.StringToHGlobalUni(s); allocs.Add(p); return p; }
        int size = Marshal.SizeOf<MapiFileDescW>();
        var files = Marshal.AllocHGlobal(size * paths.Count);
        allocs.Add(files);
        try
        {
            for (int i = 0; i < paths.Count; i++)
            {
                var fd = new MapiFileDescW { Position = 0xFFFFFFFF, PathName = Str(paths[i]), FileName = Str(Path.GetFileName(paths[i])) };
                Marshal.StructureToPtr(fd, files + i * size, false);
            }
            var subject = paths.Count == 1 ? Path.GetFileNameWithoutExtension(paths[0]) : $"{paths.Count} scanned files";
            var msg = new MapiMessageW { Subject = Str(subject), NoteText = Str(""), FileCount = (uint)paths.Count, Files = files };
            uint rc = MAPISendMailW(IntPtr.Zero, owner, ref msg, MapiLogonUi | MapiDialog, 0);
            return rc is 0 or 1; // 0 = handed over, 1 = the user closed the message
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            foreach (var p in allocs) Marshal.FreeHGlobal(p);
        }
    }
}
