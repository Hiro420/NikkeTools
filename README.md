# NikkeTools
Tools for Reverse-Engineering Nikke

# Disclaimer
Shiftup/Level Infinite owns the original assets to the game, all credits go to its rightful owner.
I am not liable for any damages caused if you get banned from using a mod created by this tool, or its derivatives.
I DO NOT CLAIM ANY RESPONSIBILITY FOR ANY USAGE OF THIS SOFTWARE, THE SOFTWARE IS MADE 100% FOR EDUCATIONAL PURPOSES ONLY

# List of tools:
- Catalog -> Decrypts all the dbs from `AppData\LocalRow`
- CatalogV2 -> For the newer versions, decrypts and unpacks bundles from `Unity\com_proximabeta_NIKKE\com.shiftup.patch`
- MetadataDumper -> Dumps decrypted metadata from process memory
- Protobuf -> A tool to recover [protobuf](https://protobuf.dev/) definitions from the game assemblies, together with their HTTP paths
- StaticData -> Fetches AND decrypts the latest StaticData.zip (game data) from live servers
- UnLuac -> Decompiles Lua bytecode back to original code, modified for Nikke lua

## Notice
- It's recommended to use [AceMetadataUnpacker](https://github.com/Hiro420/AceMetadataUnpacker) instead of `MetadataDumper` to retreive the metadata statically

## How to use them?
- Catalog | StaticData -> Just compile and run, works straight out of the box
- CatalogV2 -> Change the `inputRoot` according to your needs and run
- MetadataDumper -> Compile the project via Visual Studio 2026. Take theput the `NikkeMetadataDumper.dll` from `Win64` generated folder, together with the files in `Gadget` folder, and put them all in game directory next to `nikke.exe`. Run the game, a decrypted `global-metadata.dat` will be generated in the game directory.
- Protobuf -> Compile the project via Visual Studio 2026. Use the generated `global-metadata.dat` to dump Il2Cpp via 
[Il2CppDumper](https://github.com/Perfare/Il2CppDumper). Then either put the `DummyDll` folder next to the `NikkeProtoDumper.exe`, or drag the DummyDll folder into the `NikkeProtoDumper.exe`. `Nikke.proto` will be generated, in `proto3` format.
- UnLuac -> Run `compile.bat` to compile the project. Then put all the `.lua.bytes` files you got from [YarikStudio](https://github.com/yarik0chka/YarikStudio) or [AssetStudio](https://github.com/RazTools/Studio) into the `Lua` folder. Run the `DecompileAll.py` python script, and all the decompiled luas will be generated in `Decompiled` folder.

Copyright© Hiro420