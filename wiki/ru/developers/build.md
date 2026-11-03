---
pageId: developers/build
contentRevision: 1
---

# Сборка проекта

Применимость: Rimlink 0.8.1 · RimWorld 1.7 · протокол 1.

## Как пользоваться {#usage}

Установи SDK из global.json; версии пакетов централизованы в Directory.Packages.props. Из корня выполни:

`dotnet build Rimlink.sln`

`dotnet test Rimlink.sln -nologo -v q`

Мод не включён в solution: закрой игру и используй PowerShell scripts/Build-ClientMod.ps1 -RimWorldDir &lt;game&gt; -InstallTo &lt;game/Mods&gt;. -BundleHarmony допустим лишь для локального запуска без отдельного Harmony, не релиза. scripts/Package-Release.ps1 собирает release ZIP.

Для wiki: npm --prefix wiki ci --ignore-scripts, затем run test:checks, check:content, check:reference, build, check:links. Node 24 закреплён отдельно. Для BridgeTools нужен отдельный Build-BridgeTools.ps1 и установленная игра.

## Ограничения и безопасность {#limits}

Не заменяй DLL работающей игры и не перезапускай чужой экземпляр. Успешный solution build не доказывает сборку мода и игру. Не коммить bin/obj/data/сейвы/секреты и generated wiki output.

## См. также {#related}

- [Тестирование и приёмка](./testing.md)
- [Вклад и сопровождение wiki](./contributing.md)
- [Размещение и запуск](../admins/hosting.md)
