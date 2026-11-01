"""Windows regression check. Requires pywin32 and .NET Framework's compiler."""
import ctypes
import os
from pathlib import Path
import subprocess
import sys
import uuid

import win32event
import win32process

here = Path(__file__).resolve().parent
compiler = Path(os.environ["WINDIR"]) / "Microsoft.NET/Framework64/v4.0.30319/csc.exe"
exe = here.parent / "SpaceDictation.Smoke.exe"
sources = [*here.parent.glob("*.cs"), here / "Smoke.cs"]
if not exe.exists() or max(src.stat().st_mtime_ns for src in sources) > exe.stat().st_mtime_ns:
    subprocess.run([compiler, "/nologo", "/target:exe", "/main:SpaceDictation.Smoke",
                "/platform:x64", "/codepage:65001", f"/out:{exe}", "/r:System.Windows.Forms.dll",
                "/r:System.Drawing.dll", "/r:System.Net.Http.dll", "/r:System.Security.dll",
                f"/lib:{compiler.parent / 'WPF'}", "/r:UIAutomationClient.dll", "/r:UIAutomationTypes.dll", "/r:WindowsBase.dll",
                "/r:System.Web.Extensions.dll", *sources], check=True)
if sys.argv[1:] == ["--paste-check"] or (len(sys.argv) == 3 and sys.argv[1] == "--local-clipboard"):
    # Explicit integration mode exercises OLE on the interactive desktop, without opening a window.
    sys.exit(subprocess.run([exe, *sys.argv[1:]], timeout=90).returncode)
gui = ctypes.WinDLL("user32", use_last_error=True)
gui.CreateDesktopW.restype = ctypes.c_void_p
name = "SpaceDictationTest_" + uuid.uuid4().hex
handle = gui.CreateDesktopW(name, None, None, 0, 0x02000000, None)
if not handle:
    raise ctypes.WinError(ctypes.get_last_error())
try:
    startup = win32process.STARTUPINFO()
    startup.lpDesktop = name
    process, thread, *_ = win32process.CreateProcess(str(exe), subprocess.list2cmdline([str(exe), *sys.argv[1:]]),
        None, None, True, 0, None, str(here), startup)
    if win32event.WaitForSingleObject(process, 90000) != 0:
        win32process.TerminateProcess(process, 1)
        raise TimeoutError("Smoke test did not finish")
    sys.exit(win32process.GetExitCodeProcess(process))
finally:
    gui.CloseDesktop(ctypes.c_void_p(handle))
