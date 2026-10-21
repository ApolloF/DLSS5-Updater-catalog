> _**ATM the only games not working are Anti-Cheat enabled, with some exceptions.**_  
> 
> _Last updated – 20 October 2026_

<!--
TEMPLATE FOR NEW ENTRIES
| GAME NAME | ✅/❌/➖ | DLSS/FSR3.1/XeSS | ✨ (if OptiPatcher supported, otherwise empty) | Notes go here | [#](url) |
-->

| Game | Compatibility | Upscaler <br>Inputs | OptiPatcher <br>Support | Notes | Images | 
| ---- | :-----------: | :-----------------: | :------------------------------------------------------------------: | ----- | :----: |
| [007 First Light](007-First-Light) | ✅ | DLSS, FSR3.1/4 |  | FSR3.1 inputs are now supported with PT update, check compat entry for more info | [1](https://github.com/user-attachments/assets/ba83a9cf-c23c-4bd4-a409-1c4c2a7c7ab8) [2](https://github.com/user-attachments/assets/c81207df-8647-482a-a917-3a2928355cad) |
| 1666: Amsterdam | ✅ | DLSS, FSR3.1/4, XeSS | ✨ | Using FSR4 through XeSS or DLSS inputs requires enabling `Non-Linear Color Space` to avoid black screens and flickering. | [1](https://github.com/user-attachments/assets/228077bc-e372-4642-bb06-5a61a19f2971) [2](https://github.com/user-attachments/assets/b420b796-7526-4e4c-986d-232285ae8d72) [3](https://github.com/user-attachments/assets/f4110d2c-814c-4ce1-ad4a-c20dd3e71cdb) |
| [171](171) | ✅ | DLSS, FSR3 | ✨ |  |  |
| [7 Days To Die](7-Days-to-Die) | ✅ | DLSS |  | Make sure **EAC** is disabled. | [1](https://github.com/user-attachments/assets/da4e6e92-cb3b-47cb-b6fd-ffd0748ee0f5), [2](https://github.com/user-attachments/assets/c3e6435d-2aa0-4295-abe6-e376618a467c), [3](https://github.com/user-attachments/assets/15136419-14f1-4b1f-8d14-09b55e63380f) <br>🐧[1](https://github.com/user-attachments/assets/40f725dc-81e2-472b-96ae-821fc7c32384) |
| 63 Days | ✅ | XeSS |  |  | [1](https://github.com/user-attachments/assets/54fdc2bc-1839-4e72-9963-25b1f0ddf289) [2](https://github.com/user-attachments/assets/9f35beac-c5c4-4d8f-af8d-3ddd8a9c4cdf) |
| 687 Days on Mars Demo | ✅ | DLSS |  |  | [1](https://github.com/user-attachments/assets/5ec601d7-18bf-4435-b61b-8ff40a9e1125) |
| Atlas Fallen: Reign Of Sand | ❌ |  |  | OptiScaler cannot hook this game's FSR2 inputs in DX12 or Vulkan. |
| [Dead Space (2023)](Dead-Space-(2023)) | ✅ | DLSS, FSR2 |  | Disable Motion Blur to fix ghosting around Isaac’s head. | [1](https://github.com/user-attachments/assets/f08c5e9f-64b6-492e-a90c-ce47f7ea9bc6) [2](https://github.com/user-attachments/assets/ab005b49-d20c-49ba-bedc-ec31e1fc93a5) [3](https://github.com/user-attachments/assets/e621d0f5-4273-4160-88e1-b0a776f94180) <br>🐧[1](https://github.com/user-attachments/assets/6a0fe85d-9d4c-45d1-a047-d7273f61b7f7) |
| Gears of War: Reloaded | ❌ |  |  | EAC. Game has FSR3.1, but no FSR dll. |  |
| [Minecraft RTX](Minecraft-RTX) | ➖ | DLSS |  | Windows: ✅. Incompatible with Linux due to no Wine lacking support for UWP games. | [1](https://github.com/user-attachments/assets/72ebc1d0-cea1-400f-ab17-b9d6afdc6a97) [2](https://github.com/user-attachments/assets/d77f53c9-65c3-49de-aea5-0d754bb75e46) |
| Wolfenstein: Youngblood | ➖ | DLSS |  | Linux: ✅. Incompatible with AMD/Intel on Windows as DLSS and RT options cannot be exposed, Nvidia should be fine. |  |

---

## Upscaler mods support

Separate compatibility table for tested games that received **3rd party upscaler mods** which added support for **DLSS/FSR/XeSS**.
<!--
TEMPLATE FOR NEW ENTRIES
| GAME NAME | ✔️/❌ | ✅/⛔ | DLSS/FSR2/XeSS |  | Notes go here | [#](url) |
-->

| Game | Compatibility | Upscaler <br>Inputs | Notes  | Images | 
| ---- | :-----------: | :-----------------: | ------ | :----: |
| [Assassin's Creed Origins](Assassins-Creed-Origins) | ✅ | DLSS | Requires using a DLSS mod and carefully reading the compatibility entry | [1](https://github.com/user-attachments/assets/86e27236-082f-41fc-aa9d-40070034a4b4) [2](https://github.com/user-attachments/assets/74ae2be2-da7e-4d43-ba3f-a6e5963ec1ca) |
| [Devil May Cry 5](Devil-May-Cry-5) | ✅ | DLSS | Requires REFramework PDUpscaler branch mod + PDPerfPlugin mod and carefully reading the [compatibility entry](https://github.com/optiscaler/OptiScaler/wiki/Devil-May-Cry-5) | [1](https://github.com/user-attachments/assets/c34be097-db8b-4dc8-bf3f-bc4e9fd57359) |
| [Dishonored 2](Dishonored-2) | ✅ | DLSS | Requires Luma Framework mod and carefully reading the [compatibility entry](https://github.com/optiscaler/OptiScaler/wiki/Dishonored-2) | [1](https://github.com/user-attachments/assets/5e362326-8b54-4bfb-b430-f10843a49778) |
| [Elden Ring](Elden-Ring-(ERSS‐FG)) | ✅ | FSR3.1, XeSS | Use ERSS-FG mod as basis for Upscaling and FG. | [1](https://github.com/user-attachments/assets/a8147540-e90a-4a32-97fb-40e31c8e6b55) [2](https://github.com/user-attachments/assets/d717906a-36a4-4155-8aa5-411267c7586a) |
| [Far Cry 5](Far-Cry-5) | ✅ | DLSS | Requires Luma Framework mod and carefully reading the compatibility entry | [1](https://github.com/user-attachments/assets/57ec637b-cc54-422e-bff0-7c343b830d3c) [2](https://github.com/user-attachments/assets/c3b2d4f6-2b5a-45de-b894-a7e52e339a05) |
| [FINAL FANTASY VII REMAKE INTERGRADE](FINAL-FANTASY-VII-REMAKE-INTERGRADE) | ✅ | DLSS | Requires FF7 Remake Luma mod, requires running the game in DX11 mode - add `-dx11` launch command | [1](https://github.com/user-attachments/assets/84ef81eb-7d89-45eb-b694-7abe239d40de) [2](https://github.com/user-attachments/assets/0042c503-4bf3-4300-b8e1-c9e73e858543) |

## Luma Unreal Engine

<!--
TEMPLATE FOR NEW ENTRIES
| GAME NAME | ✔️/❌ | ✅/⛔ | DLSS/FSR2/XeSS |  | Notes go here | [#](url) |
-->

| Game | Compatibility | Upscaler <br>Inputs | Notes  | Images | 
| ---- | :-----------: | :-----------------: | ------ | :----: |
| [Aliens: Fireteam Elite](Luma-Unreal-Engine-Luma-UE) | ✅ | DLSS |  | [1](https://github.com/user-attachments/assets/f40f5b22-2bfc-4812-b8d1-eb10768a4ef1) |
| [Bloodstained: Ritual of the Night](Luma-Unreal-Engine-Luma-UE) | ✅ | DLSS | Disable `Frame Smoothing` in game settings to unlock FPS | [1](https://github.com/user-attachments/assets/978f8501-cbea-4a71-985a-816bb88702b5) |
