# 🎧 VolumeMixer Tray App

A lightweight Windows tray utility that restores the **classic Volume Mixer (SndVol.exe)** with modern enhancements.  
Opens instantly from the tray, positions itself on the correct monitor, and closes automatically when you move away.  
Includes **theme-aware tray icons**, **auto monitor detection**, and **optional auto-start setup**.

---

## ⭐ Key Features

- Runs quietly in the **system tray**
- **Left-click** → Opens the classic Volume Mixer
- **Right-click** → Context menu with settings
- Auto-detects the **current monitor**
- Positions SndVol.exe at **bottom-right** of the active screen
- Auto-closes when mouse moves away (custom distance)
- **Theme-aware icons** (auto, light, dark)
- Portable — all files in one folder, no installer
- Compiles easily using the included **Build.bat**

---

## 🎨 Theme & Icon Handling

The app includes **light and dark tray icons**:

- Windows in **dark mode** → App shows **light icon**
- Windows in **light mode** → App shows **dark icon**

You may manually override via tray menu:

- Auto (follow Windows)
- Light icon
- Dark icon

---

## 🖥 Monitor-Aware Mixer Positioning

- Detects which screen the mouse is on  
- Opens the classic mixer on that same screen  
- Anchors to **bottom-right corner** (adjusted for taskbar position)  
- If `-t` parameter is ignored by Windows, the app force-moves the window

---

## 🛠 How to Build (No Visual Studio Required)

Place these files together in the same folder:

```

Vol-Mixer-Tray.cs
Build.bat
volmixer.ico
volmixer_black.ico
batch_g2_VolMixer.ico

```

Then run:

```

Build.bat

```

This generates:

```

VolMixerTray.exe

```

---

## 📂 Project Structure

```

VolumeMixer_TrayApp/
│
├── Vol-Mixer-Tray.cs          # Main source code
├── Build.bat                  # Build script (csc.exe)
├── volmixer.ico               # Dark theme tray icon
├── volmixer_black.ico         # Light theme tray icon
├── batch_g2_VolMixer.ico      # Executable icon
└── README.md

```

---

# ▶ Auto-Start the Application (Optional)

You can configure VolumeMixer Tray App to run automatically on Windows startup.

Choose one of the following options depending on whether you want auto-start for **all users** or only **your current user**.

---

## 🟦 Auto-Start for ALL Users  
*(recommended for shared computers)*

Place **either the `.exe` or a shortcut** in:

```

C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup

```

✔ Launches for **every user**  
⚠ Requires administrator permissions  

---

## 🟩 Auto-Start for Current User Only  
*(no admin rights required)*

Place a shortcut in your personal Startup folder:

```

%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup

````

✔ Runs every time *you* log in  
✔ No admin permissions needed  

---

### Notes

- The app runs quietly in the **system tray**
- Uses extremely low system resources  
- Compatible with **Windows 10 and Windows 11**

---

## 📦 Included Build Script (`Build.bat`)

```bat
@echo off
setlocal

REM Build script for VolumeMixer Tray App (portable compiler)

set CSC="%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

%CSC% ^
 /nologo /target:winexe /optimize+ ^
 /win32icon:"batch_g2_VolMixer.ico" ^
 /resource:"volmixer.ico",VolMixerTray.Icons.Dark.ico ^
 /resource:"volmixer_black.ico",VolMixerTray.Icons.Light.ico ^
 /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
 Vol-Mixer-Tray.cs

echo.
echo Build complete! If no errors were shown, VolMixerTray.exe is ready.
echo.
pause
````

---

## 📄 License

Licensed under the **MIT License**.
You may modify, distribute, and use this software freely.

---

## 🙌 Contributions & Feedback

Issues and pull requests are welcome!
Feel free to suggest features, improve code, or submit translations.
