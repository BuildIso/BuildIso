<p align="center">
  <img src="assets/logo.png" width="250">
</p>

<p align="center">
  <img src="https://img.shields.io/github/v/release/BuildIso/BuildIso">
  <img src="https://img.shields.io/github/stars/BuildIso/BuildIso?style=social">
  <img src="https://img.shields.io/github/downloads/BuildIso/BuildIso/total">
    <img src="https://img.shields.io/github/license/BuildIso/BuildIso">
  <img src="https://img.shields.io/badge/Solo%20dev-100%25">
  <img src="https://img.shields.io/github/contributors/BuildIso/BuildIso">
  <img src="https://img.shields.io/badge/Site-buildiso.com-yellow">
  <img src="https://img.shields.io/github/created-at/BuildIso/BuildIso">
  <img src="https://img.shields.io/github/last-commit/BuildIso/BuildIso">
</p>


---
# BuildIso
No way! Iso generator?
# BuildIso – A Powerful ISO Builder for OSDev
<img width="426" height="240" alt="BuildIsoDemo" src="https://github.com/user-attachments/assets/94b36683-cd5e-4fd8-bb20-7890fe9a1ef2" />

**BuildIso** is powerful ISO Generator.  
It includes a large CLI options like
```
BuildIso init
BuildIso -p  [options]

-p, --project 
-b, --boot 
-i, --iso 
-o, --output 
-V, --volume-id

--uefi          Add UEFI boot (in addition to BIOS)
--secureboot    Secure Boot note (requires --uefi)
--bios-only     BIOS-only boot
--uefi-only     UEFI-only boot

--plugins-off          Disable all plugins
-Pu, --plugin    Allow specific plugin by name

--silent         Suppress all output
--no-prompt      Skip interactive prompts
--no-spinner     Disable spinner
--no-progress    Disable progress indicators
--verbose        Verbose step output
```
init             Initialize project structure in current directory
version          Print version
help             Print this help.

 
No installers.  
Just one `executable` dropped into your project folder.

<img width="348" height="326" alt="image" src="https://github.com/user-attachments/assets/e2b30e2d-5161-475f-86de-9e21e614b088" />


---

## Features

- **Portable** – one single executable  
- **Bootable ISO generation** (El Torito, No‑Emulation)  
- **Works with any OSDev project structure**  
- **Large CLI options**
- **Plugins**

---

## Project Structure (if you double-click the exe directly)

Your OS project should look like this:

MyOS/

├── BuildIso (executable)

├── boot/

│    └── boot.bin

├── iso_root/

    └── Your Files


- `boot/boot.bin` → your 512‑byte (0x55AA) bootloader or your 2048-byte (El Torito no‑emulation) bootloader
- `iso_root/` → all files to include in the ISO (kernel, config, etc.) note: BuildIso dosen't include a LBA reader.

---

## How It Works

BuildIso:

1. Reads the project directory. 
2. Extracts the project name  
3. Recursively adds all files from `iso_root/`  
4. Loads `boot/boot.bin` as the El Torito boot image  
5. Generates a bootable ISO named:

output.iso


in the project.

---

## Requirements (if you double-click the exe directly)
 
- A valid 512‑byte or 2048-byte bootloader (`boot.bin`)  
- A populated `iso_root/` directory  

---

## License

MIT License.  
Feel free to modify, fork, and improve.

---

## Credits

Created by **srcfrcg, htmluser-hub, BuildIso**, with guidance and support from Copilot.  
Designed to be simple, portable, and OSDev‑friendly. (solo dev)

---

## Support

For support me you can donate : **https://srcfrcg.itch.io/buildiso**

---

## Next update:

Coming soon.
Planned improvements and enhancements coming soon.

---

## Contact

BuildIso@proton.me

---

## Web

[BuildIso.com](https://buildiso.com/) What is this? It's a website where you can browse and learn more. If you have any suggestions, feel free to contact me via email or share them in the discussions or issues. This site is in beta. Enjoy!

---

## SHA256 Checksum

The official SHA256 hash of the v2026.9 win64 executable (as verified on GitHub) is: sha256:7429dda0fa85bd01d0f9554d63ad4198eebf3d19135af5b3c2bef6b1cb2fed83 This checksum corresponds to the BuildIso v2026.9 win64 release file.

---

## BuildIso Pro

BuildIso Pro adds advanced features for power users.
BuildIsoPro.exe is now available it costs 4.99€ EUR Link :
https://srcfrcg.itch.io/buildisopro
BuildIso Pro is MIT

---

## Websites:

Official websites is buildiso.com, api.buildiso.com and docs.buildiso.com and install.buildiso.com and lcrawl.buildiso.com and community.buildiso.com.

---

## How to contribute

To contribute please write a issue or a discussion or a mail.

---

## Winget

> [!IMPORTANT]
> BuildIso is now in winget, type ```winget install BuildIso.BuildIso``` in your PowerShell and enjoy.

> [!WARNING]
> The official package and the only is BuildIso.BuildIso and only for Windows

---

## How to use BuildIso?

Answered in [docs answer 3](https://docs.buildiso.com/answer/3)

---

## Note plugins

Note: the plugins executed by you can contain malwares please execute dll with total confidence
