; Установщик Веры (Inno Setup 6). Собирается в CI: .github/workflows/desktop.yml
;   ISCC.exe /DMyAppVersion=2.0.0 /DPublishDir=<папка dotnet publish> /O<куда положить> vera.iss
; Файл сохранён в UTF-8 с BOM — так Inno Setup правильно читает кириллицу.

#ifndef MyAppVersion
  #define MyAppVersion "2.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish"
#endif

#define MyAppName "Вера"
#define MyAppExe "Vera.exe"
#define MyAppPublisher "Vera Assistant"

[Setup]
AppId={{213BA9BB-9B12-59E7-95C5-D5E1F90FF4ED}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppComments=Чиби ИИ-ассистент для Windows
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
VersionInfoDescription=Установщик Веры
; установка для текущего пользователя — без прав администратора
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\Vera
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputBaseFilename=Vera-Setup-{#MyAppVersion}
SetupIconFile=..\..\build\icon.ico
UninstallDisplayIcon={app}\{#MyAppExe}
UninstallDisplayName={#MyAppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
; закрыть запущенную Веру перед обновлением/удалением (имя мьютекса — из SingleInstance.cs)
AppMutex=VeraAssistantSingleInstance
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "autostart"; Description: "Запускать Веру вместе с Windows"; GroupDescription: "Дополнительно:"

[Files]
Source: "{#PublishDir}\*"; Excludes: "*.pdb"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Comment: "Чиби ИИ-ассистент"
Name: "{group}\Удалить Веру"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Registry]
; то же значение пишет сама Вера (Настройки → Система → Запускать вместе с Windows)
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Vera"; \
    ValueData: """{app}\{#MyAppExe}"" --hidden"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; настройки и ключи в %APPDATA%\VeraAssistant не удаляем — вдруг Веру переустановят
Type: files; Name: "{app}\*.log"
