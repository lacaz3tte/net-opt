This folder is the Windows build you actually run.

NetworkOptimizer.exe
  Windows 10/11 x64, self-contained single-file
  zapret AUTO DISCOVER only (no DNS / proxy / TLS-split search)

The zapret/ folder MUST sit next to the EXE (winws.exe, WinDivert, cygwin1.dll).
third_party/zapret in the git repo is the source copy used at publish time;
the old 1.0 exe ignores it and still searches DNS/proxy.

Run:
  NetworkOptimizer.exe
    (UAC / Administrator)
    then click AUTO DISCOVER & FIX

  or: NetworkOptimizer.exe auto
