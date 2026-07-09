; Inno Setup script for SolidWorks Part Matcher.
;
; Requires Inno Setup 6.3.0 or later: the x64compatible architecture identifier used
; below was introduced in 6.3.0 and does not compile on 6.0-6.2.
;
; Build it with installer\build_installer.ps1 — do not run ISCC directly, as the
; script passes the version and source folder in via /D.
;
; The installed layout is identical to the zip layout (the exe with a sibling
; viewer\ folder), because the app locates its bundled OpenCASCADE tools by
; walking up from its own directory looking for viewer\<tool>.exe. Keeping the
; layout means the installer needs no code changes at all.
;
; Installing per-user by default (no UAC prompt). The user can still choose a
; machine-wide install, which elevates. Either is safe: the app writes its
; database and logs to %LOCALAPPDATA%\SolidWorksPartMatcher and never writes
; into its own install directory.

; Fail with a clear message rather than a cryptic "invalid ArchitecturesAllowed value"
; if someone compiles this with an Inno Setup older than 6.3.0.
#if Ver < EncodeVer(6,3,0)
  #error Inno Setup 6.3.0 or later is required (x64compatible was added in 6.3.0).
#endif

#define AppName "SolidWorks Part Matcher"
#define AppPublisher "Alan"
#define AppExeName "SolidWorksPartMatcher.App.exe"

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\SolidWorksPartMatcher-v" + AppVersion
#endif

[Setup]
; Never change AppId — it is how Windows recognises an upgrade of an existing install.
AppId={{A3D2F1C6-4E7B-4C1E-9B4A-2F6D9C5E1B70}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir=..\publish
OutputBaseFilename=SolidWorksPartMatcher-Setup-v{#AppVersion}
SetupIconFile=
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

; 64-bit only, Windows 10 or later — same as the app itself.
; x64compatible requires Inno Setup 6.3.0+ (guarded above).
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

; Default to a per-user install so no admin rights are needed. Passing
; /ALLUSERS on the command line, or choosing it in the wizard, installs
; machine-wide instead.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline dialog

UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
; The whole publish tree: the single-file app exe plus the bundled viewer\
; folder (Python + OpenCASCADE). recursesubdirs+createallsubdirs preserves the
; layout the app expects.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; The app never writes into its install directory, but the .NET single-file host
; may leave an extracted-native-libraries folder behind. Remove the directory if
; anything remains so an uninstall leaves nothing.
Type: filesandordirs; Name: "{app}"
