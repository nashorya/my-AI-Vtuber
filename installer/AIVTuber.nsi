Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "WinVer.nsh"
Name "AIVTuber 主播版"
OutFile "${OUTPUT}"
InstallDir "$LOCALAPPDATA\Programs\AIVTuber"
InstallDirRegKey HKCU "Software\AIVTuber" "InstallDir"
RequestExecutionLevel user
SetCompressor /SOLID lzma
SetCompressorDictSize 32
ShowInstDetails show
ShowUninstDetails show
VIProductVersion "${FILE_VERSION}"
VIAddVersionKey "ProductName" "AIVTuber 主播版"
VIAddVersionKey "FileDescription" "AIVTuber 主播版安装程序"
VIAddVersionKey "FileVersion" "${APP_VERSION}"
VIAddVersionKey "LegalCopyright" "AIVTuber contributors"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\AIVTuber.exe"
!define MUI_FINISHPAGE_RUN_TEXT "启动 AIVTuber"
!define MUI_FINISHPAGE_TEXT "安装完成。启动后选择「用邀请码注册」，输入邀请码并设置账号密码。"
!insertmacro MUI_PAGE_WELCOME
!define MUI_PAGE_CUSTOMFUNCTION_PRE DirectoryPre
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"

Var RegisteredDir

Function DirectoryPre
  ${If} $RegisteredDir != ""
    Abort
  ${EndIf}
FunctionEnd

!macro AppClosed PREFIX
Function ${PREFIX}CheckAppClosed
  IfFileExists "$INSTDIR\AIVTuber.exe" 0 done
  System::Call 'kernel32::CreateFileW(w "$INSTDIR\AIVTuber.exe", i 0x40000000, i 0, p 0, i 3, i 0x80, p 0) p.r0'
  ${If} $0 == -1
    MessageBox MB_ICONSTOP "程序文件正在使用或不可写，请关闭 AIVTuber 后重试。" /SD IDOK
    SetErrorLevel 3
    Abort
  ${EndIf}
  System::Call 'kernel32::CloseHandle(p r0)'
  done:
FunctionEnd
!macroend
!insertmacro AppClosed ""
!insertmacro AppClosed "un."

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "需要 64 位 Windows。"
    Abort
  ${EndIf}
  ${IfNot} ${AtLeastWin10}
    MessageBox MB_ICONSTOP "需要 Windows 10 或更新版本。"
    Abort
  ${EndIf}
  SetShellVarContext current
  SetRegView 32
  ReadRegStr $RegisteredDir HKCU "Software\AIVTuber" "InstallDir"
  ${If} $RegisteredDir != ""
  ${AndIf} $RegisteredDir != $INSTDIR
    MessageBox MB_ICONSTOP "升级必须使用原安装目录。要更换目录，请先卸载原版本。" /SD IDOK
    SetErrorLevel 4
    Abort
  ${EndIf}
FunctionEnd

Function EnsureWebView
  ; Microsoft's documented per-machine 32-bit registry view and per-user key.
  SetRegView 32
  ReadRegStr $0 HKLM "Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" "pv"
  ${If} $0 != ""
  ${AndIf} $0 != "0.0.0.0"
    Return
  ${EndIf}
  ReadRegStr $0 HKCU "Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" "pv"
  ${If} $0 != ""
  ${AndIf} $0 != "0.0.0.0"
    Return
  ${EndIf}
  InitPluginsDir
  SetOutPath "$PLUGINSDIR"
  File /oname=WebView2Setup.exe "${WEBVIEW_BOOTSTRAPPER}"
  DetailPrint "正在安装界面运行组件，请保持网络连接…"
  ExecWait '"$PLUGINSDIR\WebView2Setup.exe" /silent /install' $0
  ${If} $0 != 0
    MessageBox MB_ICONSTOP "界面运行组件安装失败，请检查网络后重新运行安装程序。错误码：$0" /SD IDOK
    SetErrorLevel 2
    Abort
  ${EndIf}
FunctionEnd

Section "AIVTuber" SEC_APP
  Call CheckAppClosed
  Call EnsureWebView
  ; The previous uninstaller owns its exact old payload, including retired files.
  ; It preserves unowned user data and must finish before the new files are copied.
  ${If} $RegisteredDir != ""
    IfFileExists "$INSTDIR\Uninstall.exe" +4 0
      MessageBox MB_ICONSTOP "原版本卸载程序缺失，请修复原安装后重试。" /SD IDOK
      SetErrorLevel 5
      Abort
    ExecWait '"$INSTDIR\Uninstall.exe" /S _?=$INSTDIR' $0
    ${If} $0 != 0
      SetErrorLevel 6
      Abort
    ${EndIf}
  ${EndIf}
  SetOutPath "$INSTDIR"
  SetOverwrite on
  File /r "${PAYLOAD}\*"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateDirectory "$SMPROGRAMS\AIVTuber"
  CreateShortcut "$SMPROGRAMS\AIVTuber\AIVTuber.lnk" "$INSTDIR\AIVTuber.exe"
  CreateShortcut "$SMPROGRAMS\AIVTuber\卸载.lnk" "$INSTDIR\Uninstall.exe"
  CreateShortcut "$DESKTOP\AIVTuber.lnk" "$INSTDIR\AIVTuber.exe"
  WriteRegStr HKCU "Software\AIVTuber" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AIVTuber" "DisplayName" "AIVTuber 主播版"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AIVTuber" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AIVTuber" "DisplayIcon" "$INSTDIR\AIVTuber.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AIVTuber" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AIVTuber" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AIVTuber" "QuietUninstallString" '$\"$INSTDIR\Uninstall.exe$\" /S'
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AIVTuber" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AIVTuber" "NoRepair" 1
SectionEnd

Section "Uninstall"
  SetShellVarContext current
  SetRegView 32
  Call un.CheckAppClosed
  StrCpy $9 0
  !include "${UNINSTALL_FILES}"
  ${If} $9 != 0
    MessageBox MB_ICONSTOP "部分文件无法删除，请关闭占用程序后重新卸载。卸载入口已保留。" /SD IDOK
    SetErrorLevel 7
    Abort
  ${EndIf}
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  Delete "$DESKTOP\AIVTuber.lnk"
  Delete "$SMPROGRAMS\AIVTuber\AIVTuber.lnk"
  Delete "$SMPROGRAMS\AIVTuber\卸载.lnk"
  RMDir "$SMPROGRAMS\AIVTuber"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AIVTuber"
  DeleteRegKey HKCU "Software\AIVTuber"
SectionEnd
