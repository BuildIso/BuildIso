# BuildIso
No way! Iso generator?
# BuildIso – A Lightweight ISO Builder for OSDev
![BuildIsoDemo](https://github.com/user-attachments/assets/2c0d8960-37e3-4673-ade7-161baddb2759)

**BuildIso** is a tiny, portable, dependency‑free ISO generator designed for OS developers.  
It integrates directly into Visual Studio as an External Tool and produces a bootable ISO with a single click.

No VSIX.  
No workloads.  
No installers.  
Just one `.exe` dropped into your project folder.

<img width="348" height="326" alt="image" src="https://github.com/user-attachments/assets/38e16972-cbe0-4372-b446-fe5f5dc24759" />

---

## 🚀 Features

- **Portable** – one single executable, no dependencies  
- **Bootable ISO generation** (El Torito, No‑Emulation)  
- **Automatic project name detection**  
- **Works with any OSDev project structure**  
- **Integrates into Visual Studio External Tools**  
- **Zero configuration required**

---

## 📁 Project Structure

Your OS project should look like this:

MyOS/
├── BuildIso.exe
├── boot/
│    └── boot.bin
├── iso_root/
│    └── KERNEL.BIN
└── MyOS.csproj


- `boot/boot.bin` → your 512‑byte (0x55AA) bootloader or your 2048-byte (El Torito no‑emulation) bootloader
- `iso_root/` → all files to include in the ISO (kernel, config, etc.)

---

## 🛠️ Visual Studio Integration

1. Open **Tools → External Tools…**
2. Click **Add**
3. Fill in the fields:

- **Title:** `Build ISO`
- **Command:** `$(ProjectDir)\BuildIso.exe`
- **Arguments:** `$(ProjectDir)`
- **Initial Directory:** `$(ProjectDir)`

Click **OK**.

You now have a **Build ISO** entry in the Tools menu.

(Optional) Add it to the toolbar for one‑click builds.

---

## 📦 How It Works

BuildIso:

1. Reads the project directory passed by Visual Studio  
2. Detects the `.csproj` file  
3. Extracts the project name  
4. Recursively adds all files from `iso_root/`  
5. Loads `boot/boot.bin` as the El Torito boot image  
6. Generates a bootable ISO named:

<ProjectName>.iso


in the project root.

---

## 🧩 Requirements

- .NET 6/7/8/9/10 runtime  
- A valid 512‑byte or 2048-byte bootloader (`boot.bin`)  
- A populated `iso_root/` directory  

---

## 📜 License

MIT License.  
Feel free to modify, fork, and improve.

---

## ❤️ Credits

Created by **Srcfrcg, htmluser-hub, BuildIso**, with guidance and support from Copilot.  
Designed to be simple, portable, and OSDev‑friendly. (solo dev)

---

## 🦄 Support

For support me you can donate : **https://srcfrcg.itch.io/buildiso**

---

## 🕶️ Next update:

COMING SOON.
Planned improvements and enhancements coming soon.

---

## 📬 Contact

BuildIso@proton.me

---

## 🌍 Web

[BuildIso.com](https://buildiso.com/) What is this? It's a website where you can browse and learn more. If you have any suggestions, feel free to contact me via email or share them in the discussions or issues. This site is in beta. Enjoy!

---

## ⚙️ SHA256 Checksum

The official SHA256 hash of the v2026.5 win64 executable (as verified on GitHub) is: sha256:4ea898a52e99e61c1bc8d00a3b8aaf59d7ae0d32ee1a9f158ddf055042876813 This checksum corresponds to the BuildIso v2026.5 win64 release file.

---

## 🪙 BuildIso Pro

BuildIsoPro.exe is now available it costs 4.99€ EUR Link :
https://srcfrcg.itch.io/buildisopro
BuildIso Pro is MIT

---

## 🗣️ Websites:

Official websites is buildiso.com, api.buildiso.com and docs.buildiso.com.
