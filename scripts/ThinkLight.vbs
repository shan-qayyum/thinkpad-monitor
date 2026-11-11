' Runs ThinkLight.ps1 without flashing a console window. Used by the desktop / Start Menu shortcut.
Dim sh, fso, ps1
Set sh  = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
ps1 = fso.BuildPath(fso.GetParentFolderName(WScript.ScriptFullName), "ThinkLight.ps1")
sh.Run "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & ps1 & """ toggle", 0, False
