[CmdletBinding()]
param()

# Invokes the installed Explorer command with a disposable text fixture.
# The QR window must also be checked on the desktop: S_OK alone is not enough.
$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$fixtureDirectory = Join-Path $repositoryRoot 'artifacts\shell-launch-test'
New-Item -ItemType Directory -Path $fixtureDirectory -Force | Out-Null
$fixturePath = Join-Path $fixtureDirectory 'QR起動テスト.txt'
[IO.File]::WriteAllText($fixturePath, 'LocalBridge shell launch test. No personal data.', [Text.UTF8Encoding]::new($false))

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class LocalBridgeShellLaunchTest
{
    [ComImport, Guid("a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IExplorerCommand
    {
        [PreserveSig] int GetTitle(IntPtr items, out IntPtr title);
        [PreserveSig] int GetIcon(IntPtr items, out IntPtr icon);
        [PreserveSig] int GetToolTip(IntPtr items, out IntPtr tip);
        [PreserveSig] int GetCanonicalName(out Guid name);
        [PreserveSig] int GetState(IntPtr items, [MarshalAs(UnmanagedType.Bool)] bool slow, out uint state);
        [PreserveSig] int Invoke(IntPtr items, IntPtr bind);
        [PreserveSig] int GetFlags(out uint flags);
        [PreserveSig] int EnumSubCommands(out IntPtr commands);
    }

    [DllImport("ole32.dll")] static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll")] static extern void CoUninitialize();
    [DllImport("ole32.dll")] static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IExplorerCommand command);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHCreateItemFromParsingName(string path, IntPtr bind, ref Guid iid, out IntPtr item);
    [DllImport("shell32.dll")] static extern int SHCreateShellItemArrayFromShellItem(IntPtr item, ref Guid iid, out IntPtr items);

    public static void Run(string path)
    {
        int initialized = CoInitializeEx(IntPtr.Zero, 2);
        IntPtr item = IntPtr.Zero, items = IntPtr.Zero;
        IExplorerCommand command = null;
        try
        {
            Guid itemId = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");
            Guid arrayId = new Guid("b63ea76d-1f85-456f-a19c-48159efa858b");
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref itemId, out item));
            Marshal.ThrowExceptionForHR(SHCreateShellItemArrayFromShellItem(item, ref arrayId, out items));
            Guid clsid = new Guid("5e932c6e-8f1f-4d3a-ad2f-1c7dfbd890a1");
            Guid iid = typeof(IExplorerCommand).GUID;
            Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsid, IntPtr.Zero, 5, ref iid, out command));
            int result = command.Invoke(items, IntPtr.Zero);
            Console.WriteLine("Installed Explorer command Invoke: 0x{0:X8}", result);
            Marshal.ThrowExceptionForHR(result);
            Console.WriteLine("Check that the QR window displays QR起動テスト.txt on the desktop.");
        }
        finally
        {
            if (command != null) Marshal.FinalReleaseComObject(command);
            if (items != IntPtr.Zero) Marshal.Release(items);
            if (item != IntPtr.Zero) Marshal.Release(item);
            if (initialized >= 0) CoUninitialize();
        }
    }
}
'@

[LocalBridgeShellLaunchTest]::Run($fixturePath)
