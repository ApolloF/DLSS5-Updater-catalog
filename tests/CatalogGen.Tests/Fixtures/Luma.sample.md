# Completed Mods
| Name | Author | Download Link |   Status   | Special Notes | Features |
|------|--------|---------------|------------|---------------|----------|
| Alfred Hitchcock – Vertigo | Pumbo | [![GitHub Unity 64-bit][gh-unity]](https://github.com/Filoppi/Luma-Framework/releases/latest/download/Luma-Unity_Engine.zip) | ✅ | | |
| BioShock 2 Remastered | Pumbo | [![GitHub][gh]](https://github.com/Filoppi/Luma-Framework/releases/latest/download/Luma-BioShock_Series-x32.zip) | ✅ | | [📌](https://github.com/Filoppi/Luma-Framework/wiki#bioshock-2-remastered) |
| Borderlands 2 | DristoforColumb | [![Nexus Mods][nexus]](https://www.nexusmods.com/borderlands2/mods/658) | ✅ | Requires **dgVoodoo2 v2.81.3** or **dgVoodoo2 v2.87.3**<details><summary>dgVoodoo2 setup</summary>Set these in `dgVoodoo.conf`:<br>**[General]**<br>• `OutputAPI`: `d3d11_fl11_0`<br>**[DirectX]**<br>• `VideoCard`: `geforce_9800_gt` *(fixes dynamic shadows)*<br>• `dgVoodooWatermark`: `false` *(removes the dgVoodoo watermark)*<br>• `VRAM`: `1024`</details> | [📌](https://github.com/Filoppi/Luma-Framework/wiki#borderlands-2) |
| Borderlands GOTY Enhanced | DristoforColumb | [![Nexus Mods][nexus]](https://www.nexusmods.com/borderlandsgotyenhanced/mods/103) | ✅ | | [📌](https://github.com/Filoppi/Luma-Framework/wiki#borderlands-goty-enhanced) |
| Call of Duty: Black Ops III | XgarhontX | [![GitHub][gh]](https://github.com/Filoppi/Luma-Framework/releases/latest/download/Luma-Call_of_Duty_Black_Ops_3.zip)<br>[![Nexus Mods][nexus]](https://www.nexusmods.com/callofdutyblackops3/mods/28) | ✅ | You may get banned on official public matchmaking.<br>Avoid online/public modes. | [📌](https://github.com/Filoppi/Luma-Framework/wiki#call-of-duty-black-ops-iii) |
| Far Cry 5 | HenDGS | [![GitHub][gh]](https://github.com/HenDGS/fc5-dlss/releases/latest)<br>[![Nexus Mods][nexus]](https://www.nexusmods.com/farcry5/mods/258) | ✅ | DLSS/DLAA; borderless native scRGB HDR support. RenoDX is optional. | |
| Mass Effect Legendary Edition | DristoforColumb | [![Nexus Mods][nexus]](https://www.nexusmods.com/masseffectlegendaryedition/mods/3413) | ✅ | | [📌](https://github.com/Filoppi/Luma-Framework/wiki#mass-effect-legendary-edition) |
| Prey | Pumbo | [![GitHub][gh]](https://github.com/Filoppi/Luma-Framework/releases/latest/download/Luma-Prey.zip)<br>[![Nexus Mods][nexus]](https://www.nexusmods.com/prey2017/mods/149) | ✅➕ | | [📌](https://github.com/Filoppi/Luma-Framework/wiki#prey) |
| Batman Arkham Knight |  |  | 🚧 | DLSS only (no HDR atm) | |

# WIP Mods

| Name | Author | Download Link | Status | Special Notes | Features |
|--|--|--|--|--|--|
| Not Listed Game | Someone | | 🚧 | | |

# Unreal Engine
[Download](https://github.com/Filoppi/Luma-Framework/releases/latest/download/Luma-Unreal_Engine.zip)

ℹ️ **Generic mod that replaces default UE4 TAA implementation with DLSS (DLAA) or FSR3, and Native HDR, at the moment in requires DX11 (it might work in UE5 games too).**

💻 Setting changes listed in Notes may require 3rd party tools such as UUU or UE4SS to activate console or change/add to Engine.ini

| Name | DLSS/FSR | HDR | Notes | UE version |
|------|--------|-------|-------|-------|
| Abiotic Factor  | ✅ |   | -DX11 launch argument | 5.4.4.0 |
| Abzu | ⛔ | ✅ |  | 4.12.5.0 |
| Ace Combat 7: Skies Unknown | ✅ |   | In-game: AA "FXAA High" <details><summary>Modify Engine.ini</summary>`[SystemSettings]`<br>`r.DefaultFeature.AntiAliasing=2`<br>`r.PostProcessAAQuality=4`</details> | 4.18 |
| Biomutant | ✅ | ⛔  | In-game: AA "High" or "Max" | 4.18.0.0 |
| Daymare: 1998 | ✅ |   | <details><summary>Notes</summary>Works perfectly on its own but crashes if combined with OptiScaler or UUU.</details> | 4.20.2.0 |
| Life is Strange: True Colors | 🚧 |   | Use [Luma build 189](https://github.com/Filoppi/Luma-Framework/releases/download/latest-189/Luma-Unreal_Engine.zip). Latest builds do not work. Having RTSS (and similar programs) open prior to game's launch may break the mod. Launch using `-dx11` parameter. In-game: Antialiasing "Very High" or "Cinematic" | 4.25.0.0 |
| Little Nightmares Enhanced Edition |  | ✅ | -DX11 launch argument. The original works. Remastered unknown. | 4.27.2 |
