# After the starter loads

1. In r2modman, open profile **my-mod** → **Play Modded**.
2. Confirm in the BepInEx console (or `BepInEx\LogOutput.log`):
   - `GTFO-testmod is loaded!`
   - `Harmony patches applied.`
   - On the rundown screen: `Rundown page Setup patched.`
3. Pick one small behavior to change (examples: FOV, HUD toggle, log when damage is dealt).
4. Find the type/method:
   - Browse `%AppData%\r2modmanPlus-local\GTFO\profiles\my-mod\BepInEx\interop\` with ILSpy / dnSpyEx
   - Or ask in the GTFO modding Discord with the behavior you want
5. Add a new class under `Patches\`, use `[HarmonyPrefix]` or `[HarmonyPostfix]` only (avoid transpilers on GTFO IL2CPP).
6. `dotnet build`, then Play Modded again.

Game types for the rundown UI live mainly in `Modules-ASM.dll` (namespace `CellMenu`).
