Unicode true
RequestExecutionLevel user
!ifndef VERSION
!define VERSION "0.1.11"
!endif
Name "인천교육 보도자료 작성기"
OutFile "${OUTPUT}"
InstallDir "$LOCALAPPDATA\Programs\PressReleaseWriter"
SetCompressor /SOLID lzma
!include "MUI2.nsh"
!include "x64.nsh"
!define MUI_ICON "..\Desktop\Assets\press-release.ico"
!define MUI_UNICON "..\Desktop\Assets\press-release.ico"
!define MUI_FINISHPAGE_RUN "$INSTDIR\PressReleaseWriter.exe"
!define MUI_FINISHPAGE_RUN_TEXT "보도자료 작성기 실행"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "Korean"

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "Windows 11 x64 환경용 설치 파일입니다."
    Abort
  ${EndIf}
FunctionEnd

Section "설치"
  SetShellVarContext current
  SetOutPath "$INSTDIR"
  File "${PAYLOAD}/PressReleaseWriter.exe"
  File "${PAYLOAD}/사용안내.md"
  SetOutPath "$INSTDIR\notices"
  File "${PAYLOAD}/notices/*.txt"
  SetOutPath "$INSTDIR"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateDirectory "$SMPROGRAMS\보도자료 작성기"
  CreateShortcut "$SMPROGRAMS\보도자료 작성기\보도자료 작성기.lnk" "$INSTDIR\PressReleaseWriter.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\PressReleaseWriter" "DisplayName" "인천교육 보도자료 작성기"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\PressReleaseWriter" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\PressReleaseWriter" "DisplayIcon" "$INSTDIR\PressReleaseWriter.exe,0"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\PressReleaseWriter" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\PressReleaseWriter" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\PressReleaseWriter" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\PressReleaseWriter" "NoRepair" 1
SectionEnd

Section "Uninstall"
  SetShellVarContext current
  Delete "$SMPROGRAMS\보도자료 작성기\보도자료 작성기.lnk"
  RMDir "$SMPROGRAMS\보도자료 작성기"
  Delete "$INSTDIR\PressReleaseWriter.exe"
  Delete "$INSTDIR\Uninstall.exe"
  Delete "$INSTDIR\사용안내.md"
  Delete "$INSTDIR\notices\dotnet-license.txt"
  Delete "$INSTDIR\notices\dotnet-third-party.txt"
  Delete "$INSTDIR\notices\pdfpig-license.txt"
  Delete "$INSTDIR\notices\nsis-copyright.txt"
  RMDir "$INSTDIR\notices"
  RMDir "$INSTDIR"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\PressReleaseWriter"
SectionEnd
